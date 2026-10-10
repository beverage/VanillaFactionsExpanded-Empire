using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//What the stellarch's attackers bring against her build. The deserters watch her through the delegations, and the
//Brutus's plot is in league with them, so whoever strikes at her comes ready for what guards her: EMP grenades for what of
//hers runs on power (her war mechs and their bubble shields, the colony's turrets near her, personal shields), a smuggled
//weapon weighted toward what gets through her armor, an execution for a stellarch the Deathless gene keeps alive, and
//psychic dampeners for a cell sent against a psycaster (the plot is deaf already, VFEE_Conspirator)
public static class AscensionCounters
{
    //Vanilla's EMP grenade: 50 EMP in a 3.5-cell blast (Proj_GrenadeEMP). A mech it stuns cannot act for 25 seconds, less a
    //super-heavy's EMP resistance, then shrugs off EMP for the rest of 2,200 ticks, so a second blast that soon is wasted on
    //it; a turret is stunned as long and can be stunned again at once (StunHandler)
    private const float EmpRadius = 3.5f;

    //What guards her: an escort follows within 5 cells and idles within 3 (JobGiver_AIFollowOverseer,
    //JobGiver_WanderOverseer), and a turret this close can shoot whoever comes for her
    private const float GuardRadius = 15f;

    //Grenades on Losing is fun: one for each blast it takes to catch everything near her, at most two in a cell of four or
    //fewer and three in a larger one; the plot brings a second when her escort is more than four war mechs. Normal sends one
    //with each (QuestPart_AscensionDifficulty)
    private const int SmallCell = 4;
    private const int SmallCellGrenades = 2;
    private const int LargeCellGrenades = 3;
    private const int EscortForSecondPlotGrenade = 4;

    private const float MinDamageThrough = 0.01f;

    private static readonly AccessTools.FieldRef<StunHandler, Dictionary<DamageDef, int>> AdaptationTicks =
        AccessTools.FieldRefAccess<StunHandler, Dictionary<DamageDef, int>>("adaptationTicksLeft");

