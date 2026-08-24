using HarmonyLib;
using UnityEngine;

namespace ImScoutmaster;

[HarmonyPatch]
public static class ScoutmasterHarmonyPatches
{
	[HarmonyPatch(typeof(InventoryItemUI), "SetItem")]
	[HarmonyPrefix]
	private static bool InventoryItemUISetItemPrefix(InventoryItemUI __instance, ItemSlot slot)
	{
		if (slot != null)
		{
			return true;
		}

		Plugin.ClearInventoryItemUiSafely(__instance);
		return false;
	}

	private static Character ResolveCharacter(Component component, Character fieldCharacter)
	{
		if (fieldCharacter != null)
		{
			return fieldCharacter;
		}
		return component != null ? component.GetComponent<Character>() : null;
	}

	[HarmonyPatch(typeof(PlayerHandler), "RegisterCharacter")]
	[HarmonyPrefix]
	private static bool PlayerHandlerRegisterCharacterPrefix(Character character)
	{
		return !Plugin.ShouldSkipCharacterRegistration(character);
	}

	[HarmonyPatch(typeof(ReconnectHandler), "UpdateReconnectData")]
	[HarmonyPrefix]
	private static bool ReconnectHandlerUpdateReconnectDataPrefix(Character character)
	{
		return !Plugin.ShouldSkipReconnectDataUpdate(character);
	}

	[HarmonyPatch(typeof(SlipperyJellyfish), "OnTriggerEnter")]
	[HarmonyPrefix]
	private static bool SlipperyJellyfishOnTriggerEnterPrefix(Collider other)
	{
		return !Plugin.ShouldSuppressSlipperyJellyfishSend(other);
	}

	[HarmonyPatch(typeof(SlipperyJellyfish), "Trigger")]
	[HarmonyPrefix]
	private static bool SlipperyJellyfishTriggerPrefix(int targetID)
	{
		return !Plugin.ShouldSkipSlipperyJellyfishTrigger(targetID);
	}

	[HarmonyPatch(typeof(Photon.Pun.PhotonNetwork), "Destroy", new[] { typeof(GameObject) })]
	[HarmonyPrefix]
	private static bool PhotonNetworkDestroyGameObjectPrefix(GameObject targetGo)
	{
		return !Plugin.ShouldSuppressRuntimePrefabBackupDestroy(targetGo);
	}

	[HarmonyPatch(typeof(Photon.Pun.PhotonNetwork), "Destroy", new[] { typeof(Photon.Pun.PhotonView) })]
	[HarmonyPrefix]
	private static bool PhotonNetworkDestroyPhotonViewPrefix(Photon.Pun.PhotonView targetView)
	{
		return !Plugin.ShouldSuppressRuntimePrefabBackupDestroy(targetView);
	}

