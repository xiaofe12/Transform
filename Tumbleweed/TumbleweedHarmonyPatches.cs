using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Photon.Pun;
using Unity.Mathematics;
using UnityEngine;

namespace ImTumbleweed;

/// <summary>
/// While the local player is in tumbleweed form:
///  - the spawned weed's vanilla chase AI, self-destruct timer and scale-in animation are
///    suppressed on every modded client (identified through the PhotonView.InstantiationData
///    marker), so the weed is fully player-driven and stays alive while transformed;
///  - collisions between the weed and the driving character's ragdoll are ignored on every
///    modded client (the character root follows the weed);
///  - the weed player's death / pass-out / warp / fall RPCs are blocked so hazards cannot
///    harm them while transformed.
///
/// The vanilla OnCollisionEnter knockdown/thorn behaviour is deliberately kept: rolling
/// into other players knocks them down, exactly like the real hazard. Unmodded clients
/// are unaffected: they run the vanilla TumbleWeed scripts, but the chase AI is gated
/// behind photonView.IsMine, so a remote unmodded client just renders the synced weed
/// (the prefab's PhotonRigidbodyView replicates its physics from the owner).
/// </summary>
[HarmonyPatch]
public static class TumbleweedHarmonyPatches
{
	/// <summary>Per-weed bookkeeping for instances spawned by this mod. Held via
	/// ConditionalWeakTable so entries die with the destroyed weeds instead of leaking
	/// across scenes.</summary>
	private sealed class WeedState
	{
		public PhotonView View;
		public bool LifetimeDisabled;
		public bool CollisionIgnored;
		public Character OwnerCharacter;
	}

	private static readonly ConditionalWeakTable<TumbleWeed, WeedState> WeedStates =
		new ConditionalWeakTable<TumbleWeed, WeedState>();

	private static bool IsWeed(Character character)
	{
		return TumbleweedController.IsLocalWeedCharacter(character);
	}

	// ------------------------------------------------------------------
	// TumbleWeed AI suppression / maintenance for our marked weed instance.
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(TumbleWeed), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool TumbleWeedFixedUpdatePrefix(TumbleWeed __instance)
	{
		// Cache the PhotonView (the prefix runs every physics frame for every weed in the
		// scene, including the vanilla spawner ones).
		WeedState state = WeedStates.GetOrCreateValue(__instance);
		if (state.View == null)
		{
			state.View = __instance.GetComponent<PhotonView>();
		}

		if (!IsOurWeed(state.View))
		{
			return true;
		}

		MaintainOurWeed(__instance, state);

		// The rider's visibility on modded remote clients is owned by the CharacterSyncer
		// interpolation prefix: while the weed exists it hides the rider every frame (so the
		// remote only sees the tumbleweed, matching the local hidden state) and un-hides it
		// when the owner exits and the weed mapping self-cleans. The owner's local character
		// already follows the weed via its own controller; its network broadcast reports a
		// buried position (see CharacterSyncerGetDataToWritePostfix) so unmodded clients never
		// receive a coordinate inside the live weed ball.

		// Skip the vanilla chase AI entirely: on the owner the controller drives the weed
		// with WASD; on remote clients the vanilla body would early-return (not IsMine)
		// anyway, and the synced physics from the owner is what everyone follows.
		return false;
	}

	/// <summary>
	/// View ids of weed riders we have hidden on this (modded remote) client, so we can
	/// restore their visibility the moment the owner exits and the weed mapping self-cleans.
	/// </summary>
	private static readonly HashSet<int> _hiddenRiderViewIds = new HashSet<int>();

	private static bool IsOurWeed(PhotonView view)
	{
		object[] data = view != null ? view.InstantiationData : null;
		return data != null
		       && data.Length > 0
		       && data[0] is string marker
		       && marker == TumbleweedController.NetworkVisualMarker;
	}

	/// <summary>
	/// Everything the controller does on the owner when it spawns the weed, repeated here
	/// for remote modded clients (they cannot run the controller). Runs from the FixedUpdate
	/// prefix until each step succeeds.
	/// </summary>
	private static void MaintainOurWeed(TumbleWeed weed, WeedState state)
	{
		if (!state.LifetimeDisabled)
		{
			TumbleweedController.DisableVanillaLifetime(weed.gameObject);
			state.LifetimeDisabled = true;
		}

		if (!state.CollisionIgnored)
		{
			TrySetupCollisionIgnore(weed, state);
		}

		// Resolve the owner character + register the weed -> rider mapping as early as
		// possible (the first physics frame this weed is seen, on every modded client).
		// A modded remote pins the rider to the weed centre itself and skips the syncer's
		// interpolation (see CharacterSyncerInterpolatePrefix); having the mapping ready on
		// frame 1 means the interpolation skip is active immediately, so the rider never
		// flickers toward the buried coordinate for a frame at weed spawn.
		if (state.OwnerCharacter == null)
		{
			TryResolveOwnerCharacter(weed, state);
		}
	}

