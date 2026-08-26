using System;
using HarmonyLib;

namespace Transform.Core;

/// <summary>
/// Game-wide guards applied while the local player is transformed into ANY form, driven by the
/// global <see cref="TransformState.AnyFormActive"/> mirror:
///
///  - <see cref="GUIManager.OpenBackpackWheel"/> is blocked so the item backpack wheel cannot be
///    opened while transformed (the game itself only blocks it while windowBlockingInput is set);
///  - <see cref="CharacterItems.DoUsing"/> and <see cref="CharacterItems.DoSwitching"/> are blocked
///    for the LOCAL character only — remote players keep their full item behaviour. This covers the
///    scoutmaster form especially, whose controlled character is driven by the vanilla input
///    pipeline and would otherwise still switch slots / use items with the number keys;
///  - <see cref="CharacterHeatEmission.Update"/> exceptions are swallowed while a form is active:
///    the old standalone-mod session log (out.txt) showed it throwing
///    InvalidOperationException("Collection was modified") when form transitions mutate
///    Character.AllCharacters mid-iteration. Swallowing only while transformed keeps vanilla
///    error behaviour intact for untransformed play.
///
/// The aiming reticle is suppressed by the zombie module's existing GUIManager.UpdateReticle patch
/// (gated on the same global flag), and the HUD itself is filtered by <see cref="TransformHud"/>.
///
/// <see cref="CharacterData.UpdateHasParachute"/>, <see cref="CharacterBackpackHandler.LateUpdate"/>
/// and <see cref="ReverbCapZone.Update"/> NRE guards live in the Wind module's patch class
/// (WindHarmonyPatches) — they were proven against the same log and are not duplicated here.
/// </summary>
[HarmonyPatch]
internal static class TransformHarmonyPatches
{
    // ---- Menu input gating -------------------------------------------------------
    // The transform menu must freeze the character and reveal the cursor while open. The game's
    // own flags are the right lever: Character.CanDoInput reads GUIManager.windowBlockingInput
    // (gates movement, items, interactions), and CursorHandler.Update reads
    // GUIManager.windowShowingCursor (reveals + unlocks the cursor). But both properties have
    // PRIVATE setters and GUIManager.LateUpdate -> UpdateWindowStatus recomputes them from
    // MenuWindow.AllActiveWindows EVERY frame, so storing a value (even via reflection) would be
    // clobbered immediately. Postfixing the getters instead covers every consumer at the read
    // site and is a no-op the moment the menu closes.

    [HarmonyPatch(typeof(GUIManager), nameof(GUIManager.windowBlockingInput), MethodType.Getter)]
    [HarmonyPostfix]
    private static void WindowBlockingInputPostfix(ref bool __result)
    {
        if (TransformState.MenuOpen) __result = true;
    }

    [HarmonyPatch(typeof(GUIManager), nameof(GUIManager.windowShowingCursor), MethodType.Getter)]
    [HarmonyPostfix]
    private static void WindowShowingCursorPostfix(ref bool __result)
    {
        if (TransformState.MenuOpen) __result = true;
    }

    // ---- Game-wide guards while transformed ---------------------------------------

    [HarmonyPatch(typeof(GUIManager), "OpenBackpackWheel")]
    [HarmonyPrefix]
    private static bool OpenBackpackWheelPrefix()
    {
        // GUIManager is the LOCAL player's UI only — blocking globally while transformed is safe.
        return !TransformState.AnyFormActive;
    }

    [HarmonyPatch(typeof(CharacterItems), "DoUsing")]
    [HarmonyPrefix]
    private static bool CharacterItemsDoUsingPrefix(Character ___character)
    {
        return !IsLocalTransformedCharacter(___character);
    }

    [HarmonyPatch(typeof(CharacterItems), "DoSwitching")]
    [HarmonyPrefix]
    private static bool CharacterItemsDoSwitchingPrefix(Character ___character)
    {
        return !IsLocalTransformedCharacter(___character);
    }

    [HarmonyPatch(typeof(CharacterItems), "Update")]
    [HarmonyPriority(Priority.High)]
    [HarmonyPrefix]
    private static bool CharacterItemsUpdateReadyPrefix(CharacterItems __instance, Character ___character)
    {
        return IsCharacterItemsUpdateReady(___character, __instance);
    }

    [HarmonyPatch(typeof(CharacterItems), "Update")]
    [HarmonyFinalizer]
    private static Exception CharacterItemsUpdateFinalizer(Exception __exception, Character ___character, CharacterItems __instance)
    {
        if (__exception == null) return null;
        if (!(__exception is NullReferenceException)) return __exception;
        if (TransformState.AnyFormActive) return null;
        if (!IsCharacterItemsUpdateReady(___character, __instance)) return null;
        return __exception;
    }

    [HarmonyPatch(typeof(CharacterCarrying), "StartCarry")]
    [HarmonyPrefix]
    private static bool CharacterCarryingStartCarryPrefix(Character ___character, Character target)
    {
        return CarryGuard.CanStartCarry(___character, target);
    }

    // CharacterHeatEmission.Update iterates the character list; form transitions can add/remove
    // entries mid-iteration (see the old out.txt session log). Swallow the failure only while a
    // form is active — one missed heat tick is harmless, an unhandled exception every frame is not.
    [HarmonyPatch(typeof(CharacterHeatEmission), "Update")]
    [HarmonyFinalizer]
    private static Exception CharacterHeatEmissionUpdateFinalizer(Exception __exception)
    {
        if (__exception == null) return null;
        return TransformState.AnyFormActive ? null : __exception;
    }

