using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace VFEEmpire;

[StaticConstructorOnStartup]
public static class DelegationUtility
{
    private static readonly Texture2D DenounceIcon = ContentFinder<Texture2D>.Get("UI/Deserters");

    public static Command_Action DenounceCommand(Pawn pawn, Faction faction, bool alreadyDenounced, System.Action denounce)
    {
        if (!pawn.Spawned || pawn.Dead || faction == null || faction.HostileTo(Faction.OfPlayer)) return null;
        var goodwill = -LordJob_Delegation.WrongDenouncementGoodwill;
        var command = new Command_Action
        {
            defaultLabel = "VFEE.Delegation.Denounce".Translate(),
            defaultDesc = "VFEE.Delegation.DenounceDesc".Translate(pawn.Named("PAWN"), faction.Named("FACTION"), goodwill.Named("GOODWILL")),
            icon = DenounceIcon,
            action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "VFEE.Delegation.DenounceConfirm".Translate(pawn.Named("PAWN"), faction.Named("FACTION"), goodwill.Named("GOODWILL")), denounce, true))
        };
        if (alreadyDenounced) command.Disable("VFEE.Delegation.AlreadyDenounced".Translate(pawn.Named("PAWN")));
        return command;
    }

    //The envoy trait keeps delegates and deserters apart while a delegation is here. It goes once a delegate is lost to its
    //group for any reason, or turns out to be a deserter; a cell leaving for the stellarch keeps it as its disguise
    public static void RemoveEnvoy(Pawn pawn)
    {
        var envoy = pawn?.story?.traits.GetTrait(VFEE_DefOf.VFEE_Envoy);
        if (envoy != null) pawn.story.traits.RemoveTrait(envoy);
    }

    //Deserters smuggle their own weapons in with a delegation and draw them when they reveal themselves: a pick from the
    //VFEE_Deserter kind's kit, by its weapon tags and budget, weighted as vanilla's weapon generation weights them and held
    //to its allow chance too, which keeps out what vanilla never generates (Odyssey's unique weapons share the kit's tags),
    //and only an upgrade on what the deserter came carrying, which it throws down. Biocoded to them, so it is no loot. They
    //keep the clothes they came in. Any other weapon they carry, anything they could wield, is thrown down too, so they
    //fight with their best: a sidearms mod would otherwise switch back to one of those the moment they closed to melee. What
    //they throw down is forbidden, as a downed pawn's gear is, so nobody runs into the fight to haul it.
    public static void DrawSmuggledWeapon(Pawn pawn)
    {
        var kit = VFEE_DefOf.VFEE_Deserter;
        if (kit?.weaponTags == null || pawn.equipment == null || pawn.inventory == null || !pawn.RaceProps.ToolUser || !pawn.Spawned
         || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return;
        var budget = kit.weaponMoney.RandomInRange;
        var carried = pawn.equipment.Primary;
        var worth = carried?.MarketValue ?? 0f;
        var candidates = PawnWeaponGenerator.AllWeaponPairs.Where(pair => pair.Price <= budget && pair.Price > worth
            && kit.weaponTags.Any(tag => pair.thing.weaponTags.Contains(tag))
            && (!pair.thing.IsRangedWeapon || !pawn.WorkTagIsDisabled(WorkTags.Shooting))
            && (pair.stuff == null || pair.stuff.stuffProps.allowedInStuffGeneration)
            && Rand.Chance(pair.thing.generateAllowChance)).ToList();
        if (candidates.TryRandomElementByWeight(pair => pair.Commonality * pair.Price, out var pick))
        {
            var weapon = (ThingWithComps)ThingMaker.MakeThing(pick.thing, pick.stuff);
            PawnGenerator.PostProcessGeneratedGear(weapon, pawn);
            weapon.TryGetComp<CompBiocodable>()?.CodeFor(pawn);
            if (carried != null) pawn.equipment.TryDropEquipment(carried, out _, pawn.Position);
            pawn.equipment.AddEquipment(weapon);
        }

        foreach (var spare in pawn.inventory.innerContainer.Where(thing => thing.def.IsWeapon && thing.TryGetComp<CompEquippable>() != null).ToList())
            pawn.inventory.innerContainer.TryDrop(spare, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _,
                (dropped, _) => dropped.SetForbidden(true, false));
    }
}
