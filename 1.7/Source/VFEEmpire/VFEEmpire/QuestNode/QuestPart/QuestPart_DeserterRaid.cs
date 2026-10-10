using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace VFEEmpire;

//The parade's deserter raid, made as it comes rather than when the ascension was offered. Sized then by the colony's threat
//points, it followed wealth: a rich colony with few to defend it met a horde, and a poor one behind turrets met a handful.
//It now comes as two deserters for each defender the Brutus's plot would leave, on either difficulty: Losing is fun's
//larger plot takes more of the defence already. A defender is a colonist who can fight, as the plot counts them (the
//guards included, the stellarch not), or a war mech, wherever it is: here, in a caravan, in flight or on a map the
//colony has not settled, but not at another of its settlements or in cryptosleep. The quest tells the player to have
//everyone home for the parade, so sending fighters away does not shrink the raid. Turrets do not count. The plot's own
//colonists come off, the Brutus and its allies as a plot begun then would draw them: they leave the fight, and a small
//colony's plot can take half of it. One defender is always left, so the plot keeps the raid as its cover. Every deserter
//is after the stellarch: half come by whatever way they can walk, and half breach in, steering around the turrets' fire.
public class QuestPart_DeserterRaid : QuestPart
{
    public const float DesertersPerDefender = 2f;
    public const float MinDefenders = 1f;

    public string inSignal;
    //Sent with the raiders as its subject, for the letter
    public string outSignalArrived;
    public MapParent mapParent;
    public Pawn stellarch;

    public override void Notify_QuestSignalReceived(Signal signal)
    {
        base.Notify_QuestSignalReceived(signal);
        if (signal.tag != inSignal) return;
        var map = mapParent?.Map;
        if (map == null) return;
        static bool HostileToPlayerAndEmpire(Faction f) => f.HostileTo(Faction.OfPlayer) && f.HostileTo(Faction.OfEmpire);
        var deserters = GameComponent_Empire.Instance.Deserter;
        var faction = deserters
            ?? Find.FactionManager.GetFactions(allowNonHumanlike: false, minTechLevel: TechLevel.Industrial).Where(HostileToPlayerAndEmpire).RandomElementWithFallback()
            ?? Find.FactionManager.GetFactions(allowNonHumanlike: false).Where(HostileToPlayerAndEmpire).RandomElementWithFallback();
        if (faction == null) return;
        var parade = map.lordManager.lords.Select(lord => lord.LordJob).OfType<LordJob_Parade>().FirstOrDefault(job => job.stellarch == stellarch);
        var plotters = parade == null ? 0 : GameComponent_Conspiracy.Instance?.PlottersFor(parade) ?? 0;
        var count = DesertersFor(Defenders(map, stellarch), plotters);
        //Made one by one, so the count is exact: the deserters' group maker would cap each pawn's cost by its points curve.
        //Another faction standing in for them sends what the same points buy it
        var raiders = faction == deserters
            ? Enumerable.Range(0, count)
               .Select(_ => PawnGenerator.GeneratePawn(new PawnGenerationRequest(VFEE_DefOf.VFEE_Deserter, faction, tile: map.Tile, mustBeCapableOfViolence: true)))
               .ToList()
            : PawnGroupMakerUtility.GeneratePawns(new PawnGroupMakerParms
                {
                    faction = faction,
                    groupKind = PawnGroupKindDefOf.Combat,
                    points = count * VFEE_DefOf.VFEE_Deserter.combatPower,
                    tile = map.Tile
                })
               .ToList();
        if (raiders.Count == 0) return;
        var parms = new IncidentParms { target = map, faction = faction };
        if (RCellFinder.TryFindRandomPawnEntryCell(out var entry, map, CellFinder.EdgeRoadChance_Hostile)) parms.spawnCenter = entry;
        var arrival = PawnsArrivalModeDefOf.EdgeWalkIn;
        arrival.Worker.TryResolveRaidSpawnCenter(parms);
        arrival.Worker.Arrive(raiders, parms);
        //Half come for the stellarch by whatever way they can walk, and half breach in around the turrets (LordJob_DeserterAssault)
        var breachers = raiders.Where((_, i) => i % 2 == 1).ToList();
        LordMaker.MakeNewLord(faction, new LordJob_DeserterAssault(stellarch, breach: false), map, raiders.Except(breachers));
        if (breachers.Count > 0) LordMaker.MakeNewLord(faction, new LordJob_DeserterAssault(stellarch, breach: true), map, breachers);
        Find.SignalManager.SendSignal(new Signal(outSignalArrived, new LookTargets(raiders).Named("SUBJECT")));
    }

    //Two deserters for each defender the plot leaves, and one defender is always left
    public static int DesertersFor(float defenders, int plotters) =>
        Mathf.RoundToInt(Mathf.Max(MinDefenders, defenders - plotters) * DesertersPerDefender);

    public static float Defenders(Map map, Pawn stellarch) =>
        PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction.Count(pawn => pawn != stellarch && Defends(pawn, map));

    //The defenders away from the parade's map, named as the parade begins
    public static List<Pawn> DefendersAway(Map map, Pawn stellarch) =>
        PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction
           .Where(pawn => pawn != stellarch && pawn.MapHeld != map && Defends(pawn, map))
           .ToList();

    private static bool Defends(Pawn pawn, Map map) =>
        (pawn.MapHeld == null || pawn.MapHeld == map || !pawn.MapHeld.IsPlayerHome) && !ThingOwnerUtility.ContentsSuspended(pawn.ParentHolder)
     && (CanDefend(pawn) || pawn.IsColonyMech && !pawn.RaceProps.IsWorkMech && !pawn.Downed);

    //The plot's own test for a colonist able to fight (GameComponent_Conspiracy.CanConspire)
    private static bool CanDefend(Pawn pawn) =>
        pawn.IsFreeNonSlaveColonist && !pawn.IsQuestLodger() && !pawn.IsMutant && pawn.DevelopmentalStage.Adult() && !pawn.Downed
     && !pawn.InMentalState && !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref inSignal, "inSignal");
        Scribe_Values.Look(ref outSignalArrived, "outSignalArrived");
        Scribe_References.Look(ref mapParent, "mapParent");
        Scribe_References.Look(ref stellarch, "stellarch");
    }
}
