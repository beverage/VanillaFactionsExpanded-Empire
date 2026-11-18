using RimWorld;
using Verse;

namespace VFEEmpire;

//After a plot against the stellarch fails, its plotters who live on in the colony weigh on the stellarch and on everyone
//who loves the stellarch, for up to a year
public class ThoughtWorker_TraitorsAmongUs : ThoughtWorker
{
    protected override ThoughtState CurrentStateInternal(Pawn p)
    {
        var stage = GameComponent_Conspiracy.Instance?.TraitorsAmongUsStage(p) ?? -1;
        return stage < 0 ? ThoughtState.Inactive : ThoughtState.ActiveAtStage(stage);
    }
}
