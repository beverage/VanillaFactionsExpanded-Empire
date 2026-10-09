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

//The Brutus: during the parade's deserter raid, one colonist per stellarch turns on her, with allies. The plot runs here
//rather than in the parade because it plays out whatever happens to the parade, and so does everything it leaves behind:
//conspirators guilty for good, truces that hold until a colonist kills one of them, and the fights its answers start
public class GameComponent_Conspiracy : GameComponent
{
    //The strike waits until no enemy is near her, but for no more than an hour once a conspirator is in reach of her,
    //and comes two hours into the approach whatever happens
    private const float EnemyNearRadius = 12f;
    private const int MaxWaitForEnemiesTicks = GenDate.TicksPerHour;
    private const int MaxApproachTicks = 2 * GenDate.TicksPerHour;

    //As a delegation's cell gathers on its leader, the allies first gather on the Brutus, in the Brutus's room and within
    //this of the Brutus, then go for her behind the Brutus, so none strikes alone with the others rooms away. A straggler
    //holds up neither the gathering nor the strike for more than half an hour
    private const float GroupRadius = 5f;
    private const int StragglerWaitTicks = 1250;

    //Who turns: one of her nobles, whatever they think of her, as Brutus loved Caesar: the rival most likely, then any other
    //titled colonist. With no noble free to (all of them in her guard, say), anyone else, again whatever they think of her,
    //the holders of Ideology roles most likely
    private const float RivalWeight = 10f;
    private const float TitledWeight = 4f;
    private const float RoleWeight = 2f;

    //The line vanilla draws for its own thoughts about a death, here between the colonists who love her and the rest. A truce
    //moves the mood of colonists whose opinion of her was at least this strong either way, and nobody who loves her joins
    //the Brutus against her
    private const int TruceOpinionThreshold = 20;

    public static GameComponent_Conspiracy Instance;

    public Conspiracy plot;
    private List<string> betrayedStellarchs = new();
    private List<Pawn> guiltyForGood = new();
    private List<Truce> truces = new();
    private List<Pawn> turncoats = new();
    private List<Pawn> bystanders = new();

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

    private static bool Fighting(Pawn pawn) => pawn.Spawned && !pawn.Dead && !pawn.Downed && !pawn.IsPrisoner && pawn.HostileTo(Faction.OfPlayer);

    private static bool InPlot(Pawn pawn) =>
        !pawn.Dead && !pawn.Downed && pawn.MentalState is MentalState_ConspiratorApproach or MentalState_ConspiratorStrike;

