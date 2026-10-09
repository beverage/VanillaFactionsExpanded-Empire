using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace VFEEmpire;

//A plotter striking at the stellarch is a colonist in a mental state, hostile to her and her guards alone. Vanilla's drafted
//attack options only offer a target hostile to the player's faction, so no drafted pawn could be ordered at a plotter: the
//guards could only shoot at what came to them, and a melee guard could do nothing. These are vanilla's own attack orders,
//offered on a striking plotter to any drafted pawn, so the guards and anyone the player drafts in can be sent at the plot
//while the rest of the colony stays out of it
public class FloatMenuOptionProvider_AttackConspirator : FloatMenuOptionProvider
{
    protected override bool Drafted => true;

    protected override bool Undrafted => false;

    protected override bool Multiselect => true;

    protected override bool MechanoidCanDo => true;

    public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
    {
        if (clickedThing is not Pawn { MentalState: MentalState_ConspiratorStrike } plotter) yield break;

        if (context.IsMultiselect)
        {
            var orders = context.ValidSelectedPawns.Select(pawn => AttackAction(pawn, plotter)).Where(order => order != null).ToList();
            if (orders.Count > 0)
                yield return Option("Attack".Translate(plotter.Label, plotter), () => orders.ForEach(order => order()), null, plotter,
                    FloatMenuUtility.UseRangedAttack(context.FirstSelectedPawn) ? FleckDefOf.FeedbackShoot : FleckDefOf.FeedbackMelee, 40f);
            yield break;
        }

        var selected = context.FirstSelectedPawn;
        if (FloatMenuUtility.UseRangedAttack(selected))
            yield return Option("FireAt".Translate(plotter.Label, plotter), FloatMenuUtility.GetRangedAttackAction(selected, plotter, out var rangedFail),
                rangedFail, plotter, FleckDefOf.FeedbackShoot, 40f);
        yield return Option((plotter.Downed ? "MeleeAttackToDeath" : "MeleeAttack").Translate(plotter.Label, plotter),
            FloatMenuUtility.GetMeleeAttackAction(selected, plotter, out var meleeFail), meleeFail, plotter, FleckDefOf.FeedbackMelee, 30f);
    }

    private static Action AttackAction(Pawn pawn, Pawn plotter) => FloatMenuUtility.UseRangedAttack(pawn)
        ? FloatMenuUtility.GetRangedAttackAction(pawn, plotter, out _)
        : FloatMenuUtility.GetMeleeAttackAction(pawn, plotter, out _);

    //As vanilla's for an enemy: a right click takes it at once, with the same feedback
    private static FloatMenuOption Option(string label, Action action, string failStr, Pawn plotter, FleckDef fleck, float autoTakeablePriority) =>
        action == null
            ? new FloatMenuOption(label + ": " + failStr, null)
            : new FloatMenuOption(label, () =>
            {
                FleckMaker.Static(plotter.DrawPos, plotter.Map, fleck);
                action();
            }, MenuOptionPriority.AttackEnemy)
            {
                autoTakeable = true,
                autoTakeablePriority = autoTakeablePriority
            };
}
