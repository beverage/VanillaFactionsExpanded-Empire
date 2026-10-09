using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VFEEmpire;

public enum ConspiracyAnswer
{
    Traitors,
    Deal,
    Truce
}

//How a conspirator fights, decided once for the plot: GameComponent_Conspiracy.HowFights
public enum ConspiratorFights
{
    Undecided,
    HandToHand,
    Shooting
}

//The Brutus: during the parade's deserter raid, one colonist per stellarch turns on her, with allies. The plot runs here
//rather than in the parade because it plays out whatever happens to the parade, and so does everything it leaves behind:
//conspirators guilty for good, truces that hold until a colonist kills one of them, and the fights its answers start
public class GameComponent_Conspiracy : GameComponent
{
    //Each conspirator takes up position for the way it fights best, close enough to touch the one it strikes first or with
    //a clear shot at them from no further than this, and the Brutus in position is the signal for all of them
    public const float ShootingDistance = 5f;

    //Once the Brutus is in position the strike waits this long at most for the allies that fight hand to hand, and comes the
    //moment every one of them is in touch of its mark: about four seconds at normal speed, long enough for an ally a few
    //cells off to arrive, and short of what it takes to run down a guard far off
    private const int AllyGraceTicks = 250;

    //The strike waits until no enemy is near her, but for no more than an hour once the Brutus is in position, and comes
    //two hours into the approach whatever happens
    private const float EnemyNearRadius = 12f;
    private const int MaxWaitForEnemiesTicks = GenDate.TicksPerHour;
    private const int MaxApproachTicks = 2 * GenDate.TicksPerHour;

    //As a delegation's cell gathers on its leader, the allies first gather on the Brutus, in the Brutus's room and within
    //this of the Brutus, before each goes for the one it strikes first. A straggler holds up the gathering for half an hour
    //at most
    private const float GroupRadius = 5f;
    private const int StragglerWaitTicks = 1250;

    //Who turns: one of her nobles, whatever they think of her, as Brutus loved Caesar: the rival most likely, then any other
    //titled colonist. With no noble free to (all of them in her guard, say), anyone else, again whatever they think of her,
    //the holders of Ideology roles most likely
    private const float RivalWeight = 10f;
    private const float TitledWeight = 4f;
    private const float RoleWeight = 2f;

    //The line vanilla draws for its own thoughts about a death, here between the colonists who love her and the rest. A truce
    //moves the mood of colonists whose opinion of her was at least this strong either way; nobody who loves her joins
    //the Brutus against her; and the plotters of a failed plot weigh on those who love her
    private const int TruceOpinionThreshold = 20;

    //How long a failed plot's plotters weigh on those who love her while they live in the colony: a year, as the truce's mood
    private const int TraitorsAmongUsTicks = 60 * GenDate.TicksPerDay;

    public static GameComponent_Conspiracy Instance;

    public Conspiracy plot;
    private List<string> betrayedStellarchs = new();
    private List<Pawn> guiltyForGood = new();
    private List<Truce> truces = new();
    private List<Pawn> turncoats = new();
    private List<Pawn> bystanders = new();
    private List<FoiledPlot> foiled = new();

    //Not saved
    private Dialog_NodeTree decisionWindow;
    private bool releasingSuccession;
    private Pawn killedByPlot;

    public GameComponent_Conspiracy(Game game) => Instance = this;

    //The colonists the answers sent over to the deserters fight to the last, whatever titles the colony has left
    public bool HoldsDeserters => turncoats.Any(Fighting);

    public bool PlotUnderWayFor(Pawn stellarch) => plot != null && plot.stellarch == stellarch;

    public bool IsConspirator(Pawn pawn) => plot != null && pawn != null && (plot.brutus == pawn || plot.allies.Contains(pawn));

    public bool IsStrikeTarget(Pawn pawn) => plot is { striking: true } && (pawn == plot.stellarch || plot.guards.Contains(pawn));

    public bool KilledByPlot(Pawn pawn) => pawn != null && pawn == killedByPlot;

    //Dev mode, from the ascension console: the conspirators strike now, wherever they are
    public void DebugStrike()
    {
        if (plot is { striking: false, decisionPending: false }) Strike();
    }

    private static bool Fighting(Pawn pawn) => pawn.Spawned && !pawn.Dead && !pawn.Downed && !pawn.IsPrisoner && pawn.HostileTo(Faction.OfPlayer);

    private static bool InPlot(Pawn pawn) =>
        !pawn.Dead && !pawn.Downed && pawn.MentalState is MentalState_ConspiratorApproach or MentalState_ConspiratorStrike;

    public override void GameComponentTick()
    {
        var ticks = Find.TickManager.TicksGame;
        //Every tick while any conspirator that fights hand to hand closes in, since it may be in touch of a mark on the move
        //for a moment only
        if (plot is { decisionPending: false } && (ticks % 30 == 0 || plot is { gathered: true, striking: false } && plot.Conspirators.Any(FightsHandToHand)))
            UpdatePlot();
        if (ticks % 250 == 0) UpdateFights();
        if (ticks % GenDate.TicksPerHour == 0) RenewGuilt();
    }

    public override void GameComponentUpdate()
    {
        if (plot is { decisionPending: true } && Current.ProgramState == ProgramState.Playing
         && (decisionWindow == null || !Find.WindowStack.IsOpen(decisionWindow)))
            OpenDecision();
    }

