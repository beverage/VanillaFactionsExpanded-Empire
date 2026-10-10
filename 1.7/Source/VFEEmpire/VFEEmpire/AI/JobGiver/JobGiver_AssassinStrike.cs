using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VFEEmpire;

//Kill the duty's pawn: shoot her when there is a clear shot, and with a gun but no shot, move to a place to shoot her
//from, bashing any door in the way. Otherwise close in and fight hand to hand, through doors too: that is the way to her
//when no place to shoot from can be reached, and what a gun does beside her, since vanilla allows no shot at an adjacent
//pawn still on her feet. Every job here ends within seconds to think again, so the shot comes as soon as there is one.
//Finish her if she goes down. If she cannot be reached even through doors, the duty's sapper digs in. A deserter given a
//smuggled EMP grenade throws it first, the moment a blast would catch what guards her, going into reach for it here and
//throwing from wherever it is from the duty's constant node too (JobGiver_AssassinThrow), and a Deathless stellarch down is
//executed rather than hit (AscensionCounters).
public class JobGiver_AssassinStrike : ThinkNode_JobGiver
{
    protected override Job TryGiveJob(Pawn pawn)
    {
        if (pawn.mindState.duty?.focus.Thing is not Pawn target || target.Dead) return null;
        var spot = target.SpawnedParentOrMe;
        if (spot == null || spot.Map != pawn.Map) return null;
        var counter = AscensionCounters.ThrowJob(pawn, target, pawn.GetLord()?.ownedPawns ?? new List<Pawn> { pawn })
                   ?? AscensionCounters.ExecuteJob(pawn, target);
        if (counter != null) return counter;
        var verb = pawn.TryGetAttackVerb(spot, !pawn.IsColonist);
        var ranged = verb != null && !verb.IsMeleeAttack;
        if (ranged && verb.CanHitTarget(spot))
        {
            var shoot = JobMaker.MakeJob(JobDefOf.AttackStatic, spot);
            shoot.maxNumStaticAttacks = 2;
            shoot.endIfCantShootTargetFromCurPos = true;
            shoot.killIncappedTarget = true;
            shoot.expiryInterval = 300;
            return shoot;
        }

        if (!pawn.CanReach(spot, PathEndMode.Touch, Danger.Deadly, true)) return null;
        if (ranged && CastPositionFinder.TryFindCastPosition(new CastPositionRequest
            {
                caster = pawn,
                target = spot,
                verb = verb,
                maxRangeFromTarget = verb.EffectiveRange,
                wantCoverFromTarget = verb.EffectiveRange > 5f
            }, out var cell) && cell != pawn.Position)
        {
            var move = JobMaker.MakeJob(JobDefOf.Goto, cell);
            move.canBashDoors = true;
            move.locomotionUrgency = LocomotionUrgency.Jog;
            move.expiryInterval = 120;
            move.checkOverrideOnExpire = true;
            return move;
        }

        var melee = JobMaker.MakeJob(JobDefOf.AttackMelee, spot);
        melee.canBashDoors = true;
        melee.killIncappedTarget = true;
        melee.expiryInterval = 240;
        melee.checkOverrideOnExpire = true;
        if (spot != target) melee.maxNumMeleeAttacks = 2;
        return melee;
    }
}
