using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace VFEEmpire;

//One half of the parade's deserter raid, and both halves are after the stellarch. The hunters go for her by whatever way
//they can walk. The breachers cut their way toward her, steering around the turrets' fire as a smart raid does
//(LordToil_DeserterBreach), and hunt her as the others do once they are through. Both leave once she is dead or gone from
//the map; carried, or aboard a shuttle still on the ground, she is still there to be hunted.
public class LordJob_DeserterAssault : LordJob
{
    public const string BreachedMemo = "VFEE_DesertersBreached";

    public Pawn stellarch;
    public bool breach;

    public LordJob_DeserterAssault() { }

    public LordJob_DeserterAssault(Pawn stellarch, bool breach)
    {
        this.stellarch = stellarch;
        this.breach = breach;
    }

    public override StateGraph CreateGraph()
    {
        var graph = new StateGraph();
        var hunt = new LordToil_AssaultThings(stellarch == null ? new List<Thing>() : new List<Thing> { stellarch }) { useAvoidGrid = breach };
        var gone = new Transition(hunt, new LordToil_ExitMapAndDefendSelf { useAvoidGrid = true });
        gone.AddTrigger(new Trigger_StellarchGone(stellarch));
        if (breach)
        {
            var cut = graph.StartingToil = new LordToil_DeserterBreach(stellarch) { useAvoidGrid = true };
            graph.AddToil(hunt);
            var through = new Transition(cut, hunt);
            through.AddTrigger(new Trigger_Memo(BreachedMemo));
            through.AddPostAction(new TransitionAction_EndAllJobs());
            graph.AddTransition(through);
            gone.AddSource(cut);
        }
        else graph.StartingToil = hunt;
        graph.AddToil(gone.target);
        graph.AddTransition(gone);
        return graph;
    }

    public override void ExposeData()
    {
        Scribe_References.Look(ref stellarch, "stellarch");
        Scribe_Values.Look(ref breach, "breach");
    }
}

//She is dead, or no longer on the lord's map. Carried, in a bed or aboard a shuttle on the ground, she is still there
public class Trigger_StellarchGone : Trigger
{
    private readonly Pawn stellarch;

    public Trigger_StellarchGone(Pawn stellarch) => this.stellarch = stellarch;

    public override bool ActivateOn(Lord lord, TriggerSignal signal) =>
        signal.type == TriggerSignalType.Tick && (stellarch == null || stellarch.Dead || stellarch.MapHeld != lord.Map);
}
