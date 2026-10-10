using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//Vanilla's breaching toil with the stellarch as its destination. Vanilla aims at a random spot in the colony, and at another
//each time it gets there; this aims at her. Once she is some way from where it is aimed, it aims at her again at the next
//duty update after the wall it was cutting falls, or at once when no wall is left to cut, so the wall being cut is finished.
//Reaching her ends the breach (LordJob_DeserterAssault then hunts her), and anyone vanilla would send at the colony at
//large is sent at her.
public class LordToil_DeserterBreach : LordToil_AssaultColonyBreaching
{
    //How far she may be from the breach's destination before it is aimed at her again
    public const float ReaimDistance = 20f;

    //Vanilla's breach width and walking room for the size of the raid (LordToil_AssaultColonyBreaching's private curves)
    private static readonly SimpleCurve BreachRadius = new() { new CurvePoint(0f, 1f), new CurvePoint(120f, 2f) };
    private static readonly SimpleCurve WalkMargin = new() { new CurvePoint(0f, 2f), new CurvePoint(60f, 4f) };

    private readonly Pawn stellarch;

    //The wall being cut fell since the duties were last set: a moment to aim again without abandoning one half cut
    private bool wallFell;

    public LordToil_DeserterBreach(Pawn stellarch) => this.stellarch = stellarch;

    public override void UpdateAllDuties()
    {
        if (lord.ownedPawns.Count > 0 && stellarch is { Dead: false } && stellarch.MapHeld == Map)
        {
            var target = stellarch.PositionHeld;
            if (!Data.breachDest.IsValid || (target.DistanceTo(Data.breachDest) > ReaimDistance && (wallFell || Data.currentTarget is null or { Destroyed: true })))
                Aim(target);
        }
        wallFell = false;
        base.UpdateAllDuties();
        if (stellarch is not { Spawned: true }) return;
        foreach (var pawn in lord.ownedPawns)
            if (pawn.mindState.duty?.def == DutyDefOf.AssaultColony)
                pawn.mindState.duty = new PawnDuty(DutyDefOf.AssaultThing, stellarch);
    }

    private void Aim(IntVec3 target)
    {
        Data.Reset();
        Data.preferMelee = Rand.Chance(0.5f);
        Data.breachStart = Lead(target).PositionHeld;
        Data.breachDest = target;
        var count = lord.ownedPawns.Count;
        Data.breachingGrid.CreateBreachPath(Data.breachStart, Data.breachDest,
            Mathf.RoundToInt(BreachRadius.Evaluate(count)), Mathf.RoundToInt(WalkMargin.Evaluate(count)), useAvoidGrid);
    }

    //The breach starts from the deserter still on its feet nearest her, not from a straggler or one lying downed at a wall
    private Pawn Lead(IntVec3 target)
    {
        Pawn lead = null;
        foreach (var pawn in lord.ownedPawns)
            if (pawn.Spawned && !pawn.Downed && (lead == null || pawn.Position.DistanceToSquared(target) < lead.Position.DistanceToSquared(target)))
                lead = pawn;
        return lead ?? lord.ownedPawns[0];
    }

    public override void Notify_BuildingDespawnedOnMap(Building b)
    {
        base.Notify_BuildingDespawnedOnMap(b);
        if (b == Data.currentTarget) wallFell = true;
    }

    //Through to where she was aimed at, or a deserter sent at her has reached her: hunt her from here
    public override void Notify_ReachedDutyLocation(Pawn pawn) => lord.ReceiveMemo(LordJob_DeserterAssault.BreachedMemo);
}