	/// <summary>
	/// Resolves the rider's Character from the weed's instantiation data (the second slot
	/// carries the owner character's PhotonView id) and registers the weed -> rider mapping
	/// so the remote pin + interpolation skip can find the weed for that character. Retried
	/// here each physics frame (cheap once resolved) until the owner character has spawned
	/// on this client.
	/// </summary>
	private static void TryResolveOwnerCharacter(TumbleWeed weed, WeedState state)
	{
		object[] data = state.View != null ? state.View.InstantiationData : null;
		if (data == null || data.Length < 2 || !(data[1] is int characterViewId))
		{
			return;
		}

		PhotonView characterView = PhotonView.Find(characterViewId);
		Character character = characterView != null ? characterView.GetComponent<Character>() : null;
		if (character == null)
		{
			return;
		}

		state.OwnerCharacter = character;
		TumbleweedController.RegisterWeedCharacter(character.photonView.ViewID, weed.gameObject);
	}

	/// <summary>
	/// The character root follows the weed, so the weed's collider must never push that
	/// character's ragdoll - on every modded client. Resolved from the instantiation data
	/// (second slot carries the owner character's PhotonView id); retried on later physics
	/// frames while the owner character has not spawned on this client yet.
	/// </summary>
	private static void TrySetupCollisionIgnore(TumbleWeed weed, WeedState state)
	{
		object[] data = state.View != null ? state.View.InstantiationData : null;
		if (data == null || data.Length < 2 || !(data[1] is int characterViewId))
		{
			state.CollisionIgnored = true;
			return;
		}

		PhotonView characterView = PhotonView.Find(characterViewId);
		Character character = characterView != null ? characterView.GetComponent<Character>() : null;
		if (character == null)
		{
			// The owner character may not have spawned on this client yet; the prefix
			// runs again on the next physics frame.
			return;
		}

		TumbleweedController.IgnoreCollisionWithCharacter(weed.gameObject, character);
		state.CollisionIgnored = true;
	}

	// ------------------------------------------------------------------
	// Weed player protection: death, pass-out, warp, fall.
	// Applied defensively at runtime (see TumbleweedPlugin.ConfigureOptionalCharacterPatches)
	// so a missing/renamed method only skips its patch instead of failing the load.
	// ------------------------------------------------------------------

	internal static bool CharacterRpcDiePrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	internal static bool CharacterRpcSetDeadPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	internal static bool CharacterRpcPassOutPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	internal static bool CharacterDieInstantlyPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	internal static bool CharacterHandleDeathPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	internal static bool CharacterWarpPlayerRpcPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	internal static bool CharacterWarpPlayerPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	/// <summary>Block Fall() on the weed player so collisions cannot knock them out of the form.</summary>
	internal static bool CharacterFallPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	/// <summary>Block RPCA_Fall on the weed player for the same reason.</summary>
	internal static bool CharacterRpcFallPrefix(Character __instance)
	{
		return !IsWeed(__instance);
	}

	// ------------------------------------------------------------------
	// End-game (win/lose) protection. PEAK's RPCEndGame iterates
	// Character.AllCharacters and calls CharacterStats.Win()/Lose() and
	// PeakHandler.EndCutscene(); the win flow then loads the Airport scene
	// (GameOverHandler.BeginAirportLoadRPC / EndScreen.ReturnToAirport) without
	// re-calling RPCEndGame. We revert the tumbleweed form BEFORE any of that
	// runs so:
	//  - the win/lose stats and badge unlocking apply to the real player
	//    (the controller lives on the player's own GameObject, and the player's
	//    badgeStatus/timelineInfo stay valid while transformed - we never swap
	//    in a fake character, unlike the Zombie mod);
	//  - the player is not left stuck in tumbleweed form when the Airport scene
	//    initializes (the weed is destroyed on revert, so localCharacter points
	//    at a normal, movable player).
	// These are applied defensively at runtime (see
	// TumbleweedPlugin.ConfigureOptionalEndgamePatches) so a missing/renamed
	// method only skips its patch instead of failing the load.
	// ------------------------------------------------------------------

	internal static void CharacterRPCEndGamePrefix()
	{
		TumbleweedController.ForceExitForEndGame();
		PruneInvalidAllCharactersEntries();
	}

	internal static void CharacterStatsWinPrefix(CharacterStats __instance)
	{
		EnsureCharacterStatsTimelinePopulated(__instance);
	}

	internal static void CharacterStatsGetFinalTimelineInfoPrefix(CharacterStats __instance)
	{
		EnsureCharacterStatsTimelinePopulated(__instance);
	}

	internal static void CharacterStatsGetFirstTimelineInfoPrefix(CharacterStats __instance)
	{
		EnsureCharacterStatsTimelinePopulated(__instance);
	}