    //Everything near her an EMP blast puts out of the fight: the colony's war mechs, its working turrets, and the active
    //personal shields of her and of anyone of the colony's around her, but nothing of her attackers' own, a plotter's shield
    //or the mechs a plotter commands. An attacker about to throw counts what is near it too: a colonist flees attackers
    //unless told to fight (Pawn_PlayerSettings), and her mechs stand and shoot from wherever they can hit
    //(JobGiver_AIFightEnemy), so they are soon far behind her
    public static List<Thing> PoweredGuards(Pawn stellarch, IEnumerable<Pawn> side, Pawn thrower = null)
    {
        var guards = new List<Thing>();
        var map = stellarch.MapHeld;
        if (map == null) return guards;
        var cell = stellarch.PositionHeld;
        var own = new HashSet<Pawn>(side);
        bool Near(IntVec3 at) => at.InHorDistOf(cell, GuardRadius) || thrower != null && at.InHorDistOf(thrower.Position, GuardRadius);
        foreach (var pawn in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            if (!pawn.Downed && Near(pawn.Position) && !own.Contains(pawn) && !own.Contains(pawn.GetOverseer())
             && (pawn.RaceProps.IsMechanoid ? !pawn.RaceProps.IsWorkMech : Shielded(pawn)))
                guards.Add(pawn);
        foreach (var building in map.listerBuildings.allBuildingsColonist)
            if (building is Building_Turret turret && Near(turret.Position) && Working(turret))
                guards.Add(turret);
        return guards;
    }

    private static bool Shielded(Pawn pawn) =>
        pawn.apparel != null && pawn.apparel.WornApparel.Any(apparel => apparel.TryGetComp<CompShield>() is { ShieldState: ShieldState.Active });

    //A turret that fights, can be stunned, and has power if it needs it
    private static bool Working(Building_Turret turret) =>
        turret.def.building is { ai_combatDangerous: true } && turret.GetComp<CompStunnable>() != null
     && turret.GetComp<CompPowerTrader>() is not { PowerOn: false } && !turret.IsBrokenDown();

    //Out of the fight already, or for any blast now: stunned, adapted to EMP, or its shield down
    private static bool Spent(Thing thing)
    {
        switch (thing)
        {
            case Pawn { RaceProps.IsMechanoid: true } mech:
                var stunner = mech.stances?.stunner;
                return stunner == null || stunner.Stunned || AdaptationTicks(stunner) is { } ticks && ticks.TryGetValue(DamageDefOf.EMP, out var left) && left > 0;
            case Pawn pawn:
                return !Shielded(pawn);
            case Building_Turret turret:
                return turret.GetComp<CompStunnable>()?.StunHandler is not { Stunned: false };
            default:
                return true;
        }
    }

    private static bool Still(Thing thing) => thing is not Pawn { pather.Moving: true };

    //What of hers fights the plot. The plot is hostile to her and her guards only (MentalState_ConspiratorStrike), so her
    //mechs never engage a plotter on their own and turrets never do: a mech counts once the player drafts it or sends it at a
    //plotter, and a shield, on her or on a guard, always does
    private static bool FightsThePlot(Thing thing, List<Pawn> plot) => thing switch
    {
        Pawn { RaceProps.IsMechanoid: true } mech => mech.Drafted || mech.CurJob?.targetA.Thing is Pawn target && plot.Contains(target),
        Pawn => true,
        _ => false
    };

    //Where the side's grenades already go: one a teammate is winding up to throw, and one in the air or burning its fuse on
    //the ground, so two grenadiers who see the same chance do not spend both grenades on the same few
    private static List<IntVec3> Claimed(Pawn pawn, List<Pawn> side, Ability ability)
    {
        var claimed = new List<IntVec3>();
        foreach (var other in side)
            if (other != pawn && other.Spawned && other.Map == pawn.Map && other.CurJob is { ability: { } held } job && held.def == ability.def)
                claimed.Add(job.targetA.Cell);
        var grenade = ability.CompOfType<CompAbilityEffect_LaunchProjectile>()?.Props.projectileDef;
        if (grenade != null)
            foreach (var thing in pawn.Map.listerThings.ThingsOfDef(grenade))
                if (thing is Projectile { Launcher: Pawn launcher } thrown && side.Contains(launcher))
                    claimed.Add(thrown.usedTarget.Cell);
        return claimed;
    }

    //An implant that knocks its wearer out in an EMP blast (the neurocalculator and its kin, HediffComp_ReactOnDamage), so a
    //blast never catches an attacker who carries one
    private static bool KnockedOutByEmp(Pawn pawn) =>
        pawn.health.hediffSet.hediffs.Any(hediff => hediff.TryGetComp<HediffComp_ReactOnDamage>()?.Props.damageDefIncoming == DamageDefOf.EMP);

    //How many blasts it takes to catch everything near her once, each catching the most of what the ones before it left,
    //centred on her or on one of them
    private static int BlastsToCatch(List<Thing> guards, IntVec3 her)
    {
        var left = guards.ToList();
        var blasts = 0;
        while (left.Count > 0)
        {
            var centers = left.Select(thing => thing.Position).ToList();
            centers.Add(her);
            var center = centers.MaxBy(cell => left.Count(thing => thing.Position.InHorDistOf(cell, EmpRadius)));
            left.RemoveAll(thing => thing.Position.InHorDistOf(center, EmpRadius));
            blasts++;
        }

        return blasts;
    }

    public static int CellGrenades(List<Pawn> cell, Pawn stellarch, AscensionDifficulty difficulty)
    {
        var guards = PoweredGuards(stellarch, cell);
        if (guards.Count == 0) return 0;
        var grenades = difficulty == AscensionDifficulty.Normal
            ? QuestPart_AscensionDifficulty.NormalCellGrenades
            : Mathf.Min(BlastsToCatch(guards, stellarch.PositionHeld), cell.Count <= SmallCell ? SmallCellGrenades : LargeCellGrenades);
        return Mathf.Min(grenades, cell.Count);
    }

    //Turrets never fire on a plotter, so the plot's grenades are for her mechs and the shields around her alone
    public static int PlotGrenades(List<Pawn> strikers, Pawn stellarch, AscensionDifficulty difficulty)
    {
        var guards = PoweredGuards(stellarch, strikers).Where(thing => thing is Pawn).ToList();
        if (guards.Count == 0) return 0;
        if (difficulty == AscensionDifficulty.Normal) return QuestPart_AscensionDifficulty.NormalPlotGrenades;
        return guards.Count(thing => thing is Pawn { RaceProps.IsMechanoid: true }) > EscortForSecondPlotGrenade ? 2 : 1;
    }

    //A grenade each for the attackers nearest what guards her, as an ability with one charge: thrown as vanilla's EMP grenade
    //is thrown, it leaves each attacker its weapon and its way of fighting
    public static List<Pawn> HandOutGrenades(IEnumerable<Pawn> attackers, Pawn stellarch, int count)
    {
        var given = new List<Pawn>();
        var side = attackers.ToList();
        var guards = PoweredGuards(stellarch, side);
        if (count <= 0 || guards.Count == 0) return given;
        foreach (var pawn in side.Where(CanThrow).OrderBy(pawn => guards.Min(thing => pawn.Position.DistanceToSquared(thing.Position))).Take(count))
        {
            pawn.abilities.GainAbility(VFEE_DefOf.VFEE_SmuggledEMPGrenade);
            given.Add(pawn);
        }

        return given;
    }

    private static bool CanThrow(Pawn pawn) => pawn.Spawned && !pawn.Downed && pawn.abilities != null && !pawn.WorkTagIsDisabled(WorkTags.Violent)
                                            && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);

