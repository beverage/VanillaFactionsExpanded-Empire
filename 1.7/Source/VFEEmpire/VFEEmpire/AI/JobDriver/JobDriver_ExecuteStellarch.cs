using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//Making sure of a downed stellarch the Deathless gene keeps alive (AscensionCounters.ExecuteJob): up to her, a moment's work,
//and vanilla's execution cut, which takes a Deathless pawn's brain and kills with the executioner as the killer, so a
//conspirator's cut counts as the plot's (GameComponent_Conspiracy.Notify_PawnKilling)
public class JobDriver_ExecuteStellarch : JobDriver
{
    private const int CutTicks = 60;

    private Pawn Victim => (Pawn)job.targetA.Thing;

    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedOrNull(TargetIndex.A);
        this.FailOn(() => Victim.Dead || !Victim.Downed);
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
        yield return Toils_General.WaitWith(TargetIndex.A, CutTicks, true, face: TargetIndex.A);
        yield return Toils_General.Do(() => ExecutionUtility.DoExecutionByCut(pawn, Victim));
    }
}