	// Redundant safety net: by the time EndCutscene runs, RPCEndGame has already
	// reverted us, so this is normally a no-op. Unlike the Zombie mod we do NOT
	// prune Character.AllCharacters here - Tumbleweed never injects a fake
	// character, so pruning by empty badgeStatus would wrongly remove the real
	// player. We just guarantee the revert has happened.
	internal static void PeakHandlerEndCutscenePrefix()
	{
		TumbleweedController.ForceExitForEndGame();
	}

	private static void PruneInvalidAllCharactersEntries()
	{
		try
		{
			List<Character> all = Character.AllCharacters;
			if (all == null || all.Count == 0) return;
			all.RemoveAll(c => c == null || c.Equals(null) || c.gameObject == null || !c.gameObject.activeInHierarchy);
		}
		catch { }
	}

	private static void EnsureCharacterStatsTimelinePopulated(CharacterStats stats)
	{
		if (stats == null) return;
		try
		{
			if (stats.timelineInfo == null || stats.timelineInfo.Count > 0) return;
			// Defensive: if a character reaches Win()/the end screen with an empty
			// timelineInfo, seed a single fallback entry so the end-game cutscene never
			// crashes on an out-of-range index. For Tumbleweed the player is a real
			// Character, so this is normally a no-op - but it guards edge cases and
			// future game updates.
			stats.timelineInfo.Add(new EndScreen.TimelineInfo(
				Biome.BiomeType.Peak, CharacterStats.peakHeightInUnits, 0f, EndScreen.TimelineNote.None));
		}
		catch { }
	}

	// The win-flow's Airport load does not call RPCEndGame first, so these two
	// prefixes (registered via AccessTools from TumbleweedPlugin) are the entry
	// points that actually run - they force the revert at the moment the Airport
	// scene is about to load. SceneManager.sceneLoaded in the plugin is the last
	// line of defense.
	internal static void GameOverHandlerBeginAirportLoadRPCPrefix()
	{
		TumbleweedController.ForceExitForEndGame();
	}

	internal static void EndScreenReturnToAirportPrefix()
	{
		TumbleweedController.ForceExitForEndGame();
	}

	// ------------------------------------------------------------------
	// Camera fallback: after the vanilla MainCameraMovement.LateUpdate finishes,
	// re-point the camera at the rolling weed. This is applied at runtime via
	// AccessTools (see TumbleweedPlugin.ConfigureCameraFallbackPatch) rather than the
	// [HarmonyPatch] attribute so the target type/method can be resolved defensively.
	// ------------------------------------------------------------------

	internal static void MainCameraMovementLateUpdatePostfix()
	{
		TumbleweedController.ApplyCameraOverrideForLocalWeed();
	}

	// ------------------------------------------------------------------
	// Network sync rewrite for the weed rider (ImZombie-style buried body).
	//
	// GetDataToWrite() is the single point where a character's hip position is
	// broadcast to every remote client. For the local weed rider we redirect that
	// broadcast 30m straight down (x/z preserved) so unmodded clients receive a
	// buried coordinate instead of one inside the live weed ball - otherwise their
	// physics would push the synced character back out of the ball ("flying
	// outside"). The local rider's own transform is untouched, so camera and
	// local visibility keep tracking the weed centre.
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(CharacterSyncer), "GetDataToWrite")]
	[HarmonyPostfix]
	private static void CharacterSyncerGetDataToWritePostfix(CharacterSyncer __instance, ref CharacterSyncData __result)
	{
		Character character = __instance.GetComponent<Character>();
		if (character == null || !TumbleweedController.IsLocalWeedCharacter(character))
		{
			return;
		}

		Vector3 hip = __result.hipLocation;
		// Bury 30m down, keep x/z so the body stays under the tumbling weed.
		__result.hipLocation = new float3(hip.x, hip.y - 30f, hip.z);
		__result.averageVelocity = float3.zero;
	}

	// ------------------------------------------------------------------
	// On modded remote clients the weed rider's character is hidden (not pinned): the
	// syncer's interpolation toward the buried coordinate we broadcast is skipped and the
	// rider's renderers are disabled, so the remote only sees the tumbleweed roll - matching
	// the local player, which is hidden while transformed. Visibility is restored when the
	// owner exits and the weed mapping self-cleans. (The owner never runs this method - its
	// call site is guarded by photonView.IsMine - so this only affects remote copies.)
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
			if (TumbleweedController.TryGetWeedForCharacter(viewId, out _))
			{
				// This is our weed rider on a modded remote client: skip the syncer's
				// interpolation toward the buried broadcast coordinate and hide the rider so
				// the remote only sees the tumbleweed - matching the local hidden state.
				TumbleweedController.HideRemoteRider(character);
				_hiddenRiderViewIds.Add(viewId);
				return false;
			}
			// Not a rider anymore (owner exited, weed mapping self-cleaned): restore the
			// rider's visibility if we had hidden it.
			if (_hiddenRiderViewIds.Remove(viewId))
			{
				TumbleweedController.ShowRemoteRider(character);
			}
		}
		return true;
	}
}
