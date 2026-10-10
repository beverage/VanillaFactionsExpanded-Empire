using System.Collections.Generic;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VFEEmpire;

//A deserter with a smuggled EMP grenade throws it the moment a blast would catch what guards her, whatever it is doing. This
//runs from the duty's constant node, which the game asks every 30 ticks and which cuts into any job: a deserter a mech is
//hitting answers each blow (JobGiver_ReactToCloseMeleeThreat, above every duty in vanilla's tree), so the duty's own throw
//(JobGiver_AssassinStrike) would never come while the mechs it is for stand beside it. Going into reach is left to the duty
public class JobGiver_AssassinThrow : ThinkNode_JobGiver
{
    protected override Job TryGiveJob(Pawn pawn)
    {
        if (pawn.mindState.duty?.focus.Thing is not Pawn target || target.Dead) return null;
        return AscensionCounters.ThrowJob(pawn, target, pawn.GetLord()?.ownedPawns ?? new List<Pawn> { pawn }, false);
    }
}
