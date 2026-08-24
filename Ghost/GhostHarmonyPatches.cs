using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Peak;
using Photon.Pun;
using UnityEngine;

namespace ImGhost;

/// <summary>
/// While the local player is in ghost form:
///  - the spawned GhostBall's vanilla AI is suppressed on every modded client (identified
///    through the PhotonView.InstantiationData marker), so the ghost is fully player-driven;
///  - the ghost player's death / pass-out / warp / fall RPCs are blocked so the explosion
///    attack and hazards cannot harm them while transformed.
///
/// Unmodded clients are unaffected: they run the vanilla GhostBall scripts, but the vanilla
/// AI is gated behind photonView.IsMine, so a remote unmodded client just renders the ball
/// and its synced state while the owner drives it.
/// </summary>
[HarmonyPatch]
public static class GhostHarmonyPatches
{
	private static readonly HashSet<int> CollisionIgnoredInstances = new HashSet<int>();

	// PhotonView per ball, cached for the prefixes that run every frame (two GetComponent
	// calls per ball per frame otherwise). ConditionalWeakTable entries die with the
	// destroyed balls instead of leaking across scenes.
	private static readonly ConditionalWeakTable<GhostBall, PhotonView> PhotonViewCache =
		new ConditionalWeakTable<GhostBall, PhotonView>();

	private static PhotonView GetPhotonView(GhostBall ghostBall)
	{
		if (PhotonViewCache.TryGetValue(ghostBall, out PhotonView view) && view != null)
		{
			return view;
		}
		view = ghostBall.GetComponent<PhotonView>();
		PhotonViewCache.Remove(ghostBall);
		PhotonViewCache.Add(ghostBall, view);
		return view;
	}

	private static bool IsGhostVisual(GhostBall ghostBall, PhotonView view)
	{
		if (ghostBall == null)
		{
			return false;
		}

		object[] data = view != null ? view.InstantiationData : null;
		return data != null
		       && data.Length > 0
		       && data[0] is string marker
		       && marker == GhostController.NetworkVisualMarker;
	}

	private static bool IsGhost(Character character)
	{
		return GhostController.IsLocalGhostCharacter(character);
	}

	// ------------------------------------------------------------------
	// GhostBall AI suppression for our marked ghost instance.
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(GhostBall), "Update")]
	[HarmonyPrefix]
	private static bool GhostBallUpdatePrefix(GhostBall __instance)
	{
		PhotonView view = GetPhotonView(__instance);
		if (!IsGhostVisual(__instance, view))
		{
			return true;
		}

		// Skip the vanilla AI/lifetime/explosion logic entirely; the owner drives the
		// ghost and every client (including unmodded ones) follows the synced state.
		// On remote modded clients, additionally mirror the owner-synced explosion state
		// onto this instance's Animator (vanilla only does so when photonView.IsMine).
		if (view != null && !view.IsMine)
		{
			GhostController.ApplyGhostBallExpression(__instance);
		}

		return false;
	}

	[HarmonyPatch(typeof(GhostBall), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool GhostBallFixedUpdatePrefix(GhostBall __instance)
	{
		if (!IsGhostVisual(__instance, GetPhotonView(__instance)))
		{
			return true;
		}

		TrySetupCollisionIgnore(__instance);
		return false;
	}

	/// <summary>
	/// The ball follows the owner's character, so its collider must never push that
	/// character's ragdoll - on every modded client. Resolved from the instantiation
	/// data (second slot carries the owner character's PhotonView id).
	/// </summary>
	private static void TrySetupCollisionIgnore(GhostBall ghostBall)
	{
		try
		{
			GameObject root = ghostBall.gameObject;
			int instanceId = root.GetInstanceID();
			if (CollisionIgnoredInstances.Contains(instanceId))
			{
				return;
			}

			PhotonView view = GetPhotonView(ghostBall);
			object[] data = view != null ? view.InstantiationData : null;
			if (data == null || data.Length < 2 || !(data[1] is int characterViewId))
			{
				return;
			}

			PhotonView characterView = PhotonView.Find(characterViewId);
			Character character = characterView != null ? characterView.GetComponent<Character>() : null;
			if (character == null)
			{
				// The owner character may not have spawned on this client yet; retry
				// on a later physics frame (the prefix runs on every FixedUpdate).
				return;
			}

			GhostController.IgnoreCollisionWithCharacter(root, character);
			CollisionIgnoredInstances.Add(instanceId);
		}
		catch
		{
		}
	}

	// ------------------------------------------------------------------
	// Ghost player protection: death, pass-out, warp, fall.
	// ------------------------------------------------------------------

	internal static bool CharacterRpcDiePrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	internal static bool CharacterRpcSetDeadPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	internal static bool CharacterRpcPassOutPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	internal static bool CharacterDieInstantlyPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	internal static bool CharacterHandleDeathPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	internal static bool CharacterWarpPlayerRpcPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	internal static bool CharacterWarpPlayerPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	/// <summary>Block Fall() on the ghost player so its own explosion cannot knock itself down.</summary>
	internal static bool CharacterFallPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	/// <summary>Block RPCA_Fall on the ghost player for the same reason.</summary>
	internal static bool CharacterRpcFallPrefix(Character __instance)
	{
		return !IsGhost(__instance);
	}

	// ------------------------------------------------------------------
	// Camera fallback: after the vanilla MainCameraMovement.LateUpdate finishes,
	// re-point the camera at the flying ghost. This is applied at runtime via
	// AccessTools (see GhostPlugin.ConfigureCameraFallbackPatch) rather than the
	// [HarmonyPatch] attribute so the target type/method can be resolved defensively.
	// ------------------------------------------------------------------

	internal static void MainCameraMovementLateUpdatePostfix()
	{
		GhostController.ApplyCameraOverrideForLocalGhost();
	}
}