    //The parade calls this an hour to three after its deserter raid arrives
    public bool TryBeginPlot(LordJob_Parade parade)
    {
        var stellarch = parade.stellarch;
        if (plot != null || !MayPlotAgainst(stellarch)) return false;
        //The Brutus may love her, so there is a plot whenever anyone outside her honor guard is fit to strike; the allies are
        //only ever candidates, who do not
        var eligible = Eligible(parade);
        if (eligible.Count == 0) return false;
        var candidates = eligible.Where(pawn => IsCandidate(pawn, stellarch)).ToList();

        var nobles = eligible.Where(IsNoble).ToList();
        var rival = nobles.OrderByDescending(pawn => pawn.royalty.MostSeniorTitle.def.seniority)
           .ThenByDescending(pawn => pawn.HasHonors() ? pawn.Honors().Honors.Count() : 0)
           .FirstOrDefault();
        var brutus = nobles.Count > 0
            ? nobles.RandomElementByWeight(pawn => pawn == rival ? RivalWeight : TitledWeight)
            : eligible.RandomElementByWeight(pawn => ModsConfig.IdeologyActive && pawn.Ideo?.GetRole(pawn) != null ? RoleWeight : 1f);

        //As many allies as she has guards, one at most on the Normal difficulty: those who dislike her most, then those who
        //like the Brutus most, then the rest, coolest towards her first
        var guards = StandingGuards(parade);
        var difficulty = DifficultyFor(stellarch);
        var count = AlliesWanted(guards.Count, difficulty);
        var others = candidates.Where(pawn => pawn != brutus)
           .Select(pawn => (pawn, ofHer: pawn.relations.OpinionOf(stellarch), ofBrutus: pawn.relations.OpinionOf(brutus))).ToList();
        var allies = others.Where(other => other.ofHer < 0).OrderBy(other => other.ofHer)
           .Concat(others.Where(other => other.ofHer >= 0 && other.ofBrutus > 0).OrderByDescending(other => other.ofBrutus))
           .Concat(others.Where(other => other.ofHer >= 0 && other.ofBrutus <= 0).OrderBy(other => other.ofHer))
           .Select(other => other.pawn)
           .Take(count)
           .ToList();

        plot = new Conspiracy
        {
            stellarch = stellarch,
            brutus = brutus,
            allies = allies,
            guards = guards,
            map = parade.Map,
            startTick = Find.TickManager.TicksGame,
            difficulty = difficulty
        };
        foreach (var pawn in plot.Conspirators) plot.fights[pawn] = HowFights(pawn);
        plot.fallbackHeir = FallbackHeir(plot);
        plot.RecordOpinions();
        betrayedStellarchs.Add(stellarch.GetUniqueLoadID());
        foreach (var pawn in plot.Conspirators)
        {
            GiveTrait(pawn, VFEE_DefOf.VFEE_Conspirator);
            StartState(pawn, VFEE_DefOf.VFEE_ConspiratorApproach);
            Messages.Message("VFEE.Conspiracy.LeftPost".Translate(pawn.Named("PAWN"), stellarch.Named("STELLARCH")), pawn, MessageTypeDefOf.NeutralEvent);
        }

        return true;
    }

    //How many colonists a plot begun now would take from her defence, the Brutus and its allies, for the parade's deserter
    //raid, which is sized by the defenders the plot leaves (QuestPart_DeserterRaid). The Brutus is drawn at random, and one
    //who loves her leaves every candidate free to be an ally, so where the draw allows it this counts that larger plot. A
    //plot already under way counts those still in it
    public int PlottersFor(LordJob_Parade parade)
    {
        var stellarch = parade.stellarch;
        if (plot != null) return plot.stellarch == stellarch ? plot.Conspirators.Count(InPlot) : 0;
        if (!MayPlotAgainst(stellarch)) return 0;
        var eligible = Eligible(parade);
        if (eligible.Count == 0) return 0;
        var candidates = eligible.Count(pawn => IsCandidate(pawn, stellarch));
        var nobles = eligible.Where(IsNoble).ToList();
        var brutusMayLoveHer = (nobles.Count > 0 ? nobles : eligible).Any(pawn => !IsCandidate(pawn, stellarch));
        var alliesWanted = AlliesWanted(StandingGuards(parade).Count, DifficultyFor(stellarch));
        return 1 + System.Math.Min(alliesWanted, brutusMayLoveHer ? candidates : candidates - 1);
    }

    //Under any storyteller: the deserter raid that covers the plot comes with violent quests off too
    private bool MayPlotAgainst(Pawn stellarch) =>
        stellarch != null && !stellarch.Dead && !betrayedStellarchs.Contains(stellarch.GetUniqueLoadID());

    //The difficulty chosen for her ascension. One under a storyteller that allows no violent quests is never asked, and its
    //plot takes Normal's rules, the gentler ones
    private static AscensionDifficulty DifficultyFor(Pawn stellarch) => QuestPart_AscensionDifficulty.For(stellarch,
        Find.Storyteller.difficulty.allowViolentQuests ? AscensionDifficulty.LosingIsFun : AscensionDifficulty.Normal);

    private static List<Pawn> Eligible(LordJob_Parade parade) =>
        parade.Map.mapPawns.FreeColonistsSpawned.Where(pawn => CanConspire(pawn, parade)).ToList();

    private static bool IsCandidate(Pawn pawn, Pawn stellarch) => pawn.relations.OpinionOf(stellarch) < TruceOpinionThreshold;

    private static bool IsNoble(Pawn pawn) => pawn.royalty?.MostSeniorTitle != null;

    private static List<Pawn> StandingGuards(LordJob_Parade parade) =>
        parade.guards.Where(guard => guard.Spawned && !guard.Dead && !guard.Downed).ToList();

    //As many allies as she has guards, one at most on the Normal difficulty
    private static int AlliesWanted(int guards, AscensionDifficulty difficulty) =>
        difficulty == AscensionDifficulty.Normal ? System.Math.Min(guards, QuestPart_AscensionDifficulty.NormalAllies) : guards;

