using Verse;
using Verse.AI.Group;

namespace VFEEmpire;

//Vanilla's violent loss, without the honor guard, whose job is to take the blows, and without anyone the Brutus's plot
//brings down: those are the plot's victims, not the colony's failure. The stellarch's own death still ends the parade.
public class Trigger_ParadeLostViolently : Trigger_PawnLostViolently
{
    public override bool ActivateOn(Lord lord, TriggerSignal signal)
    {
        if (!base.ActivateOn(lord, signal)) return false;
        if (lord.LordJob is not LordJob_Parade parade || signal.Pawn == parade.stellarch) return true;
        if (parade.assignedGuards.Contains(signal.Pawn)) return false;
        var instigator = signal.dinfo.Instigator ?? Patch_HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage.InstigatorFor(signal.Pawn);
        return instigator is not Pawn attacker || GameComponent_Conspiracy.Instance?.IsConspirator(attacker) != true;
    }
}
