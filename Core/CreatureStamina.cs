using HarmonyLib;

namespace Transform.Core;

/// <summary>Use native unlimited stamina only while controlling a zombie or Scoutmaster.</summary>
[HarmonyPatch]
internal static class CreatureStamina
{
    private static bool IsUnrestrictedCreature(Character character)
    {
        return character != null
            && (ImZombie.ZombieController.ActiveZombieCharacter == character
                || ImScoutmaster.Plugin.IsLocallyControlledScoutmasterCharacter(character));
    }

    // Read-time override avoids persistent flags on players/pools, and survives sugar-rush expiry.
    [HarmonyPatch(typeof(Character), nameof(Character.infiniteStam), MethodType.Getter)]
    [HarmonyPostfix]
    internal static void InfiniteStaminaPostfix(Character __instance, ref bool __result)
    {
        if (IsUnrestrictedCreature(__instance)) __result = true;
    }

    // Native climbing speed still reads the numeric stamina total even in unlimited mode.
    [HarmonyPatch(typeof(Character), nameof(Character.GetTotalStamina))]
    [HarmonyPostfix]
    internal static void TotalStaminaPostfix(Character __instance, ref float __result)
    {
        if (IsUnrestrictedCreature(__instance)) __result = 1f;
    }
}
