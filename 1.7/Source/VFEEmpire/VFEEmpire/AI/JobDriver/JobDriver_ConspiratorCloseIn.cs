using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//The Brutus closing in on the one it strikes first, still looking worried for her: chased as a melee attack chases, the
//path re-aimed whenever they move, and standing at their side once within touch. Vanilla's FollowClose drops to the
//followee's pace once near and makes for where they are going, which kept the Brutus a few steps off a guard on the move
public class JobDriver_ConspiratorCloseIn : JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedOrNull(TargetIndex.A);
        var closeIn = ToilMaker.MakeToil("CloseIn");
        closeIn.tickIntervalAction = _ =>
        {
            var target = job.GetTarget(TargetIndex.A);
            if (pawn.CanReachImmediate(target, PathEndMode.Touch))
            {
                if (pawn.pather.Moving) pawn.pather.StopDead();
                pawn.rotationTracker.FaceTarget(target);
            }
            else if (!pawn.pather.Moving || pawn.pather.Destination != target)
            {
                if (pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) pawn.pather.StartPath(target, PathEndMode.Touch);
                else EndJobWith(JobCondition.Incompletable);
            }
        };
        closeIn.handlingFacing = true;
        closeIn.defaultCompleteMode = ToilCompleteMode.Never;
        yield return closeIn;
    }
}
