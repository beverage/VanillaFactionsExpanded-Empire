using HarmonyLib;
using RimWorld;
using Verse;

namespace VFEEmpire;

//The stellarch's honor waits for the answer to the decision her death opens, when she dies with the Brutus's plot under way
[HarmonyPatch(typeof(Pawn_RoyaltyTracker), nameof(Pawn_RoyaltyTracker.Notify_PawnKilled))]
public static class Patch_Pawn_RoyaltyTracker_Notify_PawnKilled
{
    [HarmonyPrefix]
    public static bool Prefix(Pawn_RoyaltyTracker __instance) => GameComponent_Conspiracy.Instance?.Notify_TitleHolderKilled(__instance.pawn) ?? true;
}

//Who killed whom matters to the plot: a noble it kills does not fail the parade, and a colonist who kills a conspirator
//after a truce breaks it
[HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
public static class Patch_Pawn_Kill
{
    [HarmonyPrefix]
    public static void Prefix(Pawn __instance, DamageInfo? dinfo) => GameComponent_Conspiracy.Instance?.Notify_PawnKilling(__instance, dinfo);

    [HarmonyPostfix]
    public static void Postfix(Pawn __instance, DamageInfo? dinfo) => GameComponent_Conspiracy.Instance?.Notify_PawnKilled(__instance, dinfo);
}

//An implant can add BrainShock while reacting to EMP without forwarding the DamageInfo to AddHediff. Its downing signal
//then has no instigator. Keep the incoming damage's attacker for that reaction alone, including nested damage calls.
[HarmonyPatch(typeof(HediffComp_ReactOnDamage), nameof(HediffComp_ReactOnDamage.Notify_PawnPostApplyDamage))]
public static class Patch_HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage
{
    [System.ThreadStatic]
    private static (Pawn victim, Thing attacker) current;

    public static Thing InstigatorFor(Pawn victim) => current.victim == victim ? current.attacker : null;

    [HarmonyPrefix]
    public static void Prefix(HediffComp_ReactOnDamage __instance, DamageInfo dinfo, out (Pawn victim, Thing attacker) __state)
    {
        __state = current;
        current = (__instance.Pawn, dinfo.Instigator);
    }

    [HarmonyFinalizer]
    public static void Finalizer((Pawn victim, Thing attacker) __state) => current = __state;
}