    public override void GameComponentTick()
    {
        var ticks = Find.TickManager.TicksGame;
        if (plot is { decisionPending: false } && ticks % 30 == 0) UpdatePlot();
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
        if (plot != null || stellarch == null || stellarch.Dead || !Find.Storyteller.difficulty.allowViolentQuests
         || betrayedStellarchs.Contains(stellarch.GetUniqueLoadID())) return false;
        //The Brutus may love her, so there is a plot whenever anyone outside her honor guard is fit to strike; the allies are
        //only ever candidates, who do not
        var eligible = parade.Map.mapPawns.FreeColonistsSpawned.Where(pawn => CanConspire(pawn, parade)).ToList();
        if (eligible.Count == 0) return false;
        var candidates = eligible.Where(pawn => pawn.relations.OpinionOf(stellarch) < TruceOpinionThreshold).ToList();

        var nobles = eligible.Where(pawn => pawn.royalty?.MostSeniorTitle != null).ToList();
        var rival = nobles.OrderByDescending(pawn => pawn.royalty.MostSeniorTitle.def.seniority)
           .ThenByDescending(pawn => pawn.HasHonors() ? pawn.Honors().Honors.Count() : 0)
           .FirstOrDefault();
        var brutus = nobles.Count > 0
            ? nobles.RandomElementByWeight(pawn => pawn == rival ? RivalWeight : TitledWeight)
            : eligible.RandomElementByWeight(pawn => ModsConfig.IdeologyActive && pawn.Ideo?.GetRole(pawn) != null ? RoleWeight : 1f);

        //As many allies as she has guards: those who dislike her most, then those who like the Brutus most, then the rest,
        //coolest towards her first
        var guards = parade.guards.Where(guard => guard.Spawned && !guard.Dead && !guard.Downed).ToList();
        var others = candidates.Where(pawn => pawn != brutus)
           .Select(pawn => (pawn, ofHer: pawn.relations.OpinionOf(stellarch), ofBrutus: pawn.relations.OpinionOf(brutus))).ToList();
        var allies = others.Where(other => other.ofHer < 0).OrderBy(other => other.ofHer)
           .Concat(others.Where(other => other.ofHer >= 0 && other.ofBrutus > 0).OrderByDescending(other => other.ofBrutus))
           .Concat(others.Where(other => other.ofHer >= 0 && other.ofBrutus <= 0).OrderBy(other => other.ofHer))
           .Select(other => other.pawn)
           .Take(guards.Count)
           .ToList();

        plot = new Conspiracy
        {
            stellarch = stellarch,
            brutus = brutus,
            allies = allies,
            guards = guards,
            map = parade.Map,
            startTick = Find.TickManager.TicksGame
        };
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
        var together = Together();
        if (!plot.gathered)
        {
            if (!together && now - plot.startTick < StragglerWaitTicks) return;
            plot.gathered = true;
        }

        var inReach = plot.Conspirators.Any(pawn => pawn.MentalState is MentalState_ConspiratorApproach && pawn.Spawned
         && LordToil_AssassinApproach.InStrikingDistance(pawn, stellarch));
        if (inReach && plot.inReachSinceTick < 0) plot.inReachSinceTick = now;
        var inReachFor = plot.inReachSinceTick < 0 ? 0 : now - plot.inReachSinceTick;
        if ((inReach && (together || inReachFor >= StragglerWaitTicks) && (!EnemyNear(stellarch) || inReachFor >= MaxWaitForEnemiesTicks))
         || now - plot.startTick >= MaxApproachTicks)
            Strike();
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

    //Who an ally keeps to while the plot approaches her, and whether the Brutus waits for them: JobGiver_ConspiratorApproach
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
            text += " " + (alone ? "VFEE.Conspiracy.StrikeGuardsAlone" : "VFEE.Conspiracy.StrikeGuards").Translate(plot.brutus.Named("BRUTUS"),
                plot.stellarch.Named("STELLARCH"));
        text += "\n\n" + (alone ? "VFEE.Conspiracy.StrikeRulesAlone" : "VFEE.Conspiracy.StrikeRules").Translate(plot.brutus.Named("BRUTUS"),
            plot.stellarch.Named("STELLARCH"));
        Find.LetterStack.ReceiveLetter("VFEE.Conspiracy.StrikeLabel".Translate(plot.brutus.Named("BRUTUS")), text, LetterDefOf.ThreatBig,
            new LookTargets(strikers));
    }

    //Guards first, spread across them, then her
    public Pawn StrikeTargetFor(Pawn pawn, Pawn current)
    {
        if (plot is not { striking: true }) return null;

        bool Standing(Pawn guard) => !guard.Dead && !guard.Downed && guard.Spawned && guard.Map == pawn.Map;

        if (current != null && current != plot.stellarch && plot.guards.Contains(current) && Standing(current)) return current;
        var standing = plot.guards.Where(Standing).ToList();
        if (standing.Count > 0)
            return standing.OrderBy(guard => AttackersOf(guard, pawn)).ThenBy(guard => guard.Position.DistanceToSquared(pawn.Position)).First();
        return plot.stellarch.Dead || plot.stellarch.MapHeld != pawn.Map ? null : plot.stellarch;
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
            MakeGuiltyForGood(pawn);
            if ((pawn == failed.brutus && !outOfReach) || pawn.Downed || !pawn.Spawned) continue;
            if (pawn.equipment?.Primary != null) pawn.equipment.TryDropEquipment(pawn.equipment.Primary, out _, pawn.Position);
            surrendered.Add(pawn);
        }

