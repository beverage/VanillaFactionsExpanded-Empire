using RimWorld;
using Verse;

namespace VFEEmpire;

//After a truce, everyone thinks less of the Brutus for as long as the Brutus stays in the colony, whatever the Brutus's rank;
//on the Normal difficulty for a year at most
public class ThoughtWorker_StellarchsKiller : ThoughtWorker
{
    protected override ThoughtState CurrentSocialStateInternal(Pawn p, Pawn otherPawn) =>
        p != otherPawn && GameComponent_Conspiracy.Instance?.IsTruceBrutus(otherPawn) == true;
}