    // GUIManager.get_canEmote (read every frame by UpdateEmoteWheel) does
    // localCharacter.data.dead / localCharacter.refs.stats.won with no null checks. While a form
    // is active localCharacter can be an NPC-prefab character (zombie/scoutmaster) or a character
    // whose refs are mid-swap, which NREs every frame. Skip the getter (emotes disabled) whenever
    // those refs are not a full player's refs.
    // NOTE: canEmote is not public, so this patch is installed MANUALLY from TransformPlugin.Awake
    // (see InstallCanEmoteGuard) instead of through PatchAll. Deliberately NO [HarmonyPrefix]
    // attribute here: PatchAll scans every annotated method in the class, and a prefix without a
    // [HarmonyPatch] target throws "Undefined target method" and aborts the WHOLE class patch run
    // (killing every patch declared after it). Manual install uses AccessTools.Method directly.
    private static bool GUIManagerCanEmotePrefix()
    {
        try
        {
            if (TransformState.AnyFormActive) return false;
            Character local = Character.localCharacter;
            if (local == null) return true; // vanilla handles null fine (its first branch)
            if (local.data == null) return false;
            if (local.refs == null || local.refs.stats == null) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCharacterItemsUpdateReady(Character character, CharacterItems items)
    {
        if (character == null && items != null)
        {
            character = items.GetComponent<Character>();
        }
        if (character == null || character.data == null || character.refs == null)
        {
            return false;
        }

        try
        {
            Player player = character.player;
            return player != null && player.itemSlots != null && player.backpackSlot != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool GUIManagerUpdateEmoteWheelPrefix(GUIManager __instance)
    {
        if (!TransformState.AnyFormActive) return true;

        try
        {
            if (__instance != null && __instance.emoteWheel != null)
            {
                __instance.emoteWheel.SetActive(false);
            }

            Character local = Character.localCharacter;
            if (local != null && local.data != null)
            {
                local.data.usingEmoteWheel = false;
            }
        }
        catch
        {
            // UI refs can be rebuilt during scene/menu transitions; skip only this frame.
        }

        return false;
    }
    private static bool IsLocalTransformedCharacter(Character character)
    {
        // Every form parks the player-controlled character in Character.localCharacter (physics
        // forms keep the original character; zombie/scoutmaster swap it to their own), so this
        // matches exactly the character the local player is steering. Remote players never match
        // and keep their vanilla item behaviour.
        return TransformState.AnyFormActive
            && character != null
            && character == Character.localCharacter;
    }


    private static Exception CinemaCameraUpdateFinalizer(Exception __exception)
    {
        if (__exception == null) return null;
        if (!(__exception is NullReferenceException)) return __exception;

        string stack = __exception.StackTrace ?? string.Empty;
        if (stack.IndexOf("PeakCinema.Plugin.ApplyPlayerVisibility", StringComparison.Ordinal) >= 0
            || stack.IndexOf("PeakCinema.Plugin.CinemaCameraFix", StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        return __exception;
    }

    private static bool PeakCinemaApplyPlayerVisibilityPrefix()
    {
        if (TransformState.AnyFormActive) return false;

        try
        {
            if (Character.AllCharacters == null) return false;

            foreach (Character character in Character.AllCharacters)
            {
                if (character == null || !character.IsLocal) continue;
                return character.refs != null && character.refs.customization != null;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static void InstallPeakCinemaGuard(Harmony harmony)
    {
        if (harmony == null) return;

        Type cameraType = AccessTools.TypeByName("CinemaCamera");
        if (cameraType != null)
        {
            System.Reflection.MethodInfo update = AccessTools.Method(cameraType, "Update");
            System.Reflection.MethodInfo finalizer = AccessTools.Method(typeof(TransformHarmonyPatches), nameof(CinemaCameraUpdateFinalizer));
            if (update != null && finalizer != null)
            {
                harmony.Patch(update, finalizer: new HarmonyMethod(finalizer));
            }
        }

        Type pluginType = AccessTools.TypeByName("PeakCinema.Plugin");
        if (pluginType == null) return;

        System.Reflection.MethodInfo applyVisibility = AccessTools.Method(pluginType, "ApplyPlayerVisibility");
        System.Reflection.MethodInfo prefix = AccessTools.Method(typeof(TransformHarmonyPatches), nameof(PeakCinemaApplyPlayerVisibilityPrefix));
        if (applyVisibility == null || prefix == null) return;

        harmony.Patch(applyVisibility, prefix: new HarmonyMethod(prefix));
    }

    /// <summary>Manually installs the canEmote guard (the getter is not public, so PatchAll
    /// cannot target it). Called from TransformPlugin.Awake inside a RunGuarded step.</summary>
    internal static void InstallCanEmoteGuard(Harmony harmony)
    {
        System.Reflection.MethodInfo getter = AccessTools.PropertyGetter(typeof(GUIManager), "canEmote");
        if (getter == null) return; // member renamed by a game update — degrade quietly
        harmony.Patch(getter, prefix: new HarmonyMethod(AccessTools.Method(
            typeof(TransformHarmonyPatches), nameof(GUIManagerCanEmotePrefix))));
    }

    /// <summary>Manually installs the emote-wheel guard because UpdateEmoteWheel is private.</summary>
    internal static void InstallEmoteWheelGuard(Harmony harmony)
    {
        if (harmony == null) return;

        System.Reflection.MethodInfo updateEmoteWheel = AccessTools.Method(typeof(GUIManager), "UpdateEmoteWheel");
        System.Reflection.MethodInfo prefix = AccessTools.Method(typeof(TransformHarmonyPatches), nameof(GUIManagerUpdateEmoteWheelPrefix));
        if (updateEmoteWheel == null || prefix == null) return;

        harmony.Patch(updateEmoteWheel, prefix: new HarmonyMethod(prefix));
    }
}
