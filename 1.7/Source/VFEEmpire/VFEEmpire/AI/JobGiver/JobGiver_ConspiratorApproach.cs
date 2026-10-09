using RimWorld;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//Hurry to the stellarch and stay close to her, as anyone worried for her would. The allies keep to the Brutus instead, and
//the Brutus waits for them to gather before going (GameComponent_Conspiracy)
public class JobGiver_ConspiratorApproach : ThinkNode_JobGiver
{
    private const float FollowRadius = 4f;

    protected override Job TryGiveJob(Pawn pawn)
    {
        var conspiracy = GameComponent_Conspiracy.Instance;
        if (conspiracy?.GatheringOn(pawn) is { } brutus && brutus.Map == pawn.Map)
            return JobGiver_AssassinApproach.KeepToLeader(pawn, brutus, LocomotionUrgency.Jog);
        if (conspiracy?.WaitingForAllies(pawn) == true) return null;
        var spot = conspiracy?.plot?.stellarch?.SpawnedParentOrMe;
        if (spot == null || spot.Map != pawn.Map) return null;
        if (spot is Pawn followee)
        {
            if (!JobDriver_FollowClose.FarEnoughAndPossibleToStartJob(pawn, followee, FollowRadius)) return null;
            var follow = JobMaker.MakeJob(JobDefOf.FollowClose, followee);
            follow.followRadius = FollowRadius;
            follow.locomotionUrgency = LocomotionUrgency.Jog;
            follow.expiryInterval = 120;
            follow.checkOverrideOnExpire = true;
            return follow;
        }

        //She is inside something, a bed with a carrier, a casket or a pod
        if (pawn.Position.InHorDistOf(spot.Position, FollowRadius + 1f) || !pawn.CanReach(spot, PathEndMode.Touch, Danger.Deadly)) return null;
        var job = JobMaker.MakeJob(JobDefOf.Goto, CellFinder.RandomClosewalkCellNear(spot.Position, pawn.Map, 2));
        job.locomotionUrgency = LocomotionUrgency.Jog;
        return job;
    }
}