    private static bool CanConspire(Pawn pawn, LordJob_Parade parade) =>
        pawn != parade.stellarch && !parade.guards.Contains(pawn) && pawn.IsFreeNonSlaveColonist && !pawn.IsQuestLodger() && !pawn.IsMutant
     && pawn.DevelopmentalStage.Adult() && !pawn.Downed && !pawn.InMentalState && !pawn.WorkTagIsDisabled(WorkTags.Violent)
     && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);

    //Vanilla's second choice of heir, the colonist she likes most, taken while she is alive to like anyone. It is only
    //used when her own heir turns out to be a conspirator.
    private static Pawn FallbackHeir(Conspiracy conspiracy) =>
        PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists
           .Where(pawn => pawn != conspiracy.stellarch && !conspiracy.Conspirators.Contains(pawn))
           .MaxByWithFallback(pawn => conspiracy.stellarch.relations.OpinionOf(pawn));

    private void UpdatePlot()
    {
        var stellarch = plot.stellarch;
        var now = Find.TickManager.TicksGame;
        if (stellarch.Dead)
        {
            //Normally the royalty tracker hears of her death first; this covers a death that bypassed it
            plot.decisionPending = true;
            return;
        }

        if (!InPlot(plot.brutus) || stellarch.MapHeld != plot.map)
        {
            PlotFails();
            return;
        }

        if (plot.striking) return;
        if (!plot.gathered)
        {
            if (!Together() && now - plot.startTick < StragglerWaitTicks) return;
            plot.gathered = true;
        }

        //The Brutus in position is the signal. The allies close in on their own marks meanwhile: the strike waits a moment
        //for those that fight hand to hand to be in touch of theirs (AllyGraceTicks), and the rest strike from wherever the
        //signal finds them
        var brutus = plot.brutus;
        var inPosition = brutus.MentalState is MentalState_ConspiratorApproach && InPosition(brutus, MarkFor(brutus));
        if (inPosition && plot.inPositionSinceTick < 0) plot.inPositionSinceTick = now;
        var inPositionFor = plot.inPositionSinceTick < 0 ? 0 : now - plot.inPositionSinceTick;
        if ((inPosition && (inPositionFor >= AllyGraceTicks || AlliesInTouch()) && (!EnemyNear(stellarch) || inPositionFor >= MaxWaitForEnemiesTicks))
         || now - plot.startTick >= MaxApproachTicks)
            Strike();
    }

    //Every ally still closing in that fights hand to hand is in touch of its mark: one down, away or shooting holds nothing up
    private bool AlliesInTouch() => plot.allies.All(ally => ally.MentalState is not MentalState_ConspiratorApproach || ally.Downed
        || !ally.Spawned || ally.Map != plot.map || !FightsHandToHand(ally) || InPosition(ally, MarkFor(ally)));

    //Whom a conspirator strikes first, and so stands ready by until the strike. The Brutus: her on Losing is fun, when there
    //is a way to her; otherwise the guard nearest her when it sets off. An ally: one of her guards, spread across them, the
    //one the fewest are marked for and then the nearest. Each keeps its mark while that guard stands, and her once none does
    public Pawn MarkFor(Pawn pawn)
    {
        if (!IsConspirator(pawn)) return null;
        if (GoesStraightForHer(pawn)) return plot.stellarch;
        if (plot.marks.TryGetValue(pawn, out var kept) && kept != null && kept != plot.stellarch && plot.guards.Contains(kept) && Standing(kept, pawn))
            return kept;
        var standing = plot.guards.Where(guard => Standing(guard, pawn)).ToList();
        Pawn mark;
        if (standing.Count == 0) mark = plot.stellarch;
        else if (pawn == plot.brutus)
        {
            var her = plot.stellarch.PositionHeld;
            mark = standing.OrderBy(guard => guard.Position.DistanceToSquared(her)).First();
        }
        else
            mark = standing.OrderBy(guard => plot.marks.Count(pair => pair.Key != pawn && pair.Value == guard && InPlot(pair.Key)))
               .ThenBy(guard => guard.Position.DistanceToSquared(pawn.Position))
               .First();
        return plot.marks[pawn] = mark;
    }

    //In position for the way a conspirator fights: close enough to touch its mark, or near enough to it with a clear shot
    public bool InPosition(Pawn pawn, Pawn mark)
    {
        var spot = mark.SpawnedParentOrMe;
        if (!pawn.Spawned || spot == null || spot.Map != pawn.Map) return false;
        if (FightsHandToHand(pawn)) return pawn.CanReachImmediate(spot, PathEndMode.Touch);
        var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
        return verb != null && pawn.Position.InHorDistOf(spot.Position, ShootingDistanceFor(verb)) && verb.CanHitTarget(spot);
    }

    //Up close, unless the gun cannot fire at that range
    public static float ShootingDistanceFor(Verb verb) => System.Math.Max(ShootingDistance, verb.verbProps.minRange + 1f);

    //Every conspirator fights whichever way it does more damage (HowFights), decided once
    public bool FightsHandToHand(Pawn pawn)
    {
        if (!IsConspirator(pawn)) return false;
        if (!plot.fights.TryGetValue(pawn, out var fights) || fights == ConspiratorFights.Undecided) plot.fights[pawn] = fights = HowFights(pawn);
        return fights == ConspiratorFights.HandToHand;
    }

    //The damage a conspirator does in a second hand to hand, by the game's own melee damage stat, against what it does
    //shooting
    private static ConspiratorFights HowFights(Pawn pawn) =>
        ShootingDamagePerSecond(pawn) > pawn.GetStatValue(StatDefOf.MeleeDPS) ? ConspiratorFights.Shooting : ConspiratorFights.HandToHand;

    //What a pawn's gun does in a second from up close: its burst, at the pawn's chance to hit from there, over the burst's
    //full cycle of aiming, firing and cooling down. Nothing without a gun, or when the pawn's apparel stops it shooting
    private static float ShootingDamagePerSecond(Pawn pawn)
    {
        var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
        if (verb is not Verb_LaunchProjectile launcher || verb.IsMeleeAttack || !verb.Available() || verb.ApparelPreventsShooting()) return 0f;
        var projectile = launcher.Projectile?.projectile;
        if (projectile?.damageDef == null || !projectile.damageDef.harmsHealth) return 0f;
        var weapon = verb.EquipmentSource;
        var distance = ShootingDistanceFor(verb);
        var hitChance = (verb.verbProps.canGoWild ? ShotReport.HitFactorFromShooter(pawn, distance) : 1f) * verb.verbProps.GetHitChanceFactor(weapon, distance);
        var cycle = verb.WarmupTime * pawn.GetStatValue(StatDefOf.AimingDelayFactor) + verb.verbProps.AdjustedCooldown(verb, pawn)
                  + ((verb.BurstShotCount - 1) * verb.TicksBetweenBurstShots).TicksToSeconds();
        return projectile.GetDamageAmount(weapon) * verb.BurstShotCount * hitChance / System.Math.Max(cycle, 0.1f);
    }

    //Every ally still in the plot is on their feet in the Brutus's room and close by, the open map counting as one room
    private bool Together()
    {
        var brutus = plot.brutus;
        if (!brutus.Spawned) return true;
        var room = brutus.GetRoom();
        return plot.allies.All(ally => !InPlot(ally) || !ally.Spawned || ally.Map != brutus.Map
                                    || ally.GetRoom() == room && ally.Position.InHorDistOf(brutus.Position, GroupRadius));
    }

    //Whom an ally gathers on before the plot sets off, and keeps to afterwards if it shoots, and whether the Brutus waits for
    //them: JobGiver_ConspiratorApproach
    public Pawn GatheringOn(Pawn pawn) => plot != null && pawn != plot.brutus && plot.brutus.Spawned ? plot.brutus : null;

    public bool WaitingForAllies(Pawn pawn) => plot is { gathered: false } && pawn == plot.brutus;

    private static bool EnemyNear(Pawn stellarch)
    {
        var cell = stellarch.PositionHeld;
        foreach (var pawn in stellarch.MapHeld.mapPawns.AllPawnsSpawned)
            if (!pawn.Downed && pawn.Position.InHorDistOf(cell, EnemyNearRadius) && pawn.HostileTo(Faction.OfPlayer))
                return true;
        return false;
    }

    private void Strike()
    {
        plot.striking = true;
        var strikers = plot.Conspirators.Where(pawn => pawn.MentalState is MentalState_ConspiratorApproach && !pawn.Dead && !pawn.Downed).ToList();
        foreach (var pawn in strikers) StartState(pawn, VFEE_DefOf.VFEE_ConspiratorStrike);

        var allies = strikers.Where(pawn => pawn != plot.brutus).ToList();
        var alone = allies.Count == 0;
        var text = alone
            ? "VFEE.Conspiracy.Strike".Translate(plot.brutus.Named("BRUTUS"), plot.stellarch.Named("STELLARCH"))
            : "VFEE.Conspiracy.StrikeWithAllies".Translate(plot.brutus.Named("BRUTUS"), plot.stellarch.Named("STELLARCH"), PawnList(allies).Named("ALLIES"));
        if (plot.guards.Count > 0)
        {
            //Who goes for whom: StrikeTargetFor
            var key = GoesStraightForHer(plot.brutus)
                ? alone ? "VFEE.Conspiracy.StrikeHerFirstAlone" : "VFEE.Conspiracy.StrikeHerFirst"
                : alone ? "VFEE.Conspiracy.StrikeGuardsAlone" : "VFEE.Conspiracy.StrikeGuards";
            text += " " + key.Translate(plot.brutus.Named("BRUTUS"), plot.stellarch.Named("STELLARCH"));
        }

        //What the deserters gave the plot for what guards her: grenades for what runs on power near her, handed out now so the
        //approach shows nothing, and a blade for a Deathless stellarch (AscensionCounters)
        plot.grenadiers = AscensionCounters.HandOutGrenades(strikers, plot.stellarch, AscensionCounters.PlotGrenades(strikers, plot.stellarch, plot.difficulty));
        text += AscensionCounters.PlotReadyText(strikers, plot.grenadiers, plot.brutus, plot.stellarch);

        text += "\n\n" + (alone ? "VFEE.Conspiracy.StrikeRulesAlone" : "VFEE.Conspiracy.StrikeRules").Translate(plot.brutus.Named("BRUTUS"),
            plot.stellarch.Named("STELLARCH"));
        Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.StrikeLabel".Translate(plot.brutus.Named("BRUTUS")), text, LetterDefOf.ThreatBig,
            new LookTargets(strikers));
    }

    //Guards first, spread across them, then her, each conspirator starting on the one it stood ready by (MarkFor). On
    //Losing is fun the Brutus goes straight for her and leaves her guards to the allies, unless there is no way to reach
    //her; on Normal it starts on a guard too, since a Brutus who goes straight for her can kill her before the player has
    //had a chance to answer
    public Pawn StrikeTargetFor(Pawn pawn, Pawn current)
    {
        if (plot is not { striking: true }) return null;
        if (GoesStraightForHer(pawn)) return plot.stellarch;
        if (current == null && plot.marks.TryGetValue(pawn, out var mark)) current = mark;
        if (current != null && current != plot.stellarch && plot.guards.Contains(current) && Standing(current, pawn)) return current;
        var standing = plot.guards.Where(guard => Standing(guard, pawn)).ToList();
        if (standing.Count > 0)
            return standing.OrderBy(guard => AttackersOf(guard, pawn)).ThenBy(guard => guard.Position.DistanceToSquared(pawn.Position)).First();
        return plot.stellarch.Dead || plot.stellarch.MapHeld != pawn.Map ? null : plot.stellarch;
    }

    private static bool Standing(Pawn guard, Pawn by) => !guard.Dead && !guard.Downed && guard.Spawned && guard.Map == by.Map;

    //The Brutus on Losing is fun, with a way to her: the strike's letter asks this too, so it says what the Brutus does
    private bool GoesStraightForHer(Pawn pawn)
    {
        var stellarch = plot.stellarch;
        return pawn == plot.brutus && plot.difficulty == AscensionDifficulty.LosingIsFun && !stellarch.Dead && stellarch.MapHeld == pawn.Map
            && pawn.CanReach(stellarch.SpawnedParentOrMe, PathEndMode.Touch, Danger.Deadly, true);
    }

    private int AttackersOf(Pawn guard, Pawn except) =>
        plot.Conspirators.Count(pawn => pawn != except && pawn.MentalState is MentalState_ConspiratorStrike strike && strike.target == guard);

    //The Brutus fell, or she got away: the plot has failed, the others surrender, and all of them stay guilty
    private void PlotFails()
    {
        var failed = plot;
        plot = null;
        var outOfReach = failed.stellarch.MapHeld != failed.map;
        var surrendered = new List<Pawn>();
        foreach (var pawn in failed.Conspirators)
        {
            if (pawn.Dead) continue;
            EndPlotState(pawn);
            RemoveTrait(pawn, VFEE_DefOf.VFEE_Conspirator);
            AscensionCounters.TakeBackGrenade(pawn);
            MakeGuiltyForGood(pawn);
            if ((pawn == failed.brutus && !outOfReach) || pawn.Downed || !pawn.Spawned) continue;
            if (pawn.equipment?.Primary != null) pawn.equipment.TryDropEquipment(pawn.equipment.Primary, out _, pawn.Position);
            surrendered.Add(pawn);
        }

        var survivors = failed.Conspirators.Where(pawn => !pawn.Dead).ToList();
        if (survivors.Count > 0)
            foiled.Add(new FoiledPlot
            {
                stellarch = failed.stellarch,
                conspirators = survivors,
                tick = Find.TickManager.TicksGame
            });

        var text = (outOfReach ? "VFEE.Conspiracy.FoiledOutOfReach" : "VFEE.Conspiracy.FoiledBrutusStopped")
           .Translate(failed.brutus.Named("BRUTUS"), failed.stellarch.Named("STELLARCH"));
        if (surrendered.Count > 0)
            text += " " + (outOfReach ? "VFEE.Conspiracy.FoiledEveryoneSurrendered" : "VFEE.Conspiracy.FoiledSurrendered")
               .Translate(PawnList(surrendered).Named("ALLIES"));
        text += "\n\n" + "VFEE.Conspiracy.GuiltyForGood".Translate();
        if (survivors.Count > 0) text += " " + "VFEE.Conspiracy.TraitorsAmongUs".Translate(failed.stellarch.Named("STELLARCH"));
        Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.FoiledLabel".Translate(), text, LetterDefOf.PositiveEvent,
            new LookTargets(failed.Conspirators.Where(pawn => pawn.Spawned)));
    }

    //Vanilla hands a dead title holder's honor to her heir as she dies. When the stellarch dies with the plot under way,
    //who gets it depends on the answer to the decision her death opens, so it waits for the answer.
    public bool Notify_TitleHolderKilled(Pawn pawn)
    {
        if (releasingSuccession || plot == null || pawn != plot.stellarch) return true;
        plot.decisionPending = true;
        plot.holdingSuccession = true;
        return false;
    }

    //A noble the plot kills does not fail the parade, so the quest is told which death that was. A colonist who kills a
    //conspirator after a truce breaks it.
    public void Notify_PawnKilling(Pawn victim, DamageInfo? dinfo)
    {
        if (plot != null && dinfo?.Instigator is Pawn killer && IsConspirator(killer)) killedByPlot = victim;
    }

    public void Notify_PawnKilled(Pawn victim, DamageInfo? dinfo)
    {
        killedByPlot = null;
        if (truces.Count == 0) return;
        var truce = truces.FirstOrDefault(t => t.conspirators.Contains(victim));
        if (truce == null) return;
        truce.conspirators.Remove(victim);
        var rising = truce.conspirators.Where(pawn => !pawn.Dead && pawn.Faction == Faction.OfPlayer).ToList();
        if (dinfo?.Instigator is Pawn killer && killer != victim && killer.Faction == Faction.OfPlayer)
        {
            truces.Remove(truce);
            if (rising.Count == 0) return;
            MakeTurncoats(rising);
            Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.TruceBrokenLabel".Translate(),
                "VFEE.Conspiracy.TruceBroken".Translate(killer.Named("KILLER"), victim.Named("VICTIM"), truce.stellarchName.Named("STELLARCH"),
                    PawnList(rising).Named("CONSPIRATORS")), LetterDefOf.ThreatBig, new LookTargets(rising.Where(pawn => pawn.Spawned)));
        }
        else if (rising.Count == 0) truces.Remove(truce);
    }

    private void OpenDecision()
    {
        var current = plot;
        var text = "VFEE.Conspiracy.Decision".Translate(current.stellarch.Named("STELLARCH"), current.brutus.Named("BRUTUS"));
        //The truce's lower opinion of the Brutus, which lapses after a year on the Normal difficulty and is never given to a
        //Brutus already dead
        if (!current.brutus.Dead)
        {
            var opinion = current.difficulty == AscensionDifficulty.Normal
                ? "VFEE.Conspiracy.DecisionBrutusOpinionNormal"
                : "VFEE.Conspiracy.DecisionBrutusOpinion";
            text += " " + opinion.Translate(current.brutus.Named("BRUTUS"));
        }

        text += " " + "VFEE.Conspiracy.DecisionTruceBreaks".Translate();
        var node = new DiaNode(text);
        node.options.Add(Answer("VFEE.Conspiracy.AnswerTraitors", ConspiracyAnswer.Traitors));
        var deal = Answer("VFEE.Conspiracy.AnswerDeal", ConspiracyAnswer.Deal);
        if (current.brutus.Dead) deal.Disable("VFEE.Conspiracy.BrutusDead".Translate(current.brutus.Named("BRUTUS")));
        node.options.Add(deal);
        node.options.Add(Answer("VFEE.Conspiracy.AnswerTruce", ConspiracyAnswer.Truce));
        decisionWindow = new Dialog_NodeTree(node, true, title: "VFEE.Conspiracy.DecisionTitle".Translate(current.stellarch.Named("STELLARCH")));
        Find.WindowStack.Add(decisionWindow);

        DiaOption Answer(string key, ConspiracyAnswer answer) => new(key.Translate().Resolve())
        {
            action = () => Resolve(answer),
            resolveTree = true
        };
    }

    public void Resolve(ConspiracyAnswer answer)
    {
        if (plot == null) return;
        var done = plot;
        plot = null;
        if (decisionWindow != null && Find.WindowStack.IsOpen(decisionWindow)) decisionWindow.Close(false);
        decisionWindow = null;
        var conspirators = done.Conspirators.Where(pawn => !pawn.Dead && pawn.Faction == Faction.OfPlayer).ToList();
        foreach (var pawn in conspirators)
        {
            EndPlotState(pawn);
            RemoveTrait(pawn, VFEE_DefOf.VFEE_Conspirator);
            AscensionCounters.TakeBackGrenade(pawn);
            MakeGuiltyForGood(pawn);
        }

        if (answer == ConspiracyAnswer.Deal && !done.brutus.Dead)
        {
            Succession(done, done.brutus);
            Deal(done, conspirators);
        }
        else if (answer == ConspiracyAnswer.Truce)
        {
            Succession(done, null);
            Truce(done, conspirators);
        }
        else
        {
            Succession(done, null);
            MakeTurncoats(conspirators);
            if (conspirators.Count == 0) return;
            Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.TraitorsLabel".Translate(),
                (conspirators.Count == 1 && conspirators[0] == done.brutus ? "VFEE.Conspiracy.TraitorsAlone" : "VFEE.Conspiracy.Traitors")
               .Translate(done.brutus.Named("BRUTUS"), PawnList(conspirators).Named("CONSPIRATORS")), LetterDefOf.ThreatBig,
                new LookTargets(conspirators.Where(pawn => pawn.Spawned)));
        }
    }

    //Her heir takes her honor now and her title at a bestowing ceremony, as vanilla does it: the Brutus under the deal,
    //otherwise her own heir, or the colonist she liked most if her heir was a conspirator
    private void Succession(Conspiracy done, Pawn heirForAll)
    {
        var royalty = done.stellarch?.royalty;
        if (!done.holdingSuccession || royalty == null) return;
        foreach (var title in royalty.AllTitlesForReading.ToList())
        {
            var heir = heirForAll ?? royalty.GetHeir(title.faction);
            if (heirForAll == null && heir != null && (heir.Dead || done.Conspirators.Contains(heir))) heir = done.fallbackHeir is { Dead: false } fallback ? fallback : null;
            royalty.SetHeir(heir, title.faction);
        }

        releasingSuccession = true;
        try
        {
            royalty.Notify_PawnKilled();
        }
        finally
        {
            releasingSuccession = false;
        }
    }

    //Every adult colonist here who is not a conspirator stays loyal to her and goes over to the deserters to fight the
    //conspirators; children and slaves sit the fight out
    private void Deal(Conspiracy done, List<Pawn> conspirators)
    {
        var loyalists = new List<Pawn>();
        var hiding = false;
        foreach (var pawn in done.map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).ToList())
        {
            if (!pawn.RaceProps.Humanlike || conspirators.Contains(pawn) || pawn.IsQuestLodger()) continue;
            if (pawn.IsFreeNonSlaveColonist && pawn.DevelopmentalStage.Adult() && !pawn.IsMutant)
                loyalists.Add(pawn);
            else if (pawn.IsSlave || !pawn.DevelopmentalStage.Adult())
            {
                SitOut(pawn);
                hiding = true;
            }
        }

        MakeTurncoats(loyalists);
        var text = loyalists.Count > 0
            ? "VFEE.Conspiracy.Deal".Translate(done.brutus.Named("BRUTUS"), done.stellarch.Named("STELLARCH"), PawnList(loyalists).Named("LOYALISTS"))
            : "VFEE.Conspiracy.DealNoLoyalists".Translate(done.brutus.Named("BRUTUS"), done.stellarch.Named("STELLARCH"));
        if (hiding) text += " " + "VFEE.Conspiracy.DealHiding".Translate();
        Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.DealLabel".Translate(), text, LetterDefOf.ThreatBig,
            new LookTargets(loyalists.Where(pawn => pawn.Spawned)));
    }

    private void SitOut(Pawn pawn)
    {
        GiveTrait(pawn, VFEE_DefOf.VFEE_Bystander);
        if (!pawn.Downed && !pawn.DevelopmentalStage.Baby()) StartState(pawn, VFEE_DefOf.VFEE_SittingOutFight);
        bystanders.Add(pawn);
    }

    //Everyone stands down; the colony is glad or sorry she is gone, by what each colonist thought of her
    private void Truce(Conspiracy done, List<Pawn> conspirators)
    {
        truces.Add(new Truce
        {
            brutus = done.brutus.Dead ? null : done.brutus,
            conspirators = conspirators.ToList(),
            stellarchName = done.stellarch.LabelShort,
            opinionEndsTick = done.difficulty == AscensionDifficulty.Normal
                ? Find.TickManager.TicksGame + QuestPart_AscensionDifficulty.NormalBrutusOpinionDays * GenDate.TicksPerDay
                : -1
        });
        foreach (var colonist in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
        {
            if (colonist.needs?.mood == null || !done.opinionsOfHer.TryGetValue(colonist, out var opinion)) continue;
            var stage = opinion >= TruceOpinionThreshold ? 0 : opinion <= -TruceOpinionThreshold ? 1 : -1;
            if (stage >= 0) colonist.needs.mood.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(VFEE_DefOf.VFEE_StellarchTruce, stage));
        }

        var key = conspirators.Count == 0 ? "VFEE.Conspiracy.TruceNoneLeft"
            : conspirators.Count == 1 && conspirators[0] == done.brutus ? "VFEE.Conspiracy.TruceAlone"
            : "VFEE.Conspiracy.Truce";
        Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.TruceLabel".Translate(),
            key.Translate(done.stellarch.Named("STELLARCH"), done.brutus.Named("BRUTUS"), PawnList(conspirators).Named("CONSPIRATORS")),
            LetterDefOf.NeutralEvent, new LookTargets(conspirators.Where(pawn => pawn.Spawned)));
    }

    //The opinion malus of a truce: everyone thinks less of the Brutus for as long as the Brutus stays in the colony, whatever
    //the Brutus's rank, since a guard or her crowned heir usually outranks the Brutus; on the Normal difficulty for a year at
    //most. Arrested or enslaved, the Brutus is still here; banished, sold, dead or gone over to the deserters, gone
    public bool IsTruceBrutus(Pawn pawn) => truces.Count > 0
     && truces.Any(truce => truce.brutus == pawn && (truce.opinionEndsTick < 0 || Find.TickManager.TicksGame < truce.opinionEndsTick))
     && StillAmongUs(pawn);

    private void MakeTurncoats(List<Pawn> pawns)
    {
        var deserters = GameComponent_Empire.Instance?.Deserter;
        if (deserters == null || pawns.Count == 0) return;
        var fighters = new List<Pawn>();
        foreach (var pawn in pawns)
        {
            if (pawn.Dead || pawn.Faction == deserters) continue;
            if (pawn.IsCaravanMember())
            {
                pawn.GetCaravan().RemovePawn(pawn);
                Find.WorldPawns.PassToWorld(pawn);
            }

            pawn.SetFaction(deserters);
            turncoats.Add(pawn);
            if (pawn.Spawned && !pawn.Downed) fighters.Add(pawn);
        }

        foreach (var group in fighters.GroupBy(pawn => pawn.Map))
            LordMaker.MakeNewLord(deserters, new LordJob_AssaultColony(deserters, false, false, canSteal: false), group.Key, group);
    }

    private void UpdateFights()
    {
        if (plot is { decisionPending: false } && !plot.stellarch.Dead) plot.RecordOpinions();
        turncoats.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead || !pawn.HostileTo(Faction.OfPlayer));
        if (bystanders.Count == 0 || HoldsDeserters) return;
        foreach (var pawn in bystanders)
        {
            if (pawn.Dead) continue;
            if (pawn.MentalState is MentalState_SittingOutFight) pawn.MentalState.RecoverFromState();
            RemoveTrait(pawn, VFEE_DefOf.VFEE_Bystander);
        }

        bystanders.Clear();
        Messages.Message("VFEE.Conspiracy.FightOver".Translate(), MessageTypeDefOf.NeutralEvent);
    }

    private void MakeGuiltyForGood(Pawn pawn)
    {
        if (!guiltyForGood.Contains(pawn)) guiltyForGood.Add(pawn);
        pawn.guilt?.Notify_Guilty(GenDate.TicksPerDay);
    }

    //Vanilla's guilt runs out after a day, so it is renewed for as long as a conspirator stays in the colony
    private void RenewGuilt()
    {
        guiltyForGood.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead);
        foreach (var pawn in guiltyForGood)
            if (pawn.Faction == Faction.OfPlayer && pawn.guilt != null && pawn.guilt.TicksUntilInnocent < GenDate.TicksPerDay / 2)
                pawn.guilt.Notify_Guilty(GenDate.TicksPerDay);
        var now = Find.TickManager.TicksGame;
        foiled.RemoveAll(plot => now - plot.tick >= TraitorsAmongUsTicks || !plot.conspirators.Any(StillAmongUs));
    }

    //After a plot against her fails, for as long as any of its plotters lives in the colony and for a year at most, she and
    //everyone who loves her are uneasy: stage 0 for those who love her, 1 for her, -1 for nobody. Arrested or enslaved, a
    //plotter is still here; banished, sold or dead, gone. ThoughtWorker_TraitorsAmongUs
    public int TraitorsAmongUsStage(Pawn pawn)
    {
        if (foiled.Count == 0 || pawn.Faction != Faction.OfPlayer) return -1;
        var now = Find.TickManager.TicksGame;
        foreach (var plot in foiled)
        {
            if (now - plot.tick >= TraitorsAmongUsTicks || plot.conspirators.Contains(pawn) || !plot.conspirators.Any(StillAmongUs)) continue;
            if (pawn == plot.stellarch) return 1;
            if (plot.stellarch != null && pawn.relations.OpinionOf(plot.stellarch) >= TruceOpinionThreshold) return 0;
        }

        return -1;
    }

    private static bool StillAmongUs(Pawn pawn) => pawn is { Dead: false, Destroyed: false } && (pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony);

    private static void StartState(Pawn pawn, MentalStateDef def) =>
        pawn.mindState.mentalStateHandler.TryStartMentalState(def, forced: true, forceWake: true, transitionSilently: true);

    private static void EndPlotState(Pawn pawn)
    {
        if (pawn.MentalState is MentalState_ConspiratorApproach or MentalState_ConspiratorStrike) pawn.MentalState.RecoverFromState();
    }

    private static void GiveTrait(Pawn pawn, TraitDef def)
    {
        if (pawn.story?.traits != null && !pawn.story.traits.HasTrait(def)) pawn.story.traits.GainTrait(new Trait(def));
    }

    private static void RemoveTrait(Pawn pawn, TraitDef def)
    {
        var trait = pawn.story?.traits.GetTrait(def);
        if (trait != null) pawn.story.traits.RemoveTrait(trait);
    }

    private static string PawnList(IEnumerable<Pawn> pawns) => pawns.Select(pawn => pawn.LabelShort).ToCommaList(true);

    public override void ExposeData()
    {
        base.ExposeData();
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            guiltyForGood.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead);
            turncoats.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead);
            bystanders.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead);
            foreach (var truce in truces) truce.conspirators.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead);
            truces.RemoveAll(truce => truce.conspirators.Count == 0);
            foreach (var plot in foiled) plot.conspirators.RemoveAll(pawn => pawn == null || pawn.Destroyed || pawn.Dead);
            foiled.RemoveAll(plot => plot.conspirators.Count == 0);
        }

        Scribe_Deep.Look(ref plot, "plot");
        Scribe_Collections.Look(ref betrayedStellarchs, "betrayedStellarchs", LookMode.Value);
        Scribe_Collections.Look(ref guiltyForGood, "guiltyForGood", LookMode.Reference);
        Scribe_Collections.Look(ref truces, "truces", LookMode.Deep);
        Scribe_Collections.Look(ref turncoats, "turncoats", LookMode.Reference);
        Scribe_Collections.Look(ref bystanders, "bystanders", LookMode.Reference);
        Scribe_Collections.Look(ref foiled, "foiled", LookMode.Deep);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        betrayedStellarchs ??= new();
        guiltyForGood ??= new();
        truces ??= new();
        turncoats ??= new();
        bystanders ??= new();
        foiled ??= new();
        guiltyForGood.RemoveAll(pawn => pawn == null);
        turncoats.RemoveAll(pawn => pawn == null);
        bystanders.RemoveAll(pawn => pawn == null);
    }
}

