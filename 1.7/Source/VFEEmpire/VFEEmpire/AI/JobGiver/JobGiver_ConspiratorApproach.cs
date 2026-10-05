using RimWorld;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//Hurry to the stellarch and stay close to her, as anyone worried for her would. The allies gather on the Brutus instead,
//and the Brutus waits for them to gather before going (GameComponent_Conspiracy). Then each takes up position by whoever it
//strikes first, still looking worried for her: close enough to touch them if it fights hand to hand, or, for the Brutus, a
//few steps away with a clear shot at them if it shoots. An ally that shoots keeps at the Brutus's side, and shoots from
//wherever the strike finds it
public class JobGiver_ConspiratorApproach : ThinkNode_JobGiver
{
    private const float FollowRadius = 4f;
    //Re-aimed every two seconds, since she moves
    private const int ReAimTicks = 120;

    protected override Job TryGiveJob(Pawn pawn)
    {
        var conspiracy = GameComponent_Conspiracy.Instance;
        if (conspiracy?.plot is not { } plot) return null;
        var leader = conspiracy.GatheringOn(pawn) is { } brutus && brutus.Map == pawn.Map ? brutus : null;
        if (!plot.gathered && leader != null) return JobGiver_AssassinApproach.KeepToLeader(pawn, leader, LocomotionUrgency.Jog);
        if (conspiracy.WaitingForAllies(pawn)) return null;
        var handToHand = conspiracy.FightsHandToHand(pawn);
        if (pawn != plot.brutus && (!plot.gathered || !handToHand))
            return leader != null ? CloseIn(pawn, leader) : Near(pawn, plot.stellarch.SpawnedParentOrMe, FollowRadius);

        var mark = conspiracy.MarkFor(pawn);
        var spot = mark?.SpawnedParentOrMe;
        if (spot == null || spot.Map != pawn.Map) return null;
        if (handToHand) return CloseIn(pawn, spot);
        return conspiracy.InPosition(pawn, mark) ? null : TakeAim(pawn, spot) ?? Near(pawn, spot, FollowRadius);
    }

    private static Job Near(Pawn pawn, Thing spot, float radius)
    {
        if (spot == null || spot.Map != pawn.Map) return null;
        if (spot is Pawn followee)
            return JobDriver_FollowClose.FarEnoughAndPossibleToStartJob(pawn, followee, radius) ? Follow(followee, radius) : null;

        //She is inside something, a bed with a carrier, a casket or a pod
        if (pawn.Position.InHorDistOf(spot.Position, radius + 1f) || !pawn.CanReach(spot, PathEndMode.Touch, Danger.Deadly)) return null;
        var job = JobMaker.MakeJob(JobDefOf.Goto, CellFinder.RandomClosewalkCellNear(spot.Position, pawn.Map, 2));
        job.locomotionUrgency = LocomotionUrgency.Jog;
        return job;
    }

    private static Job Follow(Pawn followee, float radius)
    {
        var follow = JobMaker.MakeJob(JobDefOf.FollowClose, followee);
        follow.followRadius = radius;
        follow.locomotionUrgency = LocomotionUrgency.Jog;
        follow.expiryInterval = ReAimTicks;
        follow.checkOverrideOnExpire = true;
        return follow;
    }

    //Into touch with the mark and kept there while it moves, standing at its side until the strike (JobDriver_ConspiratorCloseIn),
    //and the allies at the Brutus's the same way. At a run: the parade's guards jog between spots, and would outpace a jog
    private static Job CloseIn(Pawn pawn, Thing spot)
    {
        if (!pawn.CanReach(spot, PathEndMode.Touch, Danger.Deadly)) return null;
        var job = JobMaker.MakeJob(VFEE_DefOf.VFEE_ConspiratorCloseIn, spot);
        job.locomotionUrgency = LocomotionUrgency.Sprint;
        job.expiryInterval = ReAimTicks;
        job.checkOverrideOnExpire = true;
        return job;
    }

    //A cell near enough to the mark with a clear shot at it, found as JobGiver_ConspiratorStrike finds one
    private static Job TakeAim(Pawn pawn, Thing spot)
    {
        var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
        if (verb == null || !CastPositionFinder.TryFindCastPosition(new CastPositionRequest
            {
                caster = pawn,
                target = spot,
                verb = verb,
                maxRangeFromTarget = GameComponent_Conspiracy.ShootingDistanceFor(verb),
                wantCoverFromTarget = false
            }, out var cell) || cell == pawn.Position) return null;
        return GoTo(cell);
    }

    private static Job GoTo(IntVec3 cell)
    {
        var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
        job.locomotionUrgency = LocomotionUrgency.Jog;
        job.expiryInterval = ReAimTicks;
        job.checkOverrideOnExpire = true;
        return job;
    }
}
