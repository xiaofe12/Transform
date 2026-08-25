using System;
using BepInEx.Logging;
using UnityEngine;

namespace Transform.Core;

/// <summary>Shared guard that prevents carrying another player through a transform.</summary>
internal static class CarryGuard
{
    internal static void DropBeforeTransform(Character character, ManualLogSource log)
    {
        if (character == null || character.data == null) return;

        bool carrying = false;
        bool carried = false;
        try
        {
            carrying = character.data.IsCarryingCharacter || character.data.carriedPlayer != null;
            carried = character.data.isCarried;
        }
        catch
        {
            return;
        }

        if (!carrying && !carried) return;

        Vector3 anchor = GetSafeAnchor(character);
        try
        {
            character.BreakCharacterCarrying(true);
            log?.LogInfo("[Transform] Dropped carried player before transform at " + anchor + ".");
        }
        catch (Exception ex)
        {
            log?.LogWarning("[Transform] Failed to drop carried player before transform: " + ex.Message);
        }
    }

    internal static bool CanStartCarry(Character carrier, Character target)
    {
        if (!TransformState.AnyFormActive) return true;
        if (carrier == null) return true;
        if (carrier != Character.localCharacter) return true;

        DropBeforeTransform(carrier, TransformPlugin.Log);
        return false;
    }

    private static Vector3 GetSafeAnchor(Character character)
    {
        try
        {
            Vector3 center = character.Center;
            if (IsFinite(center)) return center;
        }
        catch
        {
        }
        return character.transform != null ? character.transform.position : Vector3.zero;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
