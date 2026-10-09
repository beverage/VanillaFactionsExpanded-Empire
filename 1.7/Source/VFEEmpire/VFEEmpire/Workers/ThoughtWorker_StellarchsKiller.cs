using RimWorld;
using Verse;

namespace VFEEmpire;

//After a truce, everyone thinks less of the Brutus for as long as the Brutus holds the colony's highest title
public class ThoughtWorker_StellarchsKiller : ThoughtWorker
{
    protected override ThoughtState CurrentSocialStateInternal(Pawn p, Pawn otherPawn) =>
        p != otherPawn && GameComponent_Conspiracy.Instance?.IsHighestTruceBrutus(otherPawn) == true;
}
