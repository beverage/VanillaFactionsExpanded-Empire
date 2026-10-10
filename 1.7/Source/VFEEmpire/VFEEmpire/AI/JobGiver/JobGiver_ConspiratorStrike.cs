using RimWorld;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//Cut down the stellarch's guards, spread across them, then her, though on Losing is fun the Brutus goes straight for her
//(GameComponent_Conspiracy.StrikeTargetFor). Shoot when there is a clear shot, move for one when there is not, and close in
//to fight hand to hand when there is nowhere to shoot from. A conspirator that fights best hand to hand does so whatever
//it carries, unless there is no way to its target. Only she is finished off once down. A conspirator the deserters gave an
//EMP grenade throws it first, the moment a blast would catch what of hers fights the plot, and a Deathless stellarch down is
//executed rather than hit (AscensionCounters).
public class JobGiver_ConspiratorStrike : ThinkNode_JobGiver
{
    protected override Job TryGiveJob(Pawn pawn)
    {
        if (pawn.MentalState is not MentalState_ConspiratorStrike state) return null;
        var conspiracy = GameComponent_Conspiracy.Instance;
        var target = state.target = conspiracy?.StrikeTargetFor(pawn, state.target);
        if (target == null) return null;
        var spot = target.SpawnedParentOrMe;
        if (spot == null || spot.Map != pawn.Map) return null;
        var finish = target == conspiracy.plot.stellarch;
        var counter = AscensionCounters.ThrowJob(pawn, conspiracy.plot.stellarch, conspiracy.plot.Conspirators, plot: true)
                   ?? (finish ? AscensionCounters.ExecuteJob(pawn, target) : null);
        if (counter != null) return counter;
        var verb = pawn.TryGetAttackVerb(spot);
        var handToHand = conspiracy.FightsHandToHand(pawn) && pawn.CanReach(spot, PathEndMode.Touch, Danger.Deadly, true);
        if (verb != null && !verb.IsMeleeAttack && !handToHand)
        {
            if (verb.CanHitTarget(spot))
            {
                var shoot = JobMaker.MakeJob(JobDefOf.AttackStatic, spot);
                shoot.maxNumStaticAttacks = 2;
                shoot.endIfCantShootTargetFromCurPos = true;
                shoot.killIncappedTarget = finish;
                shoot.expiryInterval = 300;
                return shoot;
            }

            if (CastPositionFinder.TryFindCastPosition(new CastPositionRequest
                {
                    caster = pawn,
                    target = spot,
                    verb = verb,
                    maxRangeFromTarget = verb.EffectiveRange,
                    wantCoverFromTarget = false
                }, out var cell) && cell != pawn.Position)
            {
                var move = JobMaker.MakeJob(JobDefOf.Goto, cell);
                move.locomotionUrgency = LocomotionUrgency.Jog;
                move.expiryInterval = 120;
                move.checkOverrideOnExpire = true;
                return move;
            }
        }

        if (!pawn.CanReach(spot, PathEndMode.Touch, Danger.Deadly, true)) return null;
        var melee = JobMaker.MakeJob(JobDefOf.AttackMelee, spot);
        melee.canBashDoors = true;
        melee.killIncappedTarget = finish;
        melee.expiryInterval = 240;
        melee.checkOverrideOnExpire = true;
        if (spot != target) melee.maxNumMeleeAttacks = 2;
        return melee;
    }
}
