using System.Linq;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VFEEmpire;

//Disguised assassins going for the stellarch as one group. They first gather on the one leading them, then walk to her
//behind it, and reveal themselves together once one of them is at striking distance with all of them beside the leader,
//so none is left a room behind with a door to break. A straggler holds up neither the gathering nor the strike for long.
//They reveal themselves too if she is shut away where even a guest's open doors cannot take them, to break in instead
public class LordToil_AssassinApproach : LordToil
{
    public const string StrikeMemo = "VFEE_AssassinStrike";
    //Every half second, and the lord job checks as often whether she is gone (LordJob_DelegationAssassin.TargetGone)
    public const int CheckInterval = 30;
    private const float StrikeRadius = 12f;
    private const float OutdoorStrikeRadius = 6f;
    //Two cells, diagonals included
    private const float BesideRadius = 2.9f;
    //Together is every one of them on their feet within this of the leader
    private const float GroupRadius = 5f;
    //Half an hour
    private const int StragglerWaitTicks = 1250;

    //Not saved: who led when the duties were last handed out
    private Pawn dutiesLeader;

    public LordToil_AssassinApproach(Pawn target) =>
        data = new LordToilData_PawnTarget
        {
            Target = target
        };

    private Pawn Target => ((LordToilData_PawnTarget)data).Target;

    private LordJob_DelegationAssassin Job => (LordJob_DelegationAssassin)lord.LordJob;

    public override bool AllowSatisfyLongNeeds => false;

    //The first of them still on their feet leads, and the others follow it
    private Pawn Leader() => lord.ownedPawns.FirstOrDefault(pawn => pawn.Spawned && !pawn.Downed);

    //In the leader's room too, the open map counting as one, so none has a door to get through once they strike
    private bool Together(Pawn leader)
    {
        var room = leader.GetRoom();
        return lord.ownedPawns.All(pawn => pawn == leader || !pawn.Spawned || pawn.Downed
                                        || pawn.GetRoom() == room && pawn.Position.InHorDistOf(leader.Position, GroupRadius));
    }

    //Within two cells of her, or in her room with a clear line to her. Outdoors, the "room" is the whole open map, so close
    //enough for the first shots to count
    public static bool InStrikingDistance(Pawn pawn, Thing spot)
    {
        var cell = spot.PositionHeld;
        if (pawn.Position.InHorDistOf(cell, BesideRadius)) return true;
        var room = pawn.GetRoom();
        if (room == null || room != cell.GetRoom(pawn.Map)) return false;
        return pawn.Position.InHorDistOf(cell, room.PsychologicallyOutdoors ? OutdoorStrikeRadius : StrikeRadius) && GenSight.LineOfSight(pawn.Position, cell, pawn.Map);
    }

    //The duty walks a pawn to her: the leader waits for the others while they gather and then goes, and the others keep
    //to the leader, its second focus (JobGiver_AssassinApproach)
    public override void UpdateAllDuties()
    {
        var leader = Leader();
        var gathered = Job.gathered;
        foreach (var pawn in lord.ownedPawns)
        {
            pawn.mindState.duty = pawn == leader
                ? new PawnDuty(VFEE_DefOf.VFEE_AssassinApproach, gathered ? (LocalTargetInfo)Target : LocalTargetInfo.Invalid)
                : new PawnDuty(VFEE_DefOf.VFEE_AssassinApproach, Target, leader != null ? (LocalTargetInfo)leader : LocalTargetInfo.Invalid);
            pawn.mindState.duty.locomotion = LocomotionUrgency.Walk;
        }

        dutiesLeader = leader;
    }

    public override void LordToilTick()
    {
        base.LordToilTick();
        if (Find.TickManager.TicksGame % CheckInterval != 0) return;
        var job = Job;
        //Once she is gone the job's own transition takes them away, after this tick
        if (job.TargetGone) return;
        var leader = Leader();
        if (leader == null) return;
        if (leader != dutiesLeader) UpdateAllDuties();
        var now = Find.TickManager.TicksGame;
        var together = Together(leader);
        if (!job.gathered)
        {
            if (job.gatherStartTick < 0) job.gatherStartTick = now;
            if (!together && now - job.gatherStartTick < StragglerWaitTicks) return;
            job.gathered = true;
            UpdateAllDuties();
        }

        var spot = Target.SpawnedParentOrMe;
        var chance = !leader.CanReach(spot, PathEndMode.Touch, Danger.Deadly)
                  || lord.ownedPawns.Any(pawn => pawn.Spawned && !pawn.Downed && InStrikingDistance(pawn, spot));
        if (!chance)
        {
            job.chanceSinceTick = -1;
            return;
        }

        if (job.chanceSinceTick < 0) job.chanceSinceTick = now;
        if (together || now - job.chanceSinceTick >= StragglerWaitTicks) lord.ReceiveMemo(StrikeMemo);
    }
}
