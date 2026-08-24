using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace ImCritter;

/// <summary>
/// Harmony patches for the critter forms:
///
///  - <see cref="MobUpdatePrefix"/> suppresses the vanilla critter AI (targeting, patrolling,
///    auto-attacks) on EVERY modded client for critters registered as transformed players —
///    hasBrain is only flipped on the owner, so remote copies need this prefix. The walk
///    animation is still driven from the synced displacement so remote modded clients see
///    the critter animate.
///  - <see cref="FrogTongueCheckAllCharactersPrefix"/> suppresses the frog's autonomous
///    wildlife scans (repositioning / tongue auto-attacks) while leaving
///    FrogTongue.FixedUpdate/LateUpdate running: FixedUpdate applies the actual tongue
///    PULL force while _isPulling (AddForceToBodyPart, network-replicated) and LateUpdate
///    positions the tongue visual — both are required for the player-driven tongue grab.
///  - <see cref="FrogTongueWaterRaycastFinalizer"/> swallows the NullReferenceException the
///    current game build ("we nerfed the frogs again") throws from WaterRaycast on every
///    water entry (broken TagHandle/WaterZone lookup) — it spams the log for wildlife frogs
///    and would spam for ours too.
///  - <see cref="FrogActionGuard"/> drops the MOVEMENT tongue actions (Reposition / JumpAway)
///    fired at a transformed frog. An UNMODDED master client still runs the vanilla frog AI on
///    our networked prefab and fires RPCA_FrogAction from there (we cannot patch it remotely;
///    the current build's RPCA_FrogAction has no PhotonMessageInfo, so the sender cannot be
///    checked). Dropping the movement actions keeps the transformed player's own client from
///    being yanked/hopped by its own frog's AI; Attack/LetGo stay allowed so the owner's
///    right-click tongue works on every client (pull is replicated via RPCA_AddForceToBodyPart).
///  - <see cref="CharacterSyncerGetDataToWritePostfix"/> redirects the transformed player's
///    network broadcast 30m straight down ("buried body" — the most stable recipe across
///    the five source mods) so unmodded clients never receive a coordinate inside the live
///    critter, which their physics would push back out.
///  - <see cref="CharacterSyncerInterpolatePrefix"/> hides the transformed player on remote
///    modded clients and skips the syncer interpolation toward the buried coordinate.
///  - The endgame prefixes revert the form BEFORE the end-game iterates characters or the
///    Airport scene loads (proven safety nets shared with the other physics forms).
/// </summary>
[HarmonyPatch]
public static class CritterHarmonyPatches
{
	private static readonly HashSet<int> _hiddenRiderViewIds = new HashSet<int>();
	private static readonly FieldInfo MobAttackingField = AccessTools.Field(typeof(Mob), "attacking");

