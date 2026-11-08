using System.Collections.Generic;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;

namespace Transform.Statue;

/// <summary>
/// Harmony patches for the petrified-statue form:
///  - the rider's network broadcast is buried 30 m underground (GetDataToWrite postfix), so
///    unmodded clients never receive a coordinate inside the live statue;
///  - on modded remote clients the rider's interpolation toward the buried coordinate is
///    skipped and the rider is hidden, so the remote only sees the statue rolling;
///  - the statue player's death / pass-out / warp / fall RPCs are blocked so hazards cannot
///    harm them while transformed;
///  - the end-game flow reverts the form before the end screen or the Airport scene load;
///  - the camera is re-pointed at the statue after MainCameraMovement.LateUpdate.
/// </summary>
[HarmonyPatch]
public static class StatueHarmonyPatches
{
	/// <summary>View ids of statue riders we have hidden on this (modded remote) client, so we
	/// can restore their visibility the moment the owner exits and the mapping self-cleans.</summary>
	private static readonly HashSet<int> _hiddenRiderViewIds = new HashSet<int>();

	private static bool IsRider(Character character)
	{
		return StatueController.IsLocalStatueCharacter(character);
	}

	// ------------------------------------------------------------------
	// Rider protection: death, pass-out, warp, fall. Applied defensively at runtime
	// (see StatuePlugin.ConfigureOptionalCharacterPatches) so a missing/renamed method
	// only skips its patch instead of failing the load.
	// ------------------------------------------------------------------

	internal static bool CharacterRpcDiePrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	internal static bool CharacterRpcSetDeadPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	internal static bool CharacterRpcPassOutPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	internal static bool CharacterDieInstantlyPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	internal static bool CharacterHandleDeathPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	internal static bool CharacterWarpPlayerRpcPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	internal static bool CharacterWarpPlayerPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	/// <summary>Block Fall() on the statue player so collisions cannot knock them out of the form.</summary>
	internal static bool CharacterFallPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	/// <summary>Block RPCA_Fall on the statue player for the same reason.</summary>
	internal static bool CharacterRpcFallPrefix(Character __instance)
	{
		return !IsRider(__instance);
	}

	// ------------------------------------------------------------------
	// End-game (win/lose) protection: revert the statue form BEFORE the end screen
	// iterates characters or the Airport scene loads, so the stats apply to the real
	// player and the Airport scene gets a clean, movable localCharacter.
	// ------------------------------------------------------------------

	internal static void CharacterRPCEndGamePrefix()
	{
		StatueController.ForceExitForEndGame();
	}

	internal static void PeakHandlerEndCutscenePrefix()
	{
		StatueController.ForceExitForEndGame();
	}

	internal static void GameOverHandlerBeginAirportLoadRPCPrefix()
	{
		StatueController.ForceExitForEndGame();
	}

	internal static void EndScreenReturnToAirportPrefix()
	{
		StatueController.ForceExitForEndGame();
	}

	internal static void CharacterStatsWinPrefix(CharacterStats __instance)
	{
		EnsureCharacterStatsTimelinePopulated(__instance);
	}

	private static void EnsureCharacterStatsTimelinePopulated(CharacterStats stats)
	{
		if (stats == null) return;
		try
		{
			if (stats.timelineInfo == null || stats.timelineInfo.Count > 0) return;
			stats.timelineInfo.Add(new EndScreen.TimelineInfo(
				Biome.BiomeType.Peak, CharacterStats.peakHeightInUnits, 0f, EndScreen.TimelineNote.None));
		}
		catch { }
	}

	// ------------------------------------------------------------------
	// Camera fallback: re-point the camera at the statue after the vanilla camera
	// code runs. Registered defensively (see StatuePlugin.ConfigureCameraFallbackPatch).
	// ------------------------------------------------------------------

	internal static void MainCameraMovementLateUpdatePostfix()
	{
		StatueController.ApplyCameraOverrideForLocalStatue();
	}

	// ------------------------------------------------------------------
	// Network sync rewrite for the statue rider (buried body, same approach as
	// the Tumbleweed/Zombie forms).
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(CharacterSyncer), "GetDataToWrite")]
	[HarmonyPostfix]
	private static void CharacterSyncerGetDataToWritePostfix(CharacterSyncer __instance, ref CharacterSyncData __result)
	{
		Character character = __instance.GetComponent<Character>();
		if (character == null || !StatueController.IsLocalStatueCharacter(character))
		{
			return;
		}

		// Bury 30m down, keep x/z so the body stays under the rolling statue.
		float3 hip = __result.hipLocation;
		__result.hipLocation = new float3(hip.x, hip.y - 30f, hip.z);
		__result.averageVelocity = float3.zero;
	}

	// ------------------------------------------------------------------
	// On modded remote clients the statue rider's character is hidden (not interpolated):
	// the syncer's interpolation toward the buried coordinate we broadcast is skipped and
	// the rider's renderers are disabled, so the remote only sees the statue - matching the
	// local player, which is hidden while transformed.
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(CharacterSyncer), "InterpolateRigPositions")]
	[HarmonyPrefix]
	private static bool CharacterSyncerInterpolatePrefix(CharacterSyncer __instance)
	{
		Character character = __instance.GetComponent<Character>();
		// The owner's syncer never reaches this method (its call site is guarded by
		// photonView.IsMine); this guard just makes the intent explicit and harmless.
		if (character == null || character.IsLocal)
		{
			return true;
		}
		if (character.photonView != null)
		{
			int viewId = character.photonView.ViewID;
			if (StatueController.StatueRiderViewIds.Contains(viewId))
			{
				SetCharacterRenderersEnabled(character, false);
				_hiddenRiderViewIds.Add(viewId);
				return false;
			}
			// Not a rider anymore (owner exited, mapping self-cleaned): restore the
			// rider's visibility if we had hidden it.
			if (_hiddenRiderViewIds.Remove(viewId))
			{
				SetCharacterRenderersEnabled(character, true);
			}
		}
		return true;
	}

	private static void SetCharacterRenderersEnabled(Character character, bool enabled)
	{
		if (character == null)
		{
			return;
		}
		try
		{
			foreach (Renderer renderer in ((Component)character).GetComponentsInChildren<Renderer>(true))
			{
				if (renderer != null)
				{
					renderer.enabled = enabled;
				}
			}
		}
		catch
		{
		}
	}
}
