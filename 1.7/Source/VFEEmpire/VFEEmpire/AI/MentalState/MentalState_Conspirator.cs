using RimWorld;
using Verse;
using Verse.AI;

namespace VFEEmpire;

//The Brutus's plot keeps its conspirators in a mental state from the moment they leave their posts, so they cannot be
//drafted or ordered about, and they stay colonists throughout. The plot ends the states, never time.
public abstract class MentalState_Conspirator : MentalState
{
    protected override bool CanEndBeforeMaxDurationNow => false;

    public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;
}

//Making for the stellarch, looking worried for her rather than about to kill her
public class MentalState_ConspiratorApproach : MentalState_Conspirator
{
    public override string InspectLine
    {
        get
        {
            var stellarch = GameComponent_Conspiracy.Instance?.plot?.stellarch;
            return stellarch == null ? base.InspectLine : "VFEE.Conspiracy.Concerned".Translate(stellarch.Named("STELLARCH")).Resolve();
        }
    }
}

//The strike: hostile to her and her guards, and to nobody else, so the rest of the colony stays out of it unless drafted in
public class MentalState_ConspiratorStrike : MentalState_Conspirator
{
    public Pawn target;

    public override bool ForceHostileTo(Thing t) => t is Pawn pawn && GameComponent_Conspiracy.Instance?.IsStrikeTarget(pawn) == true;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref target, "target");
    }
}

//Under the deal, the children and slaves wait out the colony's fight where they are
public class MentalState_SittingOutFight : MentalState
{
    protected override bool CanEndBeforeMaxDurationNow => false;
}