public class Conspiracy : IExposable
{
    public Pawn stellarch;
    public Pawn brutus;
    public List<Pawn> allies = new();
    public List<Pawn> guards = new();
    //The plotters the deserters gave an EMP grenade as the plot struck (AscensionCounters.HandOutGrenades)
    public List<Pawn> grenadiers = new();
    public Map map;
    public Pawn fallbackHeir;
    public int startTick;
    public int inPositionSinceTick = -1;
    //How each conspirator fights (GameComponent_Conspiracy.HowFights), and whom each strikes first (MarkFor)
    public Dictionary<Pawn, ConspiratorFights> fights = new();
    public Dictionary<Pawn, Pawn> marks = new();
    private List<Pawn> fightsKeys;
    private List<ConspiratorFights> fightsValues;
    private List<Pawn> markKeys;
    private List<Pawn> markValues;
    //The allies have gathered on the Brutus, or the time to wait for a straggler is up
    public bool gathered;
    public bool striking;
    public bool decisionPending;
    public bool holdingSuccession;
    //Taken as the plot begins: the difficulty answers only for an ascension still under way, and hers ends with her death
    public AscensionDifficulty difficulty;
    public Dictionary<Pawn, int> opinionsOfHer = new();
    private List<Pawn> opinionKeys;
    private List<int> opinionValues;