    //A plotter's grenade goes when the plot does, thrown or not
    public static void TakeBackGrenade(Pawn pawn)
    {
        if (pawn?.abilities?.GetAbility(VFEE_DefOf.VFEE_SmuggledEMPGrenade) != null) pawn.abilities.RemoveAbility(VFEE_DefOf.VFEE_SmuggledEMPGrenade);
    }

    //An attacker with a grenade throws it before anything else, at whichever, she or one of what a blast would catch, takes
    //in the most of what guards her or stands near the attacker still in the fight, within its reach and sight: her escort
    //keeps within 3 cells of her while she idles or sleeps, so a blast on her takes it all. Only what stands still counts,
    //since the grenade flies at 0.12 cells a tick and then burns a 100-tick fuse (Proj_GrenadeEMP), and anything on the move
    //is gone by then; her mechs stand still to shoot, to wait by her, or when drafted to fight, and until then it holds the
    //grenade. What a teammate's grenade already goes for is left to it. A plotter holds its grenade for what fights the plot,
    //so it lands when the player drafts her mechs, the quickest answer to a plot, rather than on whichever stood idle as it
    //struck. A blast that spares everyone on its own side an EMP would knock out comes first; with none, it throws the best
    //regardless, as vanilla's EMP grenadiers do. Out of reach of everything, it first goes where it can throw at the best of
    //them, then on to whoever it strikes, unless asked only to throw
    public static Job ThrowJob(Pawn pawn, Pawn stellarch, IEnumerable<Pawn> side, bool moveIntoReach = true, bool plot = false)
    {
        var ability = pawn.abilities?.GetAbility(VFEE_DefOf.VFEE_SmuggledEMPGrenade);
        if (ability == null || !ability.CanCast.Accepted || !pawn.Spawned) return null;
        var own = side.ToList();
        var claimed = Claimed(pawn, own, ability);
        var live = PoweredGuards(stellarch, own, pawn)
           .Where(thing => !Spent(thing) && Still(thing) && (!plot || FightsThePlot(thing, own))
                        && !claimed.Any(cell => thing.Position.InHorDistOf(cell, EmpRadius))).ToList();
        if (live.Count == 0) return null;
        var spared = own.Where(other => other.Spawned && other.Map == pawn.Map && KnockedOutByEmp(other)).ToList();
        Thing best = null;
        var bestCaught = 0;
        Thing bestSparing = null;
        var bestSparingCaught = 0;
        Thing outOfReach = null;
        var outOfReachCaught = 0;
        var anchors = live.ToList();
        if (stellarch.Spawned && stellarch.Map == pawn.Map && Still(stellarch)) anchors.Add(stellarch);
        foreach (var anchor in anchors)
        {
            var center = anchor.Position;
            var caught = live.Count(other => other.Position.InHorDistOf(center, EmpRadius));
            if (caught == 0) continue;
            if (!ability.verb.CanHitTarget(center))
            {
                if (caught <= outOfReachCaught) continue;
                outOfReach = anchor;
                outOfReachCaught = caught;
                continue;
            }

            if (caught > bestCaught)
            {
                best = anchor;
                bestCaught = caught;
            }

            if (caught > bestSparingCaught && !spared.Any(other => other.Position.InHorDistOf(center, EmpRadius)))
            {
                bestSparing = anchor;
                bestSparingCaught = caught;
            }
        }

        //At the thing, not its cell, so a warmup that outlasts its standing still throws where it is then
        var target = bestSparing ?? best;
        if (target != null) return ability.GetJob(target, target);
        if (!moveIntoReach || outOfReach == null || !CastPositionFinder.TryFindCastPosition(new CastPositionRequest
            {
                caster = pawn,
                target = outOfReach,
                verb = ability.verb,
                maxRangeFromTarget = ability.verb.verbProps.range,
                wantCoverFromTarget = false
            }, out var cell) || cell == pawn.Position) return null;
        var move = JobMaker.MakeJob(JobDefOf.Goto, cell);
        move.locomotionUrgency = LocomotionUrgency.Jog;
        move.canBashDoors = true;
        move.expiryInterval = 120;
        move.checkOverrideOnExpire = true;
        return move;
    }

    //A Deathless stellarch does not die of her wounds while her brain is whole: they put her in a coma for days
    //(SanguophageUtility.ShouldBeDeathrestingOrInComaInsteadOfDead), so blows kill her only once one happens to destroy her
    //brain, and until then the colony can still save her. Vanilla's execution cut takes a Deathless pawn's brain, so once
    //she is down an attacker beside her makes sure of her with it
    public static bool NeedsExecution(Pawn stellarch) =>
        ModsConfig.BiotechActive && GeneDefOf.Deathless != null && stellarch.genes != null && stellarch.genes.HasActiveGene(GeneDefOf.Deathless);