        var text = (outOfReach ? "VFEE.Conspiracy.FoiledOutOfReach" : "VFEE.Conspiracy.FoiledBrutusStopped")
           .Translate(failed.brutus.Named("BRUTUS"), failed.stellarch.Named("STELLARCH"));
        if (surrendered.Count > 0)
            text += " " + (outOfReach ? "VFEE.Conspiracy.FoiledEveryoneSurrendered" : "VFEE.Conspiracy.FoiledSurrendered")
               .Translate(PawnList(surrendered).Named("ALLIES"));
        text += "\n\n" + "VFEE.Conspiracy.GuiltyForGood".Translate();
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
        var node = new DiaNode("VFEE.Conspiracy.Decision".Translate(current.stellarch.Named("STELLARCH"), current.brutus.Named("BRUTUS")));
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
            stellarchName = done.stellarch.LabelShort
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

    //The opinion malus of a truce: everyone thinks less of a Brutus who is the colony's highest title holder
    public bool IsHighestTruceBrutus(Pawn pawn)
    {
        if (truces.Count == 0 || pawn.Faction != Faction.OfPlayer || !truces.Any(truce => truce.brutus == pawn)) return false;
        var seniority = pawn.royalty?.MostSeniorTitle?.def.seniority ?? 0;
        return seniority > 0 && !PawnsFinder.AllMaps_FreeColonists.Any(other => other != pawn && (other.royalty?.MostSeniorTitle?.def.seniority ?? 0) > seniority);
    }

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
    }

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
        }

        Scribe_Deep.Look(ref plot, "plot");
        Scribe_Collections.Look(ref betrayedStellarchs, "betrayedStellarchs", LookMode.Value);
        Scribe_Collections.Look(ref guiltyForGood, "guiltyForGood", LookMode.Reference);
        Scribe_Collections.Look(ref truces, "truces", LookMode.Deep);
        Scribe_Collections.Look(ref turncoats, "turncoats", LookMode.Reference);
        Scribe_Collections.Look(ref bystanders, "bystanders", LookMode.Reference);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        betrayedStellarchs ??= new();
        guiltyForGood ??= new();
        truces ??= new();
        turncoats ??= new();
        bystanders ??= new();
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
    public Map map;
    public Pawn fallbackHeir;
    public int startTick;
    public int inReachSinceTick = -1;
    //The allies have gathered on the Brutus, or the time to wait for a straggler is up
    public bool gathered;
    public bool striking;
    public bool decisionPending;
    public bool holdingSuccession;
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
        if (Scribe.mode == LoadSaveMode.Saving) opinionsOfHer.RemoveAll(pair => pair.Key == null || pair.Key.Destroyed || pair.Key.Dead);
        //A dead pawn counts as destroyed, and a plain reference to it saves as null; her corpse keeps her, so these keep the dead
        Scribe_References.Look(ref stellarch, "stellarch", true);
        Scribe_References.Look(ref brutus, "brutus", true);
        Scribe_Collections.Look(ref allies, "allies", true, LookMode.Reference);
        Scribe_Collections.Look(ref guards, "guards", true, LookMode.Reference);
        Scribe_References.Look(ref map, "map");
        Scribe_References.Look(ref fallbackHeir, "fallbackHeir");
        Scribe_Values.Look(ref startTick, "startTick");
        Scribe_Values.Look(ref inReachSinceTick, "inReachSinceTick", -1);
        Scribe_Values.Look(ref gathered, "gathered");
        Scribe_Values.Look(ref striking, "striking");
        Scribe_Values.Look(ref decisionPending, "decisionPending");
        Scribe_Values.Look(ref holdingSuccession, "holdingSuccession");
        Scribe_Collections.Look(ref opinionsOfHer, "opinionsOfHer", LookMode.Reference, LookMode.Value, ref opinionKeys, ref opinionValues);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        allies ??= new();
        guards ??= new();
        opinionsOfHer ??= new();
        allies.RemoveAll(pawn => pawn == null);
        guards.RemoveAll(pawn => pawn == null);
        opinionsOfHer.RemoveAll(pair => pair.Key == null);
    }
}

public class Truce : IExposable
{
    public Pawn brutus;
    public List<Pawn> conspirators = new();
    public string stellarchName;

    public void ExposeData()
    {
        Scribe_References.Look(ref brutus, "brutus");
        Scribe_Collections.Look(ref conspirators, "conspirators", LookMode.Reference);
        Scribe_Values.Look(ref stellarchName, "stellarchName");
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        conspirators ??= new();
        conspirators.RemoveAll(pawn => pawn == null);
    }
}