    public IEnumerable<Pawn> Conspirators
    {
        get
        {
            yield return brutus;
            foreach (var ally in allies) yield return ally;
        }
    }

    //Opinions of her are kept up while she lives, for the truce's mood: a dead pawn's situational standing is gone
    public void RecordOpinions()
    {
        foreach (var colonist in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
            if (colonist != stellarch)
                opinionsOfHer[colonist] = colonist.relations.OpinionOf(stellarch);
    }

    public void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            opinionsOfHer.RemoveAll(pair => pair.Key == null || pair.Key.Destroyed || pair.Key.Dead);
            fights.RemoveAll(pair => pair.Key == null || pair.Key.Destroyed || pair.Key.Dead);
            marks.RemoveAll(pair => pair.Key == null || pair.Key.Destroyed || pair.Key.Dead || pair.Value == null || pair.Value.Destroyed || pair.Value.Dead);
        }

        //A dead pawn counts as destroyed, and a plain reference to it saves as null; her corpse keeps her, so these keep the dead
        Scribe_References.Look(ref stellarch, "stellarch", true);
        Scribe_References.Look(ref brutus, "brutus", true);
        Scribe_Collections.Look(ref allies, "allies", true, LookMode.Reference);
        Scribe_Collections.Look(ref guards, "guards", true, LookMode.Reference);
        Scribe_Collections.Look(ref grenadiers, "grenadiers", LookMode.Reference);
        Scribe_References.Look(ref map, "map");
        Scribe_References.Look(ref fallbackHeir, "fallbackHeir");
        Scribe_Values.Look(ref startTick, "startTick");
        Scribe_Values.Look(ref inPositionSinceTick, "inPositionSinceTick", -1);
        Scribe_Collections.Look(ref fights, "fights", LookMode.Reference, LookMode.Value, ref fightsKeys, ref fightsValues);
        Scribe_Collections.Look(ref marks, "marks", LookMode.Reference, LookMode.Reference, ref markKeys, ref markValues);
        Scribe_Values.Look(ref gathered, "gathered");
        Scribe_Values.Look(ref striking, "striking");
        Scribe_Values.Look(ref decisionPending, "decisionPending");
        Scribe_Values.Look(ref holdingSuccession, "holdingSuccession");
        Scribe_Values.Look(ref difficulty, "difficulty");
        Scribe_Collections.Look(ref opinionsOfHer, "opinionsOfHer", LookMode.Reference, LookMode.Value, ref opinionKeys, ref opinionValues);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        allies ??= new();
        guards ??= new();
        grenadiers ??= new();
        opinionsOfHer ??= new();
        fights ??= new();
        marks ??= new();
        allies.RemoveAll(pawn => pawn == null);
        guards.RemoveAll(pawn => pawn == null);
        grenadiers.RemoveAll(pawn => pawn == null);
        opinionsOfHer.RemoveAll(pair => pair.Key == null);
        fights.RemoveAll(pair => pair.Key == null);
        marks.RemoveAll(pair => pair.Key == null || pair.Value == null);
    }
}