    public static Job ExecuteJob(Pawn pawn, Pawn stellarch)
    {
        if (stellarch.Dead || !stellarch.Downed || !stellarch.Spawned || stellarch.Map != pawn.Map || !NeedsExecution(stellarch)
         || !pawn.CanReach(stellarch, PathEndMode.Touch, Danger.Deadly, true)) return null;
        var job = JobMaker.MakeJob(VFEE_DefOf.VFEE_ExecuteStellarch, stellarch);
        job.canBashDoors = true;
        return job;
    }

    //A titled stellarch holds a psylink, which each title's bestowing raises, so a cell sent against one comes deaf to her
    //psycasts and to psychic lances, as the plot does
    public static bool Dampen(IEnumerable<Pawn> cell, Pawn stellarch)
    {
        if (stellarch.GetPsylinkLevel() <= 0) return false;
        foreach (var pawn in cell)
            if (pawn.health.hediffSet.GetFirstHediffOfDef(VFEE_DefOf.VFEE_PsychicDampener) == null)
                pawn.health.AddHediff(VFEE_DefOf.VFEE_PsychicDampener);
        return true;
    }

    //The share of a weapon's damage that gets through what a pawn wears over its torso and its own hide, by vanilla's rule
    //(ArmorUtility.ApplyArmor), so the smuggled pick leans toward what gets through her armor. A weapon without a projectile
    //counts in full, and none counts for quite nothing, so there is still a draw against armor nothing in the kit gets through
    public static float DamageThrough(Pawn pawn, ThingDef weapon)
    {
        var projectile = weapon.Verbs?.FirstOrDefault(verb => verb.defaultProjectile != null)?.defaultProjectile?.projectile;
        var stat = projectile?.damageDef?.armorCategory?.armorRatingStat;
        if (pawn == null || stat == null) return 1f;
        var penetration = projectile.GetArmorPenetration();
        var share = Through(pawn.GetStatValue(stat), penetration);
        var torso = pawn.RaceProps.body.corePart;
        if (pawn.apparel != null)
            foreach (var apparel in pawn.apparel.WornApparel)
                if (apparel.def.apparel.CoversBodyPart(torso))
                    share *= Through(apparel.GetStatValue(stat), penetration);
        return Mathf.Max(share, MinDamageThrough);
    }

    //Of the armor left after penetration, half is the chance a hit does nothing and the rest of it the chance it does half
    private static float Through(float armor, float penetration)
    {
        var left = Mathf.Clamp(armor - penetration, 0f, ArmorUtility.MaxArmorRating);
        return left <= 1f ? 1f - 0.75f * left : (1f - left / 2f) / 2f;
    }

    //What the reveal's letter adds about what the cell came with: a lone deserter is named, a cell spoken of together
    public static string CellReadyText(List<Pawn> cell, List<Pawn> grenadiers, bool dampened, Pawn stellarch)
    {
        var text = "";
        var alone = cell.Count == 1 ? "Alone" : "";
        if (grenadiers.Count > 0)
            text += " " + (grenadiers.Count == 1 ? "VFEE.Counters.CellGrenade" : "VFEE.Counters.CellGrenades")
               .Translate(PawnList(grenadiers).Named("GRENADIERS"), stellarch.Named("STELLARCH")).Resolve();
        if (dampened) text += " " + ("VFEE.Counters.CellDampened" + alone).Translate(cell[0].Named("DESERTER"), stellarch.Named("STELLARCH")).Resolve();
        if (NeedsExecution(stellarch)) text += " " + ("VFEE.Counters.CellExecution" + alone).Translate(cell[0].Named("DESERTER"), stellarch.Named("STELLARCH")).Resolve();
        return text;
    }

    //What the strike's letter adds about what the deserters gave the plot
    public static string PlotReadyText(List<Pawn> strikers, List<Pawn> grenadiers, Pawn brutus, Pawn stellarch)
    {
        var text = "";
        if (grenadiers.Count > 0)
            text += " " + (grenadiers.Count == 1 ? "VFEE.Counters.PlotGrenade" : "VFEE.Counters.PlotGrenades")
               .Translate(PawnList(grenadiers).Named("GRENADIERS"), stellarch.Named("STELLARCH")).Resolve();
        if (NeedsExecution(stellarch))
            text += " " + (strikers.Count == 1 ? "VFEE.Counters.PlotExecutionAlone" : "VFEE.Counters.PlotExecution")
               .Translate(brutus.Named("BRUTUS"), stellarch.Named("STELLARCH")).Resolve();
        return text;
    }

    private static string PawnList(IEnumerable<Pawn> pawns) => pawns.Select(pawn => pawn.LabelShort).ToCommaList(true);
}