	[HarmonyPatch(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object) })]
	[HarmonyPrefix]
	private static bool UnityObjectDestroyPrefix(UnityEngine.Object obj)
	{
		return !Plugin.ShouldSuppressRuntimePrefabBackupDestroy(obj);
	}

	[HarmonyPatch(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object), typeof(float) })]
	[HarmonyPrefix]
	private static bool UnityObjectDestroyDelayedPrefix(UnityEngine.Object obj)
	{
		return !Plugin.ShouldSuppressRuntimePrefabBackupDestroy(obj);
	}

	[HarmonyPatch(typeof(UnityEngine.Object), "DestroyImmediate", new[] { typeof(UnityEngine.Object) })]
	[HarmonyPrefix]
	private static bool UnityObjectDestroyImmediatePrefix(UnityEngine.Object obj)
	{
		return !Plugin.ShouldSuppressRuntimePrefabBackupDestroy(obj);
	}

	[HarmonyPatch(typeof(UnityEngine.Object), "DestroyImmediate", new[] { typeof(UnityEngine.Object), typeof(bool) })]
	[HarmonyPrefix]
	private static bool UnityObjectDestroyImmediateAllowAssetsPrefix(UnityEngine.Object obj)
	{
		return !Plugin.ShouldSuppressRuntimePrefabBackupDestroy(obj);
	}

	[HarmonyPatch(typeof(Character), "Awake")]
	[HarmonyPrefix]
	private static bool CharacterAwakePrefix(Character __instance)
	{
		Plugin.EnsureControlledScoutmasterRegistered(__instance);
		if (!Plugin.ShouldUseIsolatedCharacterLifecycle(__instance))
		{
			return true;
		}

		Plugin.RunIsolatedCharacterAwake(__instance);
		return false;
	}

	[HarmonyPatch(typeof(Character), "Start")]
	[HarmonyPrefix]
	private static bool CharacterStartPrefix(Character __instance)
	{
		Plugin.EnsureControlledScoutmasterRegistered(__instance);
		if (!Plugin.ShouldUseIsolatedCharacterLifecycle(__instance))
		{
			return true;
		}

		Plugin.RunIsolatedCharacterStart(__instance);
		return false;
	}

	[HarmonyPatch(typeof(CharacterData), "Awake")]
	[HarmonyPrefix]
	private static bool CharacterDataAwakePrefix(CharacterData __instance)
	{
		Character character = __instance != null ? __instance.GetComponent<Character>() : null;
		Plugin.EnsureControlledScoutmasterRegistered(character);
		if (!Plugin.ShouldUseIsolatedCharacterLifecycle(character))
		{
			return true;
		}

		Plugin.RunIsolatedCharacterDataAwake(__instance, character);
		return false;
	}

	[HarmonyPatch(typeof(CharacterData), "UpdateHasParachute")]
	[HarmonyPrefix]
	private static bool CharacterDataUpdateHasParachutePrefix(CharacterData __instance)
	{
		// CharacterData.UpdateHasParachute 的首条指令即读取 CharacterData.character
		// 作为 isScoutmaster 判据。受控领队/藏匿源角色在隔离生命周期中该反向引用
		// 偶发为空，会导致每帧 NullReferenceException 刷屏并使 Character.Update 崩溃。
		// 反向引用缺失时直接跳过该方法来消除空引用（合法角色不受影响）。
		if (__instance == null || Plugin.GetCharacterDataOwningCharacter(__instance) == null)
		{
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(CharacterData), "SetBadgeStatus")]
	[HarmonyPrefix]
	private static bool CharacterDataSetBadgeStatusPrefix(CharacterData __instance)
	{
		return !Plugin.ShouldSkipControlledScoutmasterBadgeStatus(__instance);
	}

	[HarmonyPatch(typeof(CharacterCustomization), "Start")]
	[HarmonyPrefix]
	private static bool CharacterCustomizationStartPrefix(CharacterCustomization __instance)
	{
		return !Plugin.ShouldSkipControlledScoutmasterCustomization(__instance);
	}

	[HarmonyPatch(typeof(CharacterCustomization), "OnDestroy")]
	[HarmonyPrefix]
	private static bool CharacterCustomizationOnDestroyPrefix(CharacterCustomization __instance)
	{
		return !Plugin.ShouldSkipControlledScoutmasterCustomization(__instance);
	}

	[HarmonyPatch(typeof(Scoutmaster), "Update")]
	[HarmonyPrefix]
	private static bool ScoutmasterUpdatePrefix(Scoutmaster __instance)
	{
		return !Plugin.IsControlledScoutmaster(__instance);
	}

	[HarmonyPatch(typeof(Scoutmaster), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool ScoutmasterFixedUpdatePrefix(Scoutmaster __instance)
	{
		return true;
	}

	[HarmonyPatch(typeof(Scoutmaster), "SetCurrentTarget")]
	[HarmonyPrefix]
	private static bool ScoutmasterSetCurrentTargetPrefix(Scoutmaster __instance)
	{
		return !Plugin.IsControlledScoutmaster(__instance);
	}

	[HarmonyPatch(typeof(Scoutmaster), "RPCA_SetCurrentTarget")]
	[HarmonyPrefix]
	private static bool ScoutmasterRpcSetCurrentTargetPrefix(Scoutmaster __instance)
	{
		return !Plugin.IsControlledScoutmaster(__instance);
	}

	[HarmonyPatch(typeof(CharacterBackpackHandler), "LateUpdate")]
	[HarmonyPrefix]
	private static bool CharacterBackpackHandlerLateUpdatePrefix(CharacterBackpackHandler __instance, Character ___character)
	{
		Character character = ResolveCharacter(__instance, ___character);
		if (!Plugin.ShouldDisableInventoryForCharacter(character))
		{
			return true;
		}

		// 游戏更新后 CharacterBackpackHandler.backpack(GameObject) 字段被拆分为多个
		// BackpackOnBackVisuals（backpackVisuals/fannypackVisuals/jetpackVisuals/rocketpackVisuals）。
		// BackpackOnBackVisuals 暴露 SetViewActive(bool) 用于隐藏背包视觉，遍历全部字段确保隐藏。
		if (__instance != null)
		{
			BackpackOnBackVisuals[] visuals = new BackpackOnBackVisuals[]
			{
				__instance.backpackVisuals,
				__instance.fannypackVisuals,
				__instance.jetpackVisuals,
				__instance.rocketpackVisuals
			};
			foreach (BackpackOnBackVisuals v in visuals)
			{
				if (v != null)
				{
					v.SetViewActive(false);
				}
			}
		}
		return false;
	}

	[HarmonyPatch(typeof(CharacterBackpackHandler), "StashInBackpack")]
	[HarmonyPrefix]
	private static bool CharacterBackpackHandlerStashInBackpackPrefix(CharacterBackpackHandler __instance, Character ___character, Character interactor)
	{
		return !Plugin.ShouldDisableInventoryForCharacter(ResolveCharacter(__instance, ___character))
			&& !Plugin.ShouldDisableInventoryForCharacter(interactor);
	}

	[HarmonyPatch(typeof(CharacterBackpackHandler), "RPCAddItemToCharacterBackpack")]
	[HarmonyPrefix]
	private static bool CharacterBackpackHandlerAddItemPrefix(CharacterBackpackHandler __instance, Character ___character)
	{
		return !Plugin.ShouldDisableInventoryForCharacter(ResolveCharacter(__instance, ___character));
	}

	[HarmonyPatch(typeof(CharacterItems), "Update")]
	[HarmonyPrefix]
	private static bool CharacterItemsUpdatePrefix(CharacterItems __instance, Character ___character)
	{
		return HandleDisabledInventory(__instance, ___character);
	}

	[HarmonyPatch(typeof(CharacterItems), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool CharacterItemsFixedUpdatePrefix(CharacterItems __instance, Character ___character)
	{
		return HandleDisabledInventory(__instance, ___character);
	}

	[HarmonyPatch(typeof(CharacterItems), "EquipSlot")]
	[HarmonyPrefix]
	private static bool CharacterItemsEquipSlotPrefix(CharacterItems __instance, Character ___character)
	{
		return HandleDisabledInventory(__instance, ___character);
	}

	[HarmonyPatch(typeof(CharacterItems), "Equip")]
	[HarmonyPrefix]
	private static bool CharacterItemsEquipPrefix(CharacterItems __instance, Character ___character, ref Item __result)
	{
		if (HandleDisabledInventory(__instance, ___character))
		{
			return true;
		}
		__result = null;
		return false;
	}

	private static bool HandleDisabledInventory(CharacterItems items, Character character)
	{
		Character resolved = ResolveCharacter(items, character);
		if (!Plugin.ShouldDisableInventoryForCharacter(resolved)) return true;
		Plugin.ResetDisabledInventoryState(items, resolved);
		return false;
	}

	[HarmonyPatch(typeof(Item), "Interact")]
	[HarmonyPrefix]
	private static bool ItemInteractPrefix(Character interactor)
	{
		return !Plugin.ShouldDisableInventoryForCharacter(interactor);
	}

	[HarmonyPatch(typeof(Item), "IsInteractible")]
	[HarmonyPostfix]
	private static void ItemIsInteractiblePostfix(Character interactor, ref bool __result)
	{
		if (Plugin.ShouldDisableInventoryForCharacter(interactor))
		{
			__result = false;
		}
	}

	[HarmonyPatch(typeof(CharacterMovement), "TryToJump")]
	[HarmonyPrefix]
	private static bool CharacterMovementTryToJumpPrefix(Character ___character)
	{
		return !Plugin.TryHandleControlledScoutmasterJump(___character);
	}

	[HarmonyPatch(typeof(CharacterMovement), "JumpRpc")]
	[HarmonyPrefix]
	private static bool CharacterMovementJumpRpcPrefix(Character ___character)
	{
		return !Plugin.ShouldSuppressControlledScoutmasterJumpRpc(___character);
	}


	[HarmonyPatch(typeof(CharacterClimbing), "TryToStartWallClimb")]
	[HarmonyPrefix]
	private static bool CharacterClimbingTryToStartWallClimbPrefix(CharacterClimbing __instance, Character ___character, bool forceAttempt, Vector3 overide, bool botGrab, float raycastDistance)
	{
		return !Plugin.TryHandleControlledScoutmasterStartWallClimb(__instance, ___character, forceAttempt, overide, botGrab, raycastDistance);
	}

	[HarmonyPatch(typeof(CharacterClimbing), "StopClimbingRpc")]
	[HarmonyPrefix]
	private static bool CharacterClimbingStopClimbingRpcPrefix(CharacterClimbing __instance, Character ___character, float setFall)
	{
		return !Plugin.TryHandleControlledScoutmasterStopClimb(__instance, ___character, setFall);
	}

	[HarmonyPatch(typeof(CharacterGrabbing), "Update")]
	[HarmonyPrefix]
	private static bool CharacterGrabbingUpdatePrefix(CharacterGrabbing __instance, Character ___character)
	{
		return !Plugin.TryHandleControlledScoutmasterGrabbingUpdate(__instance, ResolveCharacter(__instance, ___character));
	}

	[HarmonyPatch(typeof(CharacterGrabbing), "GrabAction")]
	[HarmonyPrefix]
	private static bool CharacterGrabbingGrabActionPrefix(CharacterGrabbing __instance, Character ___character, Collision collision)
	{
		return !Plugin.TryHandleControlledScoutmasterGrabAction(__instance, ResolveCharacter(__instance, ___character), collision);
	}

	[HarmonyPatch(typeof(Character), "GetCameraPos")]
	[HarmonyPostfix]
	private static void CharacterGetCameraPosPostfix(Character __instance, ref Vector3 __result)
	{
		Plugin.TryApplyCameraOverride(__instance, ref __result);
	}

	[HarmonyPatch(typeof(MainCameraMovement), "LateUpdate")]
	[HarmonyPostfix]
	private static void MainCameraMovementLateUpdatePostfix(MainCameraMovement __instance)
	{
		Plugin.RefreshControlledScoutmasterCamera(__instance);
	}

	[HarmonyPatch(typeof(GUIManager), "UpdateReticle")]
	[HarmonyPrefix]
	private static void GUIManagerUpdateReticlePrefix(ref Character ___character)
	{
		Character controlled = Plugin.GetControlledScoutmasterCharacter();
		if (controlled != null)
		{
			Character.localCharacter = controlled;
			___character = controlled;
		}
	}

	[HarmonyPatch(typeof(CharacterMovement), "Update")]
	[HarmonyPrefix]
	private static bool CharacterMovementUpdatePrefix(Character ___character)
	{
		return !Plugin.ShouldSkipCharacterMovementUpdate(___character);
	}

	[HarmonyPatch(typeof(Character), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool CharacterFixedUpdatePrefix(Character __instance)
	{
		return !Plugin.ShouldSkipCharacterFixedUpdate(__instance);
	}

	[HarmonyPatch(typeof(Character), "Update")]
	[HarmonyPrefix]
	private static bool CharacterUpdatePrefix(Character __instance)
	{
		return !Plugin.ShouldSkipCharacterUpdate(__instance);
	}

	[HarmonyPatch(typeof(CharacterSyncer), "Update")]
	[HarmonyPrefix]
	private static bool CharacterSyncerUpdatePrefix(Character ___m_character)
	{
		return !Plugin.ShouldSkipCharacterNetworkInterpolation(___m_character);
	}

	[HarmonyPatch(typeof(CharacterSyncer), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool CharacterSyncerFixedUpdatePrefix(Character ___m_character)
	{
		return !Plugin.ShouldSkipCharacterNetworkInterpolation(___m_character);
	}

	[HarmonyPatch(typeof(CharacterSyncer), "InterpolateRigPositions")]
	[HarmonyPostfix]
	private static void CharacterSyncerInterpolateRigPositionsPostfix(CharacterSyncer __instance, Character ___m_character)
	{
		Plugin.SmoothRemoteControlledScoutmasterInterpolation(__instance, ___m_character);
	}

	[HarmonyPatch(typeof(CharacterAfflictions), "UpdateWeight")]
	[HarmonyPrefix]
	private static bool CharacterAfflictionsUpdateWeightPrefix(CharacterAfflictions __instance, Character ___character)
	{
		return !Plugin.ShouldSkipCharacterAfflictionWeightUpdate(__instance, ___character);
	}

	[HarmonyPatch(typeof(CharacterMovement), "CheckFallDamage")]
	[HarmonyPrefix]
	private static bool CharacterMovementCheckFallDamagePrefix(Character ___character)
	{
		return !Plugin.ShouldSuppressControlledCharacterFall(___character);
	}

	[HarmonyPatch(typeof(Character), "Fall", new[] { typeof(float), typeof(float) })]
	[HarmonyPrefix]
	private static bool CharacterFallPrefix(Character __instance)
	{
		return !Plugin.ShouldSuppressControlledCharacterFall(__instance);
	}

	// 游戏更新后 Character.RPCA_Fall 签名从无参变为 (float seconds, float shake)，
	// 且 RPCA_FallWithScreenShake 已被移除（合并进 RPCA_Fall）。
	// 注意：必须按名称匹配而不是签名匹配 —— PEAKERRpcInfo patcher 会在运行时给所有
	// [PunRPC] 方法追加 PhotonMessageInfo 参数，精确签名匹配会找不到目标方法，
	// 导致整个补丁类（含本类全部补丁）加载失败。
	[HarmonyPatch(typeof(Character), "RPCA_Fall")]
	[HarmonyPrefix]
	private static bool CharacterRpcFallPrefix(Character __instance)
	{
		return !Plugin.ShouldSuppressControlledCharacterFall(__instance);
	}

	[HarmonyPatch(typeof(Character), "RPCA_PassOut")]
	[HarmonyPrefix]
	private static bool CharacterRpcPassOutPrefix(Character __instance)
	{
		return !Plugin.ShouldSuppressControlledCharacterFall(__instance);
	}

	[HarmonyPatch(typeof(Character), "PassOutInstantly")]
	[HarmonyPrefix]
	private static bool CharacterPassOutInstantlyPrefix(Character __instance)
	{
		return !Plugin.ShouldSuppressControlledCharacterFall(__instance);
	}

	[HarmonyPatch(typeof(Character), "get_IsLocal")]
	[HarmonyPrefix]
	private static bool CharacterIsLocalPrefix(Character __instance, ref bool __result)
	{
		return OverrideControlledScoutmasterCharacterState(__instance, ref __result);
	}

	[HarmonyPatch(typeof(Character), "get_IsPlayerControlled")]
	[HarmonyPrefix]
	private static bool CharacterIsPlayerControlledPrefix(Character __instance, ref bool __result)
	{
		return OverrideControlledScoutmasterCharacterState(__instance, ref __result);
	}

	[HarmonyPatch(typeof(Character), "get_IsRegisteredToPlayer")]
	[HarmonyPrefix]
	private static bool CharacterIsRegisteredToPlayerPrefix(Character __instance, ref bool __result)
	{
		return OverrideControlledScoutmasterCharacterState(__instance, ref __result);
	}

	private static bool OverrideControlledScoutmasterCharacterState(Character character, ref bool result)
	{
		if (Plugin.IsStashedSourceCharacter(character))
		{
			result = false;
			return false;
		}
		if (Plugin.IsLocallyControlledScoutmasterCharacter(character))
		{
			result = true;
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(Character), "get_player")]
	[HarmonyPrefix]
	private static bool CharacterPlayerPrefix(Character __instance, ref Player __result)
	{
		if (Plugin.IsStashedSourceCharacter(__instance))
		{
			__result = null;
			return false;
		}
		if (Plugin.IsLocallyControlledScoutmasterCharacter(__instance) && Player.localPlayer != null)
		{
			__result = Player.localPlayer;
			return false;
		}
		return true;
	}

	[HarmonyPatch(typeof(PlayerHandler), "GetPlayer", new[] { typeof(Photon.Realtime.Player) })]
	[HarmonyPrefix]
	private static bool PlayerHandlerGetPlayerPrefix(Photon.Realtime.Player photonPlayer, ref Player __result)
	{
		if (photonPlayer != null)
		{
			return true;
		}

		__result = null;
		return false;
	}

	[HarmonyPatch(typeof(Player), "get_character")]
	[HarmonyPostfix]
	private static void PlayerCharacterPostfix(Player __instance, ref Character __result)
	{
		if (__instance == Player.localPlayer)
		{
			Character controlled = Plugin.GetControlledScoutmasterCharacter();
			if (controlled != null)
			{
				__result = controlled;
			}
		}
	}

	[HarmonyPatch(typeof(PlayerHandler), "GetPlayerCharacter")]
	[HarmonyPostfix]
	private static void PlayerHandlerGetPlayerCharacterPostfix(Photon.Realtime.Player photonPlayer, ref Character __result)
	{
		if (Plugin.TryGetControlledScoutmasterForPhotonPlayer(photonPlayer, out Character controlled))
		{
			__result = controlled;
		}
	}

	[HarmonyPatch(typeof(HideTheBody), "SetShowing")]
	[HarmonyPrefix]
	private static void HideTheBodySetShowingPrefix(HideTheBody __instance, Character ___character, ref float x)
	{
		if (Plugin.ShouldForceShowControlledScoutmaster(__instance, ___character))
		{
			x = 0f;
		}
	}

	[HarmonyPatch(typeof(HideTheBody), "SetShowing")]
	[HarmonyPostfix]
	private static void HideTheBodySetShowingPostfix(HideTheBody __instance, Character ___character, Renderer r)
	{
		RefreshControlledScoutmasterBody(__instance, ___character);
	}

	[HarmonyPatch(typeof(HideTheBody), "Update")]
	[HarmonyPostfix]
	private static void HideTheBodyUpdatePostfix(HideTheBody __instance, Character ___character)
	{
		RefreshControlledScoutmasterBody(__instance, ___character);
	}

	private static void RefreshControlledScoutmasterBody(HideTheBody hideTheBody, Character character)
	{
		if (Plugin.ShouldForceShowControlledScoutmaster(hideTheBody, character))
		{
			Plugin.RefreshHideTheBodyVisuals(hideTheBody);
		}
	}

	[HarmonyPatch(typeof(Character), "RPCEndGame")]
	[HarmonyPrefix]
	private static void CharacterRPCEndGamePrefix()
	{
		// Force-restore local player from Scoutmaster form BEFORE RPCEndGame
		// iterates AllCharacters. This moves the local player back onto the
		// source character (which has valid badgeStatus/timelineInfo) so
		// Win()/Lose() apply to the source character and EndCutscene's
		// SetCosmetics never touches a controlled Scoutmaster body with empty
		// badgeStatus.
		Plugin.ForceExitLocalScoutmasterFormBeforeEndGame();
		Plugin.PruneInvalidAllCharactersEntries();
	}

	[HarmonyPatch(typeof(CharacterStats), "Win")]
	[HarmonyPrefix]
	private static void CharacterStatsWinPrefix(CharacterStats __instance)
	{
		Plugin.EnsureCharacterStatsTimelinePopulated(__instance);
	}

	[HarmonyPatch(typeof(PeakHandler), "EndCutscene")]
	[HarmonyPrefix]
	private static void PeakHandlerEndCutscenePrefix()
	{
		// Defensive: even after force-exit, controlled Scoutmaster bodies owned
		// by remote transformed players may still be in AllCharacters. Strip
		// them (and any character with empty badgeStatus) before SetCosmetics
		// calls BadgeUnlocker.SetBadges, which would otherwise create
		// Texture2D(0,1) and throw.
		Plugin.PruneEndCutsceneCharacters();
	}
}