//A plot that failed with plotters left alive, who weigh on her and on those who love her while they live in the colony
public class FoiledPlot : IExposable
{
    public Pawn stellarch;
    public List<Pawn> conspirators = new();
    public int tick;

    public void ExposeData()
    {
        Scribe_References.Look(ref stellarch, "stellarch", true);
        Scribe_Collections.Look(ref conspirators, "conspirators", LookMode.Reference);
        Scribe_Values.Look(ref tick, "tick");
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        conspirators ??= new();
        conspirators.RemoveAll(pawn => pawn == null);
    }
}

public class Truce : IExposable
{
    public Pawn brutus;
    public List<Pawn> conspirators = new();
    public string stellarchName;
    //When everyone stops thinking less of the Brutus: never (-1), or a year after the truce on the Normal difficulty
    public int opinionEndsTick = -1;

    public void ExposeData()
    {
        Scribe_References.Look(ref brutus, "brutus");
        Scribe_Collections.Look(ref conspirators, "conspirators", LookMode.Reference);
        Scribe_Values.Look(ref stellarchName, "stellarchName");
        Scribe_Values.Look(ref opinionEndsTick, "opinionEndsTick", -1);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        conspirators ??= new();
        conspirators.RemoveAll(pawn => pawn == null);
    }
}