	// ------------------------------------------------------------------
	// AI suppression on every modded client
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(Mob), "Update")]
	[HarmonyPrefix]
	private static bool MobUpdatePrefix(Mob __instance)
	{
		PhotonView view = __instance.photonView;
		if (!CritterController.IsCritterView(view))
		{
			// A critter item spawned by the GAME (normal wildlife): vanilla behaviour.
			return true;
		}

		// Transformed player's critter: keep the walk animation alive from the synced
		// displacement (vanilla recipe — UpdateAnimation lerps the walk speed from the
		// object's actual movement), but skip every AI branch. IsCritterView also
		// matches remote copies through the instantiation-data marker, so modded
		// clients that are not the owner suppress the AI here too.
		try
		{
			EnsureRemoteCritterInterpolation(__instance, view);
			TrySetWalking(__instance);
			__instance.UpdateAnimation();
		}
		catch
		{
			// Animation is cosmetic — never fail the frame over it.
		}
		return false;
	}

	private static void EnsureRemoteCritterInterpolation(Mob mob, PhotonView view)
	{
		if (mob == null || view == null || view.IsMine)
		{
			return;
		}

		try
		{
			Rigidbody rig = mob.GetComponent<Rigidbody>();
			if (rig != null && rig.interpolation != RigidbodyInterpolation.Interpolate)
			{
				rig.interpolation = RigidbodyInterpolation.Interpolate;
			}
		}
		catch
		{
		}
	}

	[HarmonyPatch(typeof(Mob), "RPC_StartAttack")]
	[HarmonyPostfix]
	private static void MobStartAttackPostfix(Mob __instance)
	{
		if (__instance == null || !CritterController.IsCritterView(__instance.photonView))
		{
			return;
		}

		try
		{
			// Mob.Update is suppressed for transformed critters so vanilla AI cannot take over.
			// That also means the vanilla "attacking" flag set by RPC_StartAttack would never
			// reach Mob.Attacking(). Consume it here as a cosmetic network animation event.
			__instance.anim?.SetTrigger("Attack");
			MobAttackingField?.SetValue(__instance, false);
		}
		catch
		{
			// Cosmetic only.
		}
	}

	// FrogTongue.FixedUpdate and LateUpdate MUST keep running for the transformed player's
	// frog: FixedUpdate's _isPulling branch applies the tongue pull force via
	// Character.AddForceToBodyPart (which broadcasts RPCA_AddForceToBodyPart, so unmodded
	// clients receive the pull too), and LateUpdate positions the tongueEnd visual between
	// frog and victim. Only the autonomous wildlife scan is suppressed:

	[HarmonyPatch(typeof(FrogTongue), "CheckAllCharacters")]
	[HarmonyPrefix]
	private static bool FrogTongueCheckAllCharactersPrefix(FrogTongue __instance)
	{
		// CheckAllCharacters is driven from LateUpdate's master-client timer branch and is
		// the frog's own targeting AI (auto tongue-grab / reposition / hop-away). The
		// transformed player's frog must only attack on right-click input.
		// NOTE: this only covers MODDED clients (incl. a modded MASTER whose copy is
		// matched by the marker). When the room's master client is unmodded, their copy
		// of the frog still runs the vanilla wildlife AI (and fires RPCA_FrogAction from
		// there) — the FrogActionGuard below drops those on this client, and
		// UpdateUnmoddedSync keeps their copy pinned to the rider.
		return !CritterController.IsCritterView(__instance.photonView);
	}

	// ------------------------------------------------------------------
	// AI-tongue guard for transformed frogs. An unmodded master client runs
	// the vanilla FrogTongue.LateUpdate AI (CheckAllCharacters) on our
	// networked frog and fires RPCA_FrogAction from there — we cannot patch
	// that remotely. The two prefixes below are installed manually (see
	// CritterPlugin.TryPatchFrogActionGuard) because the runtime signature
	// is ambiguous: the game assembly ships WITHOUT PhotonMessageInfo —
	// RPCA_FrogAction(PhotonView, FrogActionType, Vector3) — but PEAKER's
	// PEAKERRpcInfo patcher appends PhotonMessageInfo at runtime. We reflect
	// the live MethodInfo and pick the matching prefix:
	//   - With info (PEAKER active): FrogActionGuardWithInfo — exact sender
	//     check, drops anything not fired by the frog's owner (wildlife AI
	//     licks/repositions/hops all blocked; owner's right-click passes).
	//   - Without info (PEAKER not active): FrogActionGuard — drops the
	//     MOVEMENT actions (Reposition/JumpAway) that would drag the frog
	//     around; Attack/LetGo stay allowed so the tongue works everywhere
	//     (pull replicated via RPCA_AddForceToBodyPart).
	// ------------------------------------------------------------------

	internal static bool FrogActionGuard(FrogTongue __instance,
		PhotonView characterView, FrogTongue.FrogActionType frogActionType, Vector3 hopDir)
	{
		if (__instance == null) return true;
		if (!CritterController.IsCritterView(__instance.photonView)) return true;
		// 受控青蛙只随玩家控制：跳过野生 AI 的位移类动作（把青蛙拽向玩家 / 跳开）。
		return frogActionType != FrogTongue.FrogActionType.Reposition
			&& frogActionType != FrogTongue.FrogActionType.JumpAway;
	}

	internal static bool FrogActionGuardWithInfo(FrogTongue __instance,
		PhotonView characterView, FrogTongue.FrogActionType frogActionType, Vector3 hopDir,
		PhotonMessageInfo info)
	{
		if (__instance == null) return true;
		if (!CritterController.IsCritterView(__instance.photonView)) return true;
		// Defensive: no sender info (offline/local calls) — never block local execution.
		if (info.Sender == null) return true;
		PhotonView view = __instance.photonView;
		if (view.Owner != null && info.Sender.ActorNumber == view.Owner.ActorNumber) return true;
		// Non-owner sender (an unmodded master's wildlife AI, or any other client): drop it.
		return false;
	}

	// ------------------------------------------------------------------
	// Pickup guards: nobody may pick up the transformed player's critter.
	// blockInteraction on the owner is local-only — an unmodded (or even a
	// modded, non-owner) player can otherwise walk up and take the critter,
	// which yanks the networked item into their backpack while the owner's
	// controller keeps driving it (the "duplicated critter" bug: the master
	// grants the item to the picker, the destroy then fails with "Client is
	// neither owner nor MasterClient", and two copies stay around).
	// The marker check works on EVERY modded client (remotes included), so:
	//  - Item.Interact (runs on the interacting client) blocks modded pickers
	//    from even starting a pickup;
	//  - Item.RequestPickup (the vanilla RPC that adds the item to the
	//    picker's inventory, executed on the master) blocks the pickup for
	//    unmodded pickers too, as long as the master is modded. An unmodded
	//    master executing the RPC is the inherent pure-client-mod limit.
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(Item), "Interact")]
	[HarmonyPrefix]
	private static bool ItemInteractPrefix(Item __instance)
	{
		if (__instance == null) return true;
		return !CritterController.IsCritterView(__instance.photonView);
	}

	[HarmonyPatch(typeof(Item), "RequestPickup")]
	[HarmonyPrefix]
	private static bool ItemRequestPickupPrefix(Item __instance, object[] __args)
	{
		if (__instance == null) return true;
		if (!CritterController.IsCritterView(__instance.photonView)) return true;

		// This RPC executes on the MASTER only. The picker's own client already deactivated its
		// local copy of the critter in Item.Interact (SetActive(false)) BEFORE broadcasting this
		// request — and the vanilla DenyPickupRPC (which reactivates it) is only sent when
		// AddItem fails, which our skip prevents. Reply with the denial ourselves so the
		// picker's copy comes right back (works on unmodded pickers: DenyPickupRPC is vanilla).
		try
		{
			if (__args != null && __args.Length > 0 && __args[0] is PhotonView pickerView
				&& pickerView.Owner != null)
			{
				__instance.photonView.RPC("DenyPickupRPC", pickerView.Owner);
			}
		}
		catch (Exception ex)
		{
			CritterPlugin.Log?.LogWarning("[Critter] DenyPickupRPC reply failed: " + ex.Message);
		}
		return false;
	}

	// ------------------------------------------------------------------
	// Bomb fuse guard. Vanilla Dynamite auto-lights on the MASTER when any
	// Character is within lightFuseRadius. In bomb form the fuse must be
	// controlled only by the transformed player pressing RMB.
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(Dynamite), "TestLightWick")]
	[HarmonyPrefix]
	private static bool DynamiteTestLightWickPrefix(Dynamite __instance)
	{
		return !IsTransformedBomb(__instance);
	}

	internal static bool BombFlareLitGuard(Dynamite __instance)
	{
		if (!IsTransformedBomb(__instance)) return true;
		PhotonView view = __instance != null ? __instance.photonView : null;
		return view != null && view.IsMine && CritterController.IsManualBombIgnitionInProgress(view);
	}

	internal static bool BombFlareLitGuardWithInfo(Dynamite __instance, PhotonMessageInfo info)
	{
		if (!IsTransformedBomb(__instance)) return true;
		PhotonView view = __instance != null ? __instance.photonView : null;
		if (view == null) return false;
		return info.Sender != null
		       && view.Owner != null
		       && info.Sender.ActorNumber == view.Owner.ActorNumber;
	}

	private static bool IsTransformedBomb(Dynamite dynamite)
	{
		if (dynamite == null) return false;
		PhotonView view = dynamite.photonView;
		return CritterController.TryGetKind(view, out CritterKind kind) && kind == CritterKind.Bomb;
	}

	[HarmonyPatch(typeof(FrogTongue), "WaterRaycast")]
	[HarmonyFinalizer]
	private static Exception FrogTongueWaterRaycastFinalizer(FrogTongue __instance, Exception __exception)
	{
		// Vanilla bug in the current build ("we nerfed the frogs again"): OnEnable resolves
		// _waterTag via TagHandle.GetExistingTag("Water") and the splash path dereferences
		// the hit transform's parent chain without null checks, so frogs entering water
		// throw NullReferenceException from WaterRaycast->FixedUpdate. The method is
		// best-effort buoyancy/splash; the exception already aborts it mid-way, so
		// swallowing it only removes the log spam (for wildlife frogs and ours alike).
		if (__exception != null)
		{
			if (!_loggedWaterRaycastBug)
			{
				_loggedWaterRaycastBug = true;
				CritterPlugin.Log?.LogWarning("[Critter] Swallowed a vanilla FrogTongue.WaterRaycast exception " +
					"(broken _waterTag/WaterZone lookup in this game build); further occurrences are muted.");
			}
		}
		return null;
	}

	private static bool _loggedWaterRaycastBug;

	private static void TrySetWalking(Mob mob)
	{
		try
		{
			// UpdateAnimation only reports a non-zero walk speed in the Walking state.
			object walking = CritterController.GetWalkingMobState();
			if (walking != null)
			{
				AccessTools.PropertySetter(typeof(Mob), "mobState")?.Invoke(mob, new object[] { walking });
			}
		}
		catch
		{
		}
	}

	// ------------------------------------------------------------------
	// Network sync rewrite for the critter rider (buried body, ImZombie-style)
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(CharacterSyncer), "GetDataToWrite")]
	[HarmonyPostfix]
	private static void CharacterSyncerGetDataToWritePostfix(CharacterSyncer __instance, ref CharacterSyncData __result)
	{
		Character character = __instance.GetComponent<Character>();
		if (character == null || !CritterController.IsLocalCritterCharacter(character))
		{
			return;
		}

		Vector3 hip = __result.hipLocation;
		// Bury 30m down, keep x/z so the body stays under the critter.
		__result.hipLocation = new Unity.Mathematics.float3(hip.x, hip.y - 30f, hip.z);
		__result.averageVelocity = Unity.Mathematics.float3.zero;
	}

	// ------------------------------------------------------------------
	// Remote rider hiding on modded clients
	// ------------------------------------------------------------------

	[HarmonyPatch(typeof(CharacterSyncer), "InterpolateRigPositions")]
	[HarmonyPrefix]
	private static bool CharacterSyncerInterpolatePrefix(CharacterSyncer __instance)
	{
		Character character = __instance.GetComponent<Character>();
		if (character == null || character.IsLocal)
		{
			return true;
		}
		if (character.photonView != null)
		{
			int viewId = character.photonView.ViewID;
			if (character.photonView.Owner == PhotonNetwork.LocalPlayer)
			{
				// Our own rider copy on a remote load — not applicable (owner never runs this).
				return true;
			}
			bool registered = false;
			try
			{
				// The rider is a critter form player if their Character is the active critter
				// character registered by the controller (modded clients only know their own
				// local transforms; for remote riders we recognize the form via the shared
				// hidden-rider marker set below).
				registered = _hiddenRiderViewIds.Contains(viewId);
			}
			catch
			{
			}
			if (registered)
			{
				// Keep the rider hidden: skip interpolation toward the buried coordinate.
				return false;
			}
		}
		return true;
	}

	// ------------------------------------------------------------------
	// Endgame safety nets (same set as the other physics forms)
	// ------------------------------------------------------------------

	internal static void CharacterRPCEndGamePrefix()
	{
		CritterController.ForceExitForEndGame();
	}

	internal static void GameOverHandlerBeginAirportLoadRPCPrefix()
	{
		CritterController.ForceExitForEndGame();
	}

	internal static void EndScreenReturnToAirportPrefix()
	{
		CritterController.ForceExitForEndGame();
	}
}
