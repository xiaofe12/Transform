using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;
using TransformHud = global::Transform.Core.TransformHud;
using FormValidation = global::Transform.Core.FormValidation;

namespace ImScoutmaster;

/// <summary>
/// Scoutmaster form module, adapted from the standalone "I'm Scoutmaster" BepInEx plugin into a
/// MonoBehaviour component driven by the unified Transform plugin. The unified plugin creates the
/// host GameObject, adds this component and calls <see cref="InitializeModule"/> with its own
/// ConfigFile/log; per-frame Update and OnDestroy keep working through the normal Unity
/// component lifecycle. Enter/exit is driven externally by the unified menu instead of the old
/// toggle-key hold; the short-press manual fall while transformed is kept.
/// </summary>
public sealed class Plugin : MonoBehaviour, Photon.Realtime.IInRoomCallbacks
{
	private enum ConfigKey
	{
		ToggleKey,
		ThrowForce,
		ThrowUpBias,
		ThrowFallSeconds,
		ThirdPersonHeightOffset,
		ThirdPersonDistance,
		SourceStashDistance,
		RestoreAtScoutmasterPosition,
		RestoreGroundOffset
	}

	public const string Id = "com.github.Thanks.ImScoutmaster";
	public const string Name = "I'm Scoutmaster";
	public const string Version = "0.7.14";

	private const string ScoutmasterResourceName = "Character_Scoutmaster";
	private const string CharacterResourceName = "Character";
	private const float ManualFallShortPressMaxSeconds = 0.3f;
	private const float ToggleDebounceSeconds = 0.35f;
	private const string ControlsConfigSectionName = "Controls";
	private const string ScoutmasterControlConfigSectionName = "Scoutmaster Control";
	private const string CameraConfigSectionName = "Camera";
	private const string PlayerRestoreConfigSectionName = "Player Restore";
	private const float CameraRestoreAssistSeconds = 0.4f;
	private const float CameraHealSeconds = 8f;
	private const float CameraHealMaxDistance = 100f;
	private const float CameraHealDefaultFieldOfView = 70f;
	private const float CameraPatrolIntervalSeconds = 0.5f;
	private const float CameraPatrolBackoffSeconds = 10f;
	private const int CameraPatrolMaxConsecutiveRepairs = 4;
	private const float ThirdPersonCameraFollowSharpness = 18f;
	private const float ThirdPersonCameraPositionSharpness = 16f;
	private const float ThirdPersonCameraRotationSharpness = 22f;
	private const float ThirdPersonCameraSnapDistance = 8f;
	private const float ThirdPartyCompatibilityRetryIntervalSeconds = 1f;
	private const float ClearBoostReticleTimer = 10f;
	private const float ControlledScoutmasterJumpVelocity = 10.5f;
	private const float ControlledScoutmasterJumpGroundClearance = 0.12f;
	private const float ControlledScoutmasterJumpStaminaCost = 0.1f;
	private const float ControlledScoutmasterManualFallSeconds = 1.25f;
	private const float ControlledScoutmasterRagdollControl = 1f;
	private const float ControlledGrabAttachPointMaxHandDistance = 3.5f;
	private const string ControlledScoutmasterInstantiationMarker = "ImScoutmaster.ControlledScoutmaster";
	private const int ControlledScoutmasterNetworkProtocol = 1;
	private const float PrefabPoolWrapperRetryIntervalSeconds = 0.5f;
	private const float ScoutmasterPrefabRetrySeconds = 8f;
	private const float ScoutmasterPrefabWarmIntervalSeconds = 2f;

	private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
	private static readonly FieldInfo CharacterViewField = typeof(Character).GetField("view", InstanceFlags);
	private static readonly FieldInfo CharacterStartedField = typeof(Character).GetField("started", InstanceFlags);
	private static readonly FieldInfo CharacterSmoothedCamPosField = typeof(Character).GetField("smoothedCamPos", InstanceFlags);
	private static readonly MethodInfo CharacterGetBodypartMethod = typeof(Character).GetMethod("GetBodypart", InstanceFlags);
	private static readonly MethodInfo CharacterGetPartTypeMethod = typeof(Character).GetMethod("GetPartType", InstanceFlags);
	private static readonly FieldInfo CharacterDataCharacterField = typeof(CharacterData).GetField("character", InstanceFlags);
	private static readonly FieldInfo CharacterDataDeadField = typeof(CharacterData).GetField("_dead", InstanceFlags);
	private static readonly FieldInfo CharacterDataGrabbedPlayerField = typeof(CharacterData).GetField("grabbedPlayer", InstanceFlags);
	private static readonly MethodInfo CharacterFallMethod = typeof(Character).GetMethod("Fall", InstanceFlags, null, new[] { typeof(float), typeof(float) }, null);
	private static readonly FieldInfo CharacterCustomizationCharacterField = typeof(CharacterCustomization).GetField("_character", InstanceFlags);
	private static readonly FieldInfo BodypartCharacterField = typeof(Bodypart).GetField("character", InstanceFlags);
	private static readonly FieldInfo ScoutmasterCurrentTargetField = typeof(Scoutmaster).GetField("_currentTarget", InstanceFlags);
	private static readonly FieldInfo ScoutmasterTargetForcedUntilField = typeof(Scoutmaster).GetField("targetForcedUntil", InstanceFlags);
	private static readonly FieldInfo ScoutmasterChillForSecondsField = typeof(Scoutmaster).GetField("chillForSeconds", InstanceFlags);
	private static readonly FieldInfo ScoutmasterIsThrowingField = typeof(Scoutmaster).GetField("isThrowing", InstanceFlags);
	private static readonly FieldInfo ScoutmasterAllScoutmastersField = typeof(Scoutmaster).GetField("AllScoutmasters", StaticFlags);
	private static readonly PropertyInfo MainCameraSpecCharacterProperty = typeof(MainCameraMovement).GetProperty("specCharacter", StaticFlags);
	private static readonly FieldInfo MainCameraIsSpectatingField = typeof(MainCameraMovement).GetField("isSpectating", InstanceFlags);
	private static readonly FieldInfo MainCameraRagdollCamField = typeof(MainCameraMovement).GetField("ragdollCam", InstanceFlags);
	private static readonly FieldInfo MainCameraCurrentForwardOffsetField = typeof(MainCameraMovement).GetField("currentForwardOffset", InstanceFlags);
	private static readonly FieldInfo MainCameraTargetPlayerPovPositionField = typeof(MainCameraMovement).GetField("targetPlayerPovPosition", InstanceFlags);
	private static readonly FieldInfo MainCameraPhysicsRotField = typeof(MainCameraMovement).GetField("physicsRot", InstanceFlags);
	private static readonly FieldInfo CharacterSyncerTargetPositionField = typeof(CharacterSyncer).GetField("<tPos>k__BackingField", InstanceFlags);
	private static readonly MethodInfo FindObjectsOfTypeByTypeMethod = typeof(Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
	private static readonly MethodInfo CharacterClimbingCanClimbMethod = typeof(CharacterClimbing).GetMethod("CanClimb", InstanceFlags, null, Type.EmptyTypes, null);
	private static readonly MethodInfo CharacterClimbingStartClimbRpcMethod = FindCharacterClimbingStartClimbRpcMethod();
	private static readonly FieldInfo CharacterClimbingClimbToggledOnField = typeof(CharacterClimbing).GetField("climbToggledOn", InstanceFlags);
	private static readonly FieldInfo CharacterClimbingSinceLastClimbStartedField = typeof(CharacterClimbing).GetField("sinceLastClimbStarted", InstanceFlags);
	private static readonly FieldInfo CharacterClimbingPlayerSlideField = typeof(CharacterClimbing).GetField("playerSlide", InstanceFlags);
	private static readonly FieldInfo CharacterClimbingPlayerSlideFieldRenamed = typeof(CharacterClimbing).GetField("_playerSlide", InstanceFlags);
	private static readonly PropertyInfo CharacterClimbingPlayerSlideProperty = typeof(CharacterClimbing).GetProperty("playerSlide", InstanceFlags);
	private static readonly MethodInfo CharacterGrabbingGrabAttachMethod = typeof(CharacterGrabbing).GetMethod("RPCA_GrabAttach", InstanceFlags);
	private static readonly PropertyInfo PlayerHandlerInstanceProperty = typeof(PlayerHandler).GetProperty("Instance", StaticFlags);
	private static readonly FieldInfo PlayerHandlerCharacterLookupField = typeof(PlayerHandler).GetField("m_playerCharacterLookup", InstanceFlags);
	private static readonly FieldInfo CharacterItemsCurrentSelectedSlotField = typeof(CharacterItems).GetField("currentSelectedSlot", InstanceFlags);
	private static readonly FieldInfo CharacterItemsLastSelectedSlotField = typeof(CharacterItems).GetField("lastSelectedSlot", InstanceFlags);
	private static readonly FieldInfo CharacterAfflictionsCharacterField = typeof(CharacterAfflictions).GetField("character", InstanceFlags);
	private static readonly object CharacterItemsNoneSlotValue = CreateOptionableNoneValue(CharacterItemsCurrentSelectedSlotField?.FieldType);
	private static FieldInfo PeakStatsStaminaBarsField;
	private static FieldInfo PeakStatsObservedCharacterField;
	private static FieldInfo PeakStatsAfflictionBarField;
	private static Type PeakStatsStaminaBarType;
	private static Type PeakStatsAfflictionType;
	private static float _peakStatsCleanupGraceUntil;

	internal static Plugin Instance { get; private set; }
	internal static ManualLogSource Log { get; private set; }
	private static bool _skipControlledCharacterFixedUpdate;
	private static CharacterItems _lastDisabledInventoryResetItems;
	private static int _lastDisabledInventoryResetFrame = -1;
	private bool _characterFixedUpdateCompatibilityConfigured;
	private bool _loggedWaitingForPeakerHook;
	private bool _loggedPeakStatsTypesMissing;
	private float _nextPeakerGuardAttemptTime;
	private bool _peakStatsCompatibilityConfigured;
	private float _nextThirdPartyCompatibilityAttemptTime;
	private static ImScoutmasterPrefabPool _prefabPoolWrapper;
	private static float _nextPrefabPoolWrapperEnsureTime;
	// 首次实例化保护：Unity 物理场景初始化滞后于实例化，首次变身时领队碰撞体可能尚未注册，
	// 保持 kinematic 等待物理就绪再激活，避免"第一次变身掉入地下"（后续变身物理已就绪，不延迟）。
	private static bool _scoutmasterPhysicsWarmedUp;
	private static float _nextScoutmasterPrefabWarmTime;

	internal static ConfigEntry<KeyCode> ToggleKey { get; private set; }
	internal static ConfigEntry<float> ThrowForce { get; private set; }
	internal static ConfigEntry<float> ThrowUpBias { get; private set; }
	internal static ConfigEntry<float> ThrowFallSeconds { get; private set; }
	internal static ConfigEntry<float> ThirdPersonHeightOffset { get; private set; }
	internal static ConfigEntry<float> ThirdPersonDistance { get; private set; }
	internal static ConfigEntry<float> SourceStashDistance { get; private set; }
	internal static ConfigEntry<bool> RestoreAtScoutmasterPosition { get; private set; }
	internal static ConfigEntry<float> RestoreGroundOffset { get; private set; }

	private readonly Harmony _harmony = new Harmony(Id);
	private ActiveScoutmasterSession _session;
	private bool _switching;
	private float _lastToggleTime;
	private float _toggleHoldStartTime = -1f;
	private bool _toggleHoldTriggered;

	// 注意：统一工程存在全局命名空间 Transform（mod 根命名空间），会遮蔽 UnityEngine.Transform 类型
	// （using 别名会与根命名空间冲突报 CS0576），因此本文件中所有 Transform 类型必须写全限定名
	// UnityEngine.Transform，命名空间引用须写 global:: 前缀。
	private static UnityEngine.Transform _controlledScoutmasterCreationRoot;
	private static readonly HashSet<int> _controlledScoutmasterInstanceIds = new HashSet<int>();
	private static readonly HashSet<int> _controlledScoutmasterViewIds = new HashSet<int>();
	private static readonly HashSet<int> _controlledScoutmasterCharacterInstanceIds = new HashSet<int>();
	private static readonly Dictionary<int, int> _controlledScoutmasterOwnerActorNumbersByViewId = new Dictionary<int, int>();
	private static readonly Dictionary<int, Character> _controlledScoutmasterByOwnerActorNumber = new Dictionary<int, Character>();
	private static readonly HashSet<int> _stashedSourceCharacterIds = new HashSet<int>();
	private static readonly Dictionary<int, RendererVisualState> _rendererVisualStates = new Dictionary<int, RendererVisualState>();
	private static readonly Dictionary<int, RendererVisualState> _sourceRendererVisualStates = new Dictionary<int, RendererVisualState>();
	private static readonly Dictionary<int, bool> _sourceLightVisualStates = new Dictionary<int, bool>();
	private static Character _cameraOverrideCharacter;
	private static Character _cameraRestoreCharacter;
	private static float _cameraRestoreUntil;
	private static float _cameraHealUntil;
	private static float _nextCameraPatrolTime;
	private static int _cameraPatrolConsecutiveRepairs;
	private static Character _lastCameraPatrolCharacter;
	private static GameObject _viewScoutmasterObject;
	private static bool _hasSmoothedThirdPersonCameraPose;
	private static bool _hasSmoothedThirdPersonCameraTarget;
	private static Vector3 _smoothedThirdPersonCameraTarget;
	private static Vector3 _smoothedThirdPersonCameraPosition;
	private static Quaternion _smoothedThirdPersonCameraRotation = Quaternion.identity;

	private readonly struct RendererVisualState
	{
		public readonly bool Enabled;
		public readonly bool ForceRenderingOff;

		public RendererVisualState(bool enabled, bool forceRenderingOff)
		{
			Enabled = enabled;
			ForceRenderingOff = forceRenderingOff;
		}
	}

	/// <summary>ConfigFile owned by the unified Transform plugin, injected via InitializeModule.</summary>
	private static ConfigFile _moduleConfigFile;

	/// <summary>Instance-level shim so the ported code keeps using <c>Config</c> like BaseUnityPlugin did.</summary>
	private ConfigFile Config => _moduleConfigFile;

	/// <summary>Instance-level shim so the ported code keeps using <c>Logger</c> like BaseUnityPlugin did.</summary>
	private ManualLogSource Logger => Log;

	/// <summary>
	/// Called by the unified Transform plugin right after this component is added to its host
	/// GameObject. Replaces the standalone plugin's Awake(): config binding, Harmony patches and
	/// callback registration all happen here, driven by the unified plugin's ConfigFile/log.
	/// </summary>
	internal void InitializeModule(ConfigFile config, ManualLogSource log)
	{
		Instance = this;
		_moduleConfigFile = config;
		Log = log;
		BindConfig();
		PatchHarmonyTolerantly();
		ConfigureCharacterFixedUpdateCompatibility();
		Logger.LogInfo("[I'm Scoutmaster] Module loaded (integrated into Transform), version " + Version + ".");
		ValidateReflectionMembers();
		PhotonNetwork.AddCallbackTarget(this);
	}

	// 逐个 patch 且容错：任一 patch 因游戏更新 API 变更而失败时，
	// 仅记录警告并跳过，不中断整个模组加载。
	private void PatchHarmonyTolerantly()
	{
		int applied = 0;
		int failed = 0;
		Type[] patchTypes = typeof(ScoutmasterHarmonyPatches).Assembly.GetTypes();
		for (int i = 0; i < patchTypes.Length; i++)
		{
			Type type = patchTypes[i];
			// 注意：patch 类是 static class（abstract+sealed），不能因 IsAbstract 跳过。
			if (type == null || !type.IsClass || (type.IsAbstract && !type.IsSealed))
			{
				continue;
			}
			if (!type.IsDefined(typeof(HarmonyPatch), inherit: false))
			{
				continue;
			}

			try
			{
				_harmony.CreateClassProcessor(type).Patch();
				applied++;
			}
			catch (Exception ex)
			{
				failed++;
				Logger?.LogWarning("[I'm Scoutmaster] Harmony patch class " + type.Name + " failed: " + ex.Message);
			}
		}
		Logger?.LogInfo("[I'm Scoutmaster] Harmony patches applied=" + applied + " failed=" + failed + ".");
	}

	private void ValidateReflectionMembers()
	{
		List<string> missing = new List<string>();
		CheckReflectionMember(missing, CharacterViewField, "Character.view");
		CheckReflectionMember(missing, CharacterStartedField, "Character.started");
		CheckReflectionMember(missing, CharacterSmoothedCamPosField, "Character.smoothedCamPos");
		CheckReflectionMember(missing, CharacterGetBodypartMethod, "Character.GetBodypart");
		CheckReflectionMember(missing, CharacterGetPartTypeMethod, "Character.GetPartType");
		CheckReflectionMember(missing, CharacterDataCharacterField, "CharacterData.character");
		CheckReflectionMember(missing, CharacterDataDeadField, "CharacterData._dead");
		CheckReflectionMember(missing, CharacterDataGrabbedPlayerField, "CharacterData.grabbedPlayer");
		CheckReflectionMember(missing, CharacterFallMethod, "Character.Fall");
		CheckReflectionMember(missing, CharacterCustomizationCharacterField, "CharacterCustomization._character");
		CheckReflectionMember(missing, BodypartCharacterField, "Bodypart.character");
		CheckReflectionMember(missing, ScoutmasterCurrentTargetField, "Scoutmaster._currentTarget");
		CheckReflectionMember(missing, ScoutmasterTargetForcedUntilField, "Scoutmaster.targetForcedUntil");
		CheckReflectionMember(missing, ScoutmasterChillForSecondsField, "Scoutmaster.chillForSeconds");
		CheckReflectionMember(missing, ScoutmasterIsThrowingField, "Scoutmaster.isThrowing");
		CheckReflectionMember(missing, MainCameraSpecCharacterProperty, "MainCameraMovement.specCharacter");
		CheckReflectionMember(missing, MainCameraIsSpectatingField, "MainCameraMovement.isSpectating");
		CheckReflectionMember(missing, MainCameraRagdollCamField, "MainCameraMovement.ragdollCam");
		CheckReflectionMember(missing, MainCameraCurrentForwardOffsetField, "MainCameraMovement.currentForwardOffset");
		CheckReflectionMember(missing, MainCameraTargetPlayerPovPositionField, "MainCameraMovement.targetPlayerPovPosition");
		CheckReflectionMember(missing, MainCameraPhysicsRotField, "MainCameraMovement.physicsRot");
		CheckReflectionMember(missing, FindObjectsOfTypeByTypeMethod, "Object.FindObjectsOfType");
		CheckReflectionMember(missing, CharacterClimbingCanClimbMethod, "CharacterClimbing.CanClimb");
		CheckReflectionMember(missing, CharacterClimbingStartClimbRpcMethod, "CharacterClimbing.StartClimbRpc");
		CheckReflectionMember(missing, CharacterClimbingClimbToggledOnField, "CharacterClimbing.climbToggledOn");
		CheckReflectionMember(missing, CharacterClimbingSinceLastClimbStartedField, "CharacterClimbing.sinceLastClimbStarted");
		// 游戏更新后 playerSlide 字段改名为 _playerSlide，或通过公共属性 playerSlide 访问。
		// 三者任一可用即视为反射成功。
		if (CharacterClimbingPlayerSlideField == null && CharacterClimbingPlayerSlideFieldRenamed == null && CharacterClimbingPlayerSlideProperty == null)
		{
			missing.Add("CharacterClimbing.playerSlide");
		}
		CheckReflectionMember(missing, CharacterGrabbingGrabAttachMethod, "CharacterGrabbing.RPCA_GrabAttach");
		CheckReflectionMember(missing, PlayerHandlerInstanceProperty, "PlayerHandler.Instance");
		CheckReflectionMember(missing, PlayerHandlerCharacterLookupField, "PlayerHandler.m_playerCharacterLookup");
		CheckReflectionMember(missing, CharacterItemsCurrentSelectedSlotField, "CharacterItems.currentSelectedSlot");
		CheckReflectionMember(missing, CharacterItemsLastSelectedSlotField, "CharacterItems.lastSelectedSlot");
		CheckReflectionMember(missing, CharacterAfflictionsCharacterField, "CharacterAfflictions.character");
		PlayerScoutmasterController.AppendMissingReflectionMembers(missing);

		if (missing.Count == 0)
		{
			Logger.LogInfo("[I'm Scoutmaster] All reflection members resolved successfully.");
			return;
		}

		foreach (string memberName in missing)
		{
			Logger.LogWarning("[I'm Scoutmaster] Reflection lookup failed: " + memberName + ", related features may not work.");
		}
	}

	internal static void CheckReflectionMember(List<string> missing, MemberInfo member, string memberName)
	{
		if (member == null)
		{
			missing.Add(memberName);
		}
	}

	internal static void ClearInventoryItemUiSafely(InventoryItemUI ui)
	{
		if (ui == null)
		{
			return;
		}

		try
		{
			ui.Clear();
		}
		catch { }
	}

	private void OnDestroy()
	{
		try
		{
			PhotonNetwork.RemoveCallbackTarget(this);
			ExitScoutmasterForm(restorePlayer: true);
			TransformHud.Restore();
			_harmony.UnpatchSelf();
		}
		catch (Exception ex)
		{
			Logger.LogWarning("[I'm Scoutmaster] Cleanup failed: " + ex.Message);
		}
		finally
		{
			if (Instance == this)
			{
				Instance = null;
			}
		}
	}

	public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
	{
		try
		{
			if (_session?.IsActive == true)
			{
				_session.PushSourceStashPositionToPlayer(newPlayer);
			}
		}
		catch { }
	}

	public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
	{
		if (otherPlayer == null)
		{
			return;
		}

		// 该玩家若正处于变身状态，其受控领队会随 Photon 一起销毁；
		// 这里兜底清扫残留的 owner 映射与 ViewID 注册，避免 ViewID 复用导致误判。
		PruneControlledScoutmastersForActor(otherPlayer.ActorNumber);
	}

	public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged)
	{
	}

	public void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps)
	{
	}

	public void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
	{
		// 房主切换：清扫已不在房间内的 owner 映射，防御 OnPlayerLeftRoom 与 Photon 销毁的竞态。
		SweepStaleControlledScoutmasterOwners();
	}

	private void Update()
	{
		if (!_characterFixedUpdateCompatibilityConfigured && Time.unscaledTime >= _nextPeakerGuardAttemptTime)
		{
			_nextPeakerGuardAttemptTime = Time.unscaledTime + 0.5f;
			ConfigureCharacterFixedUpdateCompatibility();
		}
		if (!_peakStatsCompatibilityConfigured && Time.unscaledTime >= _nextThirdPartyCompatibilityAttemptTime)
		{
			_nextThirdPartyCompatibilityAttemptTime = Time.unscaledTime + ThirdPartyCompatibilityRetryIntervalSeconds;
			ConfigureThirdPartyCompatibility();
		}

		RefreshRestoredPlayerCamera();
		PatrolLocalCameraHealth();
		EnsureScoutmasterPrefabPoolWrapper();
		WarmScoutmasterPrefabCache();
		if (_session?.IsActive == true)
		{
			_session.Tick();
			TransformHud.TickHideKeepStatusUnlessExternalCamera();
		}

		HandleToggleHold();
	}

	private void ConfigureCharacterFixedUpdateCompatibility()
	{
		if (_characterFixedUpdateCompatibilityConfigured)
		{
			return;
		}

		try
		{
			Type peakerTestingType = FindLoadedType("PEAKER.Testing");
			if (peakerTestingType == null)
			{
				if (!_loggedWaitingForPeakerHook)
				{
					_loggedWaitingForPeakerHook = true;
					Logger.LogInfo("[I'm Scoutmaster] Waiting for PEAKER ragdoll hook; controlled Scoutmaster Character.FixedUpdate remains enabled.");
				}
				return;
			}

			MethodInfo peakerPostfix = peakerTestingType.GetMethod(
				"PostCharacterGetTargetRagdollControll",
				StaticFlags,
				null,
				new[] { typeof(CharacterData), typeof(float).MakeByRefType() },
				null);
			MethodInfo guardPrefix = AccessTools.Method(typeof(Plugin), nameof(PeakerRagdollControlPostfixPrefix));
			if (peakerPostfix == null || guardPrefix == null)
			{
				_skipControlledCharacterFixedUpdate = false;
				_characterFixedUpdateCompatibilityConfigured = true;
				Logger.LogWarning("[I'm Scoutmaster] Could not install PEAKER ragdoll guard; controlled Scoutmaster Character.FixedUpdate remains enabled.");
				return;
			}

			_harmony.Patch(peakerPostfix, prefix: new HarmonyMethod(guardPrefix));
			_skipControlledCharacterFixedUpdate = false;
			_characterFixedUpdateCompatibilityConfigured = true;
			Logger.LogInfo("[I'm Scoutmaster] Installed PEAKER ragdoll guard; controlled Scoutmaster Character.FixedUpdate enabled.");
		}
		catch (Exception ex)
		{
			_skipControlledCharacterFixedUpdate = false;
			Logger.LogWarning("[I'm Scoutmaster] Failed to install PEAKER ragdoll guard; will retry while keeping controlled Scoutmaster Character.FixedUpdate enabled: " + ex.Message);
		}
	}

	private static Type FindLoadedType(string fullName)
	{
		if (string.IsNullOrEmpty(fullName))
		{
			return null;
		}

		try
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(fullName, throwOnError: false);
				if (type != null)
				{
					return type;
				}
			}
		}
		catch
		{
		}

		return null;
	}

	internal static float GetControlledScoutmasterRagdollControl()
	{
		return ControlledScoutmasterRagdollControl;
	}

	internal static void ApplyControlledScoutmasterRagdollBlend(Character character)
	{
		if (character?.data == null)
		{
			return;
		}

		character.data.currentRagdollControll = ControlledScoutmasterRagdollControl;
	}

	private static MethodInfo FindMethod(Type type, string name, bool preferStatic, params Type[] parameterTypes)
	{
		if (type == null || string.IsNullOrEmpty(name))
		{
			return null;
		}

		BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | (preferStatic ? BindingFlags.Static : BindingFlags.Instance);
		try
		{
			MethodInfo exactMethod = type.GetMethod(name, bindingFlags, null, parameterTypes ?? Type.EmptyTypes, null);
			if (exactMethod != null)
			{
				return exactMethod;
			}
		}
		catch
		{
		}

		try
		{
			foreach (MethodInfo method in type.GetMethods(bindingFlags))
			{
				if (method == null || method.Name != name)
				{
					continue;
				}

				ParameterInfo[] parameters = method.GetParameters();
				int expectedCount = parameterTypes?.Length ?? 0;
				if (parameters.Length != expectedCount)
				{
					continue;
				}

				bool matches = true;
				for (int i = 0; i < expectedCount; i++)
				{
					Type expectedType = parameterTypes[i];
					Type actualType = parameters[i].ParameterType;
					if (expectedType == null)
					{
						continue;
					}
					if (actualType == expectedType)
					{
						continue;
					}
					if (!string.Equals(actualType.FullName, expectedType.FullName, StringComparison.Ordinal))
					{
						matches = false;
						break;
					}
				}

				if (matches)
				{
					return method;
				}
			}
		}
		catch
		{
		}

		return null;
	}

	private void ConfigureThirdPartyCompatibility()
	{
		if (_peakStatsCompatibilityConfigured)
		{
			return;
		}

		try
		{
			Type proximityManagerType = FindLoadedType("PeakStats.MonoBehaviours.ProximityStaminaManager");
			Type staminaBarType = FindLoadedType("PeakStats.MonoBehaviours.CharacterStaminaBar");
			Type barAfflictionType = FindLoadedType("PeakStats.MonoBehaviours.CharacterBarAffliction");
			if (proximityManagerType == null || staminaBarType == null || barAfflictionType == null)
			{
				if (!_loggedPeakStatsTypesMissing)
				{
					_loggedPeakStatsTypesMissing = true;
				}
				return;
			}

			MethodInfo managerUpdateMethod = FindMethod(proximityManagerType, "Update", preferStatic: false, Type.EmptyTypes);
			MethodInfo createStaminaBarMethod = FindMethod(proximityManagerType, "CreateStaminaBar", preferStatic: true, typeof(Character));
			MethodInfo staminaBarUpdateMethod = FindMethod(staminaBarType, "Update", preferStatic: false, Type.EmptyTypes);
			MethodInfo fetchReferencesMethod = FindMethod(barAfflictionType, "FetchReferences", preferStatic: false, Type.EmptyTypes);
			FieldInfo staminaBarsField = proximityManagerType.GetField("staminaBars", InstanceFlags);
			FieldInfo observedCharacterField = staminaBarType.GetField("_observedCharacter", InstanceFlags);
			FieldInfo afflictionBarField = barAfflictionType.GetField("characterStaminaBar", InstanceFlags);
			MethodInfo prunePrefix = AccessTools.Method(typeof(Plugin), nameof(PeakStatsProximityManagerUpdatePrefix));
			MethodInfo createPrefix = AccessTools.Method(typeof(Plugin), nameof(PeakStatsCreateStaminaBarPrefix));
			MethodInfo barUpdatePrefix = AccessTools.Method(typeof(Plugin), nameof(PeakStatsCharacterStaminaBarUpdatePrefix));
			MethodInfo fetchPrefix = AccessTools.Method(typeof(Plugin), nameof(PeakStatsCharacterBarAfflictionFetchReferencesPrefix));
			if (managerUpdateMethod == null
				|| staminaBarUpdateMethod == null
				|| fetchReferencesMethod == null
				|| staminaBarsField == null
				|| observedCharacterField == null
				|| afflictionBarField == null
				|| prunePrefix == null
				|| barUpdatePrefix == null
				|| fetchPrefix == null)
			{
				Logger.LogWarning("[I'm Scoutmaster] PeakStats types found but some members missing. Update=" + (managerUpdateMethod != null) + " CreateStaminaBar=" + (createStaminaBarMethod != null) + " StaminaBarUpdate=" + (staminaBarUpdateMethod != null) + " FetchReferences=" + (fetchReferencesMethod != null) + " staminaBars=" + (staminaBarsField != null) + " _observedCharacter=" + (observedCharacterField != null) + " characterStaminaBar=" + (afflictionBarField != null));
				return;
			}

			PeakStatsStaminaBarsField = staminaBarsField;
			PeakStatsObservedCharacterField = observedCharacterField;
			PeakStatsAfflictionBarField = afflictionBarField;
			PeakStatsStaminaBarType = staminaBarType;
			PeakStatsAfflictionType = barAfflictionType;

			_harmony.Patch(managerUpdateMethod, prefix: new HarmonyMethod(prunePrefix));
			if (createStaminaBarMethod != null && createPrefix != null)
			{
				_harmony.Patch(createStaminaBarMethod, prefix: new HarmonyMethod(createPrefix));
			}
			_harmony.Patch(staminaBarUpdateMethod, prefix: new HarmonyMethod(barUpdatePrefix));
			_harmony.Patch(fetchReferencesMethod, prefix: new HarmonyMethod(fetchPrefix));

			CleanupPeakStatsUi();
			_peakStatsCompatibilityConfigured = true;
			Logger.LogInfo("[I'm Scoutmaster] Installed PeakStats compatibility guard for controlled Scoutmaster.");
		}
		catch (Exception ex)
		{
			Logger.LogWarning("[I'm Scoutmaster] Failed to install PeakStats compatibility guard: " + ex.Message);
		}
	}

	private static bool PeakerRagdollControlPostfixPrefix(
		[HarmonyArgument("__instance")] CharacterData data,
		[HarmonyArgument("__result")] ref float ragdollControl)
	{
		try
		{
			Character character = CharacterDataCharacterField?.GetValue(data) as Character;
			if (character == null && data != null)
			{
				character = data.GetComponent<Character>();
			}
			if (IsStashedSourceCharacter(character))
			{
				ragdollControl = 1f;
				return false;
			}
			if (IsControlledScoutmasterCharacter(character) && !IsControlledScoutmasterIncapacitated(character))
			{
				ragdollControl = ControlledScoutmasterRagdollControl;
				return false;
			}
		}
		catch
		{
		}

		return true;
	}

	internal static void SetCharacterDeadWithoutReconnect(Character character, bool dead)
	{
		if (character?.data == null)
		{
			return;
		}

		try
		{
			if (CharacterDataDeadField != null)
			{
				CharacterDataDeadField.SetValue(character.data, dead);
				return;
			}
		}
		catch
		{
		}

		try
		{
			if (character.data.dead != dead)
			{
				character.data.dead = dead;
			}
		}
		catch
		{
		}
	}

	internal static bool ShouldSkipReconnectDataUpdate(Character character)
	{
		if (character == null)
		{
			return true;
		}
		if (IsControlledScoutmasterCharacter(character) || IsStashedSourceCharacter(character) || IsCharacterInControlledCreationRoot(character))
		{
			return true;
		}

		try
		{
			if (character.data == null || character.refs == null || character.refs.afflictions == null || character.refs.stats == null)
			{
				return true;
			}

			return character.player == null;
		}
		catch
		{
			return true;
		}
	}

	internal static bool ShouldSuppressSlipperyJellyfishSend(Collider other)
	{
		if (other == null)
		{
			return false;
		}

		try
		{
			if (!CharacterRagdoll.TryGetCharacterFromCollider(other, out Character character))
			{
				return false;
			}

			if (IsStashedSourceCharacter(character) || IsCharacterInControlledCreationRoot(character))
			{
				return true;
			}

			if (character == Character.localCharacter && (character.refs?.view == null || character.refs.view.ViewID <= 0))
			{
				return true;
			}
		}
		catch
		{
		}

		return false;
	}

	internal static bool ShouldSkipSlipperyJellyfishTrigger(int targetID)
	{
		if (targetID <= 0)
		{
			return true;
		}

		try
		{
			PhotonView view = PhotonView.Find(targetID);
			if (view == null)
			{
				return true;
			}

			Character character = view.GetComponent<Character>();
			if (character == null || character.data == null || character.refs == null || character.refs.afflictions == null)
			{
				return true;
			}
			if (IsStashedSourceCharacter(character) || IsCharacterInControlledCreationRoot(character))
			{
				return true;
			}

			return GetBodypart(character, (BodypartType)16)?.Rig == null
				|| GetBodypart(character, (BodypartType)13)?.Rig == null
				|| GetBodypart(character, (BodypartType)0)?.Rig == null
				|| GetBodypart(character, (BodypartType)4)?.Rig == null;
		}
		catch
		{
			return true;
		}
	}

	private void LateUpdate()
	{
		RefreshControlledScoutmasterVisuals();
		if (_session?.IsActive == true)
		{
			TransformHud.TickHideKeepStatusUnlessExternalCamera();
		}
		RefreshRestoredPlayerCamera();
	}

	private void BindConfig()
	{
		ToggleKey = BindEntry(ConfigKey.ToggleKey, KeyCode.G);
		ThrowForce = BindEntry(ConfigKey.ThrowForce, 1600f);
		ThrowUpBias = BindEntry(ConfigKey.ThrowUpBias, 0.18f);
		ThrowFallSeconds = BindEntry(ConfigKey.ThrowFallSeconds, 3f);
		ThirdPersonHeightOffset = BindEntry(ConfigKey.ThirdPersonHeightOffset, 1.35f);
		ThirdPersonDistance = BindEntry(ConfigKey.ThirdPersonDistance, 7.5f);
		SourceStashDistance = BindEntry(ConfigKey.SourceStashDistance, 30f);
		RestoreAtScoutmasterPosition = BindEntry(ConfigKey.RestoreAtScoutmasterPosition, true);
		RestoreGroundOffset = BindEntry(ConfigKey.RestoreGroundOffset, 1.8f);
		MigrateOldThrowDefaults();
		ClampConfigValues();
	}

	private static void MigrateOldThrowDefaults()
	{
		// BepInEx keeps existing cfg values forever. Move untouched historical defaults to the
		// current shorter throw while preserving deliberate user-tuned values.
		if (ThrowForce != null
		    && (Mathf.Abs(ThrowForce.Value - 1500f) < 0.01f
		        || Mathf.Abs(ThrowForce.Value - 3200f) < 0.01f
		        || Mathf.Abs(ThrowForce.Value - 2200f) < 0.01f))
		{
			ThrowForce.Value = 1600f;
		}
		if (ThrowUpBias != null
		    && (Mathf.Abs(ThrowUpBias.Value - 0.3f) < 0.001f
		        || Mathf.Abs(ThrowUpBias.Value - 0.45f) < 0.001f
		        || Mathf.Abs(ThrowUpBias.Value - 0.35f) < 0.001f))
		{
			ThrowUpBias.Value = 0.18f;
		}
	}

	private ConfigEntry<T> BindEntry<T>(ConfigKey configKey, T defaultValue)
	{
		return Config.Bind(GetSectionName(configKey), GetKeyName(configKey), defaultValue, CreateConfigDescription(configKey));
	}

	private ConfigDescription CreateConfigDescription(ConfigKey configKey)
	{
		string description = GetLocalizedDescription(configKey);
		switch (configKey)
		{
			case ConfigKey.ThrowForce:
				return new ConfigDescription(description, new AcceptableValueRange<float>(100f, 2500f), Array.Empty<object>());
			case ConfigKey.ThrowUpBias:
				return new ConfigDescription(description, new AcceptableValueRange<float>(0f, 0.8f), Array.Empty<object>());
			case ConfigKey.ThrowFallSeconds:
				return new ConfigDescription(description, new AcceptableValueRange<float>(0f, 10f), Array.Empty<object>());
			case ConfigKey.ThirdPersonHeightOffset:
				return new ConfigDescription(description, new AcceptableValueRange<float>(-2f, 6f), Array.Empty<object>());
			case ConfigKey.ThirdPersonDistance:
				return new ConfigDescription(description, new AcceptableValueRange<float>(2f, 16f), Array.Empty<object>());
			case ConfigKey.SourceStashDistance:
				return new ConfigDescription(description, new AcceptableValueRange<float>(10f, 200f), Array.Empty<object>());
			case ConfigKey.RestoreGroundOffset:
				return new ConfigDescription(description, new AcceptableValueRange<float>(0.2f, 5f), Array.Empty<object>());
			default:
				return new ConfigDescription(description, null, Array.Empty<object>());
		}
	}

	private void HandleToggleHold()
	{
		// Integrated into Transform: enter/exit is driven by the unified menu key, so the old
		// hold-to-transform branch is gone. While IN scoutmaster form, a short press of the
		// module's own ToggleKey (default G) still triggers the manual fall — a form-specific
		// control that must not conflict with the unified menu key.
		if (_session?.IsActive != true)
		{
			_toggleHoldStartTime = -1f;
			_toggleHoldTriggered = false;
			return;
		}

		KeyCode toggleKey = ToggleKey.Value;
		if (Input.GetKeyDown(toggleKey))
		{
			_toggleHoldStartTime = Time.unscaledTime;
			_toggleHoldTriggered = false;
		}

		if (Input.GetKeyUp(toggleKey))
		{
			float heldSeconds = _toggleHoldStartTime >= 0f ? Time.unscaledTime - _toggleHoldStartTime : 0f;
			bool shortPress = !_toggleHoldTriggered && heldSeconds > 0f && heldSeconds <= ManualFallShortPressMaxSeconds;
			_toggleHoldStartTime = -1f;
			_toggleHoldTriggered = false;

			if (shortPress && Time.unscaledTime - _lastToggleTime >= ToggleDebounceSeconds)
			{
				_lastToggleTime = Time.unscaledTime;
				TriggerControlledScoutmasterManualFall();
			}
		}
	}

	/// <summary>True while the local player is in the controlled-scoutmaster form.</summary>
	internal bool IsFormActive => _session?.IsActive == true;

	/// <summary>State gate shared with the unified menu: may the local player enter this form now?</summary>
	internal bool CanEnterScoutmasterForm()
	{
		return _session?.IsActive != true && CanTransform(Character.localCharacter);
	}

	/// <summary>Enters the controlled-scoutmaster form. Returns false when the request was rejected.</summary>
	internal bool EnterScoutmasterFormExternal()
	{
		if (_switching || _session?.IsActive == true)
		{
			return false;
		}
		StartCoroutine(EnterScoutmasterFormRoutine());
		return true;
	}

	/// <summary>Exits the controlled-scoutmaster form and restores the stashed source player.</summary>
	internal void ExitScoutmasterFormExternal()
	{
		ExitScoutmasterForm(restorePlayer: true);
	}

	private void TriggerControlledScoutmasterManualFall()
	{
		Character controlled = GetControlledScoutmasterCharacter();
		if (controlled == null)
		{
			return;
		}

		TriggerControlledScoutmasterManualFall(controlled);
	}

	private IEnumerator EnterScoutmasterFormRoutine()
	{
		_switching = true;
		Character sourceCharacter = Character.localCharacter;
		if (!CanTransform(sourceCharacter))
		{
			_switching = false;
			yield break;
		}

		// 锁定本次变身的锚点（验证过的玩家位置）：CreateScoutmaster、session 构造、
		// 领队对齐、恢复位置全部复用同一锚点，避免首次实例化/预制体重试/玩家移动
		// 期间位置漂移（"第一次变身位置和恢复位置都偏"）。
		Vector3 transformAnchor = ResolveTransformAnchor(sourceCharacter);
		Vector3 spawnPosition = transformAnchor;
		Quaternion spawnRotation = GetSpawnRotation(sourceCharacter);
		bool sourceWasActive = sourceCharacter.gameObject.activeSelf;
		GameObject scoutmasterObject = CreateScoutmaster(spawnPosition, spawnRotation);
		if (scoutmasterObject == null)
		{
			// 如果当前场景根本不会生成领队（例如机场大厅），预制体永远无法获取，
			// 直接快速失败并给出明确提示，避免无意义的等待。
			if (ImScoutmasterPrefabPool.ResolveScoutmasterPrefab() == null && Object.FindFirstObjectByType<ScoutmasterSpawner>() == null)
			{
				Logger.LogWarning("[I'm Scoutmaster] Cannot transform here: this scene does not spawn Scoutmasters (no Character_Scoutmaster prefab available).");
				_switching = false;
				yield break;
			}

			// Character_Scoutmaster 预制体可能尚未加载（例如本轮游戏还没有生成过领队）。
			// 在短暂窗口内重试，而不是立即失败，因为重试期间预制体可能已经出现。
			float retryUntil = Time.unscaledTime + ScoutmasterPrefabRetrySeconds;
			while (scoutmasterObject == null && Time.unscaledTime < retryUntil)
			{
				yield return new WaitForSecondsRealtime(0.25f);
				if (!CanTransform(sourceCharacter))
				{
					_switching = false;
					yield break;
				}
				transformAnchor = ResolveTransformAnchor(sourceCharacter);
				spawnPosition = transformAnchor;
				spawnRotation = GetSpawnRotation(sourceCharacter);
				scoutmasterObject = CreateScoutmaster(spawnPosition, spawnRotation);
			}

			if (scoutmasterObject == null)
			{
				Logger.LogWarning("[I'm Scoutmaster] Could not create Character_Scoutmaster prefab.");
				_switching = false;
				yield break;
			}
		}

		Scoutmaster scoutmaster = scoutmasterObject.GetComponent<Scoutmaster>();
		Character scoutmasterCharacter = scoutmasterObject.GetComponent<Character>();
		if (scoutmaster == null || scoutmasterCharacter == null)
		{
			yield return null;
			scoutmaster = scoutmasterObject.GetComponent<Scoutmaster>();
			scoutmasterCharacter = scoutmasterObject.GetComponent<Character>();
		}
		if (scoutmaster == null || scoutmasterCharacter == null)
		{
			Logger.LogWarning("[I'm Scoutmaster] Created object is missing Scoutmaster or Character.");
			DestroyScoutmasterObject(scoutmasterObject);
			_switching = false;
			yield break;
		}
		if (!CanTransform(sourceCharacter))
		{
			DestroyScoutmasterObject(scoutmasterObject);
			_switching = false;
			yield break;
		}

		_session = new ActiveScoutmasterSession(sourceCharacter, scoutmasterObject, scoutmaster, scoutmasterCharacter, sourceWasActive, transformAnchor);
		yield return _session.Enter();
		_switching = false;
	}

	private static bool CanTransform(Character sourceCharacter)
	{
		if (sourceCharacter == null)
		{
			FormValidation.ReportFailure(Log, "I'm Scoutmaster", "[I'm Scoutmaster] No local character found.");
			return false;
		}
		if (sourceCharacter.data == null || sourceCharacter.refs == null || sourceCharacter.photonView == null)
		{
			FormValidation.ReportFailure(Log, "I'm Scoutmaster", "[I'm Scoutmaster] Local character is not ready yet.");
			return false;
		}
		if (IsDeadForTransform(sourceCharacter))
		{
			FormValidation.ReportFailure(Log, "I'm Scoutmaster", "[I'm Scoutmaster] Cannot transform after the player has died.");
			return false;
		}
		if (IsControlledScoutmasterCharacter(sourceCharacter))
		{
			FormValidation.ReportFailure(Log, "I'm Scoutmaster", "[I'm Scoutmaster] Already controlling Scoutmaster.");
			return false;
		}
		if (IsZombieOrZombified(sourceCharacter))
		{
			FormValidation.ReportFailure(Log, "I'm Scoutmaster", "[I'm Scoutmaster] Cannot transform while the local character is in zombie form (I'm Zombie compatibility).");
			return false;
		}
		// 菜单每帧查询路径走短时缓存，避免每帧 RaycastAll 的 GC/CPU 开销。
		if (!TryFindStandingGroundBelow(sourceCharacter.Center, MaxTransformGroundProbeDistance, out _, allowCache: true))
		{
			FormValidation.ReportFailure(Log, "I'm Scoutmaster", "[I'm Scoutmaster] Cannot transform here: no standing ground below the player (airport lobby or void).");
			return false;
		}
		FormValidation.ClearFailure("I'm Scoutmaster");
		return true;
	}

	// 变身预检与藏匿点共用的"真实地面"探测：从玩家中心向上偏移 5 后向下射线，
	// 只接受非角色、非触发器碰撞体作为可站立地面，取最近命中点。
	// 与 ResolveSourceStashPosition 的过滤规则保持一致，保证"能变身的地方必有可站立地面"。
	private const float MaxTransformGroundProbeDistance = 40f;
	private const float GroundProbeMaxAboveCenter = 0.25f;

	// ---- 地面探测短时缓存（菜单每帧查询专用）：查询路径 0.3s/2m 内复用结果，进入/恢复路径保持实时 ----
	private const float GroundProbeCacheSeconds = 0.3f;
	private const float GroundProbeCacheMaxMove = 2f;
	private static float _groundProbeCachedTime = float.MinValue;
	private static Vector3 _groundProbeCachedCenter;
	private static bool _groundProbeCachedResult;
	private static RaycastHit _groundProbeCachedHit;

	private static bool TryFindStandingGroundBelow(Vector3 center, float maxDistance, out RaycastHit groundHit, bool allowCache = false)
	{
		groundHit = default(RaycastHit);
		if (!IsFiniteVector(center) || !(maxDistance > 0f))
		{
			return false;
		}
		if (allowCache
			&& Time.unscaledTime - _groundProbeCachedTime <= GroundProbeCacheSeconds
			&& (center - _groundProbeCachedCenter).sqrMagnitude <= GroundProbeCacheMaxMove * GroundProbeCacheMaxMove)
		{
			groundHit = _groundProbeCachedHit;
			return _groundProbeCachedResult;
		}

		bool found = false;
		try
		{
			Vector3 origin = center + Vector3.up * 5f;
			RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
			RaycastHit bestHit = default(RaycastHit);
			float bestDistance = float.MaxValue;
			foreach (RaycastHit hit in hits)
			{
				if (hit.collider == null || hit.collider.GetComponentInParent<Character>() != null)
				{
					continue;
				}
				if (hit.point.y > center.y + GroundProbeMaxAboveCenter)
				{
					continue;
				}
				if (hit.distance < bestDistance)
				{
					bestDistance = hit.distance;
					bestHit = hit;
				}
			}
			if (bestHit.collider != null)
			{
				groundHit = bestHit;
				found = true;
			}
		}
		catch
		{
		}
		if (allowCache)
		{
			_groundProbeCachedTime = Time.unscaledTime;
			_groundProbeCachedCenter = center;
			_groundProbeCachedResult = found;
			_groundProbeCachedHit = groundHit;
		}
		return found;
	}

	private static bool IsZombieOrZombified(Character character)
	{
		if (character == null)
		{
			return false;
		}
		if (character.isZombie)
		{
			return true;
		}

		try
		{
			return character.data != null && character.data.zombified;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsDeadForTransform(Character character)
	{
		if (character?.data == null)
		{
			return false;
		}

		try
		{
			if (character.data.dead)
			{
				return true;
			}
		}
		catch
		{
		}

		try
		{
			return character.data.fullyPassedOut && character.data.deathTimer >= 1f;
		}
		catch
		{
			return false;
		}
	}

	// 变身位置锚点：验证并返回玩家当前位置（Center 优先，transform.position 兜底）。
	// 用于锁定本次变身/恢复的位置基准，防止首次实例化或玩家移动导致位置漂移。
	private static Vector3 ResolveTransformAnchor(Character character)
	{
		if (character == null)
		{
			return Vector3.zero;
		}
		Vector3 center = character.Center;
		if (IsFiniteVector(center))
		{
			return center;
		}
		Vector3 position = ((Component)character).transform.position;
		if (IsFiniteVector(position))
		{
			return position;
		}
		return Vector3.zero;
	}

	private static Quaternion GetSpawnRotation(Character sourceCharacter)
	{
		Vector3 forward = sourceCharacter.data.lookDirection_Flat;
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = ((Component)sourceCharacter).transform.forward;
		}
		forward = Vector3.ProjectOnPlane(forward, Vector3.up);
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}
		return Quaternion.LookRotation(forward.normalized, Vector3.up);
	}

	private static GameObject CreateScoutmaster(Vector3 position, Quaternion rotation)
	{
		GameObject scoutmasterObject = TryCreateNetworkScoutmaster(ScoutmasterResourceName, position, rotation);
		if (scoutmasterObject != null)
		{
			return scoutmasterObject;
		}

		return TryCreateLocalScoutmaster(ScoutmasterResourceName, position, rotation);
	}

	private static GameObject TryCreateNetworkScoutmaster(string resourceName, Vector3 position, Quaternion rotation)
	{
		if (string.IsNullOrWhiteSpace(resourceName) || (!PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode))
		{
			return null;
		}

		// 预检：当预制体池返回 null 时 Photon 会在 Unity 日志里打出刺眼的错误。
		// 只有在预制体确实可解析时才发起网络实例化，其余情况走本地回退与重试。
		if (ImScoutmasterPrefabPool.ResolveScoutmasterPrefab() == null)
		{
			return null;
		}

		try
		{
			GameObject scoutmasterObject = PhotonNetwork.Instantiate(resourceName, position, rotation, 0, BuildControlledScoutmasterInstantiationData());
			return scoutmasterObject;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Photon instantiate failed for " + resourceName + ": " + ex.Message);
			return null;
		}
	}

	private static object[] BuildControlledScoutmasterInstantiationData()
	{
		int ownerActorNumber = 0;
		try
		{
			if (Player.localPlayer?.photonView != null)
			{
				ownerActorNumber = Player.localPlayer.photonView.OwnerActorNr;
			}
			else if (PhotonNetwork.LocalPlayer != null)
			{
				ownerActorNumber = PhotonNetwork.LocalPlayer.ActorNumber;
			}
		}
		catch
		{
			ownerActorNumber = 0;
		}

		return new object[]
		{
			ControlledScoutmasterInstantiationMarker,
			ControlledScoutmasterNetworkProtocol,
			ownerActorNumber
		};
	}

	private static GameObject TryCreateLocalScoutmaster(string resourceName, Vector3 position, Quaternion rotation)
	{
		try
		{
			GameObject prefab = ImScoutmasterPrefabPool.ResolveScoutmasterPrefab();
			if (prefab == null)
			{
				return null;
			}

			GameObject stagingRoot = new GameObject("ImScoutmaster_LocalScoutmaster_Staging");
			stagingRoot.SetActive(false);
			_controlledScoutmasterCreationRoot = stagingRoot.transform;
			try
			{
				GameObject scoutmasterObject = Object.Instantiate(prefab, position, rotation, stagingRoot.transform);
				scoutmasterObject.SetActive(false);
				scoutmasterObject.transform.SetParent(null, true);

				// 关键：备份克隆自运行时实例时，布娃娃部件会保留备份源对象的绝对世界坐标
				// （克隆挂到 DontDestroyOnLoad 根下时部件不随根移动），直接 Instantiate 到
				// 目标位置会导致领队部件散落在场景其他位置（表现为出现在虚空中）。
				// 实例化后立即按目标位置重建整个身体（根 + 所有部件）。
				Character scoutmasterCharacter = scoutmasterObject.GetComponent<Character>();
				if (scoutmasterCharacter != null)
				{
					SetCharacterPositionImmediate(scoutmasterCharacter, position, rotation);
				}
				return scoutmasterObject;
			}
			finally
			{
				_controlledScoutmasterCreationRoot = null;
				Object.Destroy(stagingRoot);
			}
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Local instantiate failed for " + resourceName + ": " + ex.Message);
			return null;
		}
	}

	internal void ExitScoutmasterForm(bool restorePlayer)
	{
		if (_session == null)
		{
			return;
		}

		_switching = true;
		try
		{
			_session.Exit(restorePlayer);
		}
		finally
		{
			_session = null;
			ClearCameraOverride();
			_viewScoutmasterObject = null;
			TransformHud.Restore();
			_switching = false;
		}
	}

	internal static void ForceExitLocalScoutmasterFormBeforeEndGame()
	{
		try
		{
			Plugin instance = Instance;
			if (instance == null || instance._session == null)
			{
				return;
			}

			// If the local player is currently in Scoutmaster form, force-restore
			// to the source character BEFORE RPCEndGame iterates AllCharacters.
			// This ensures Win()/Lose() are applied to the source character
			// (which has valid badgeStatus/timelineInfo) instead of the controlled
			// Scoutmaster body, preventing crashes inside EndCutscene's
			// BadgeUnlocker.SetBadges (empty badgeStatus) and CharacterStats.Win
			// (empty timelineInfo).
			instance.ExitScoutmasterForm(restorePlayer: true);
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Failed to force-exit Scoutmaster form before EndGame: " + ex.Message);
		}
	}

	internal static void PruneInvalidAllCharactersEntries()
	{
		try
		{
			List<Character> allCharacters = Character.AllCharacters;
			if (allCharacters == null || allCharacters.Count == 0)
			{
				return;
			}

			allCharacters.RemoveAll(c => c == null || c.Equals(null) || !IsUsableCharacterForWinSequence(c));
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Failed to prune invalid AllCharacters entries: " + ex.Message);
		}
	}

	private static bool IsUsableCharacterForWinSequence(Character character)
	{
		try
		{
			if (character == null)
			{
				return false;
			}

			GameObject go = ((Component)character).gameObject;
			return go != null;
		}
		catch
		{
			return false;
		}
	}

	internal static void EnsureCharacterStatsTimelinePopulated(CharacterStats stats)
	{
		if (stats == null)
		{
			return;
		}

		try
		{
			List<EndScreen.TimelineInfo> timeline = stats.timelineInfo;
			if (timeline == null || timeline.Count > 0)
			{
				return;
			}

			// Win() accesses timelineInfo[Count - 1] when character.IsLocal. A
			// controlled Scoutmaster body never records timeline entries, which
			// would throw ArgumentOutOfRangeException inside Win(). Seed a single
			// fallback entry at peak height so the win sequence can proceed.
			// 游戏更新后 EndScreen.TimelineInfo 构造函数从 (float, float) 改为
			// (Biome.BiomeType, float, float, EndScreen.TimelineNote)。使用 Peak 生物群落
			// 与 None 备注占位，仅用于让 Win() 序列能取到 timelineInfo[Count-1]。
			timeline.Add(new EndScreen.TimelineInfo(Biome.BiomeType.Peak, CharacterStats.peakHeightInUnits, 0f, EndScreen.TimelineNote.None));
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Failed to populate empty CharacterStats timeline: " + ex.Message);
		}
	}

	internal static bool ShouldExcludeFromEndCutscene(Character character)
	{
		// Controlled Scoutmaster bodies have empty/uninitialized badgeStatus,
		// which crashes BadgeUnlocker.SetBadges (Texture2D(0,1)). Exclude them
		// from EndCutscene's cosmetics pass as a defensive guard in case the
		// force-exit didn't fully clear them (e.g. bodies owned by remote
		// transformed players still present in AllCharacters).
		if (character == null)
		{
			return true;
		}

		try
		{
			if (IsControlledScoutmasterCharacter(character))
			{
				return true;
			}

			CharacterData data = character.data;
			if (data == null || data.badgeStatus == null || data.badgeStatus.Length == 0)
			{
				return true;
			}
		}
		catch
		{
			return true;
		}

		return false;
	}

	internal static void PruneEndCutsceneCharacters()
	{
		try
		{
			List<Character> allCharacters = Character.AllCharacters;
			if (allCharacters == null || allCharacters.Count == 0)
			{
				return;
			}

			allCharacters.RemoveAll(c => c == null || c.Equals(null) || ShouldExcludeFromEndCutscene(c));
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Failed to prune EndCutscene characters: " + ex.Message);
		}
	}

	internal static bool ShouldUseIsolatedCharacterLifecycle(Character character)
	{
		return character != null && HasScoutmasterComponent(character) && (IsCharacterInControlledCreationRoot(character) || IsControlledScoutmasterCharacter(character));
	}

	internal static bool ShouldSkipCharacterRegistration(Character character)
	{
		if (character == null || !HasScoutmasterComponent(character))
		{
			return false;
		}

		if (!IsCharacterInControlledCreationRoot(character) && !IsControlledScoutmasterCharacter(character))
		{
			return false;
		}
		return true;
	}

	internal static void RunIsolatedCharacterAwake(Character character)
	{
		if (character == null)
		{
			return;
		}

		try
		{
			if (!character.isBot && Character.AllCharacters != null && !Character.AllCharacters.Contains(character))
			{
				Character.AllCharacters.Add(character);
			}
			else if (character.isBot && Character.AllBotCharacters != null && !Character.AllBotCharacters.Contains(character))
			{
				Character.AllBotCharacters.Add(character);
			}

			CharacterViewField?.SetValue(character, character.GetComponent<PhotonView>());
			character.InitializeRefs();
			character.input?.Init();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Isolated Character.Awake failed: " + ex.Message);
		}
	}

	internal static void RunIsolatedCharacterStart(Character character)
	{
		if (character == null)
		{
			return;
		}

		try
		{
			if (CharacterStartedField?.GetValue(character) is bool started && started)
			{
				return;
			}

			CharacterStartedField?.SetValue(character, true);
			if (character.refs != null)
			{
				character.refs.hip = GetBodypart(character, BodypartType.Hip);
				character.refs.head = GetBodypart(character, BodypartType.Head);
			}

			UnityEngine.Transform head = ResolveHeadTransform(character);
			Vector3 smoothedCameraPosition = head != null ? head.TransformPoint(Vector3.up) : character.Head;
			CharacterSmoothedCamPosField?.SetValue(character, smoothedCameraPosition);
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Isolated Character.Start failed: " + ex.Message);
		}
	}

	internal static void RunIsolatedCharacterDataAwake(CharacterData data, Character character)
	{
		if (data == null)
		{
			return;
		}

		try
		{
			CharacterDataCharacterField?.SetValue(data, character);
			data.isScoutmaster = true;
			if (data.badgeStatus == null)
			{
				data.badgeStatus = Array.Empty<bool>();
			}
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Isolated CharacterData.Awake failed: " + ex.Message);
		}
	}

	internal static bool ShouldSkipControlledScoutmasterBadgeStatus(CharacterData data)
	{
		Character character = data != null ? data.GetComponent<Character>() : null;
		return IsControlledScoutmasterCharacter(character) || IsCharacterInControlledCreationRoot(character);
	}

	internal static bool ShouldSkipControlledScoutmasterCustomization(CharacterCustomization customization)
	{
		if (customization == null)
		{
			return false;
		}

		Character character = CharacterCustomizationCharacterField?.GetValue(customization) as Character;
		if (character == null)
		{
			character = customization.GetComponent<Character>();
		}
		return IsControlledScoutmasterCharacter(character) || IsCharacterInControlledCreationRoot(character);
	}

	internal static bool ShouldSkipThirdPartyCharacterTracking(Character character)
	{
		return IsControlledScoutmasterCharacter(character) || IsStashedSourceCharacter(character);
	}

	private static bool IsInPeakStatsCleanupGraceWindow()
	{
		return Time.unscaledTime <= _peakStatsCleanupGraceUntil;
	}

	private static void BeginPeakStatsCleanupGraceWindow()
	{
		_peakStatsCleanupGraceUntil = Time.unscaledTime + 1.25f;
	}

	private static bool IsInvalidPeakStatsCharacter(Character character)
	{
		if (character == null)
		{
			return true;
		}

		try
		{
			if (((Component)character).gameObject == null || !((Component)character).gameObject.activeInHierarchy)
			{
				return true;
			}
		}
		catch
		{
			return true;
		}

		try
		{
			if (character.data == null || character.refs == null)
			{
				return true;
			}
		}
		catch
		{
			return true;
		}

		try
		{
			Vector3 head = character.Head;
			if (!IsFiniteVector(head))
			{
				return true;
			}
		}
		catch
		{
			return true;
		}

		return false;
	}

	private static bool IsFiniteVector(Vector3 value)
	{
		return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z);
	}

	private static bool IsFiniteFloat(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value);
	}

	internal static bool ShouldSuppressRuntimePrefabBackupDestroy(UnityEngine.Object target)
	{
		if (target == null)
		{
			return false;
		}

		try
		{
			GameObject targetObject = ResolveDestroyTargetGameObject(target);
			if (targetObject == null)
			{
				return false;
			}

			if (ImScoutmasterPrefabPool.IsRuntimePrefabBackupObject(targetObject))
			{
				return true;
			}
		}
		catch
		{
		}

		return false;
	}

	private static GameObject ResolveDestroyTargetGameObject(UnityEngine.Object target)
	{
		if (target is GameObject gameObject)
		{
			return gameObject;
		}
		if (target is Component component)
		{
			return component.gameObject;
		}

		return null;
	}

	private static bool IsFiniteQuaternion(Quaternion value)
	{
		return IsFiniteFloat(value.x) && IsFiniteFloat(value.y)
			&& IsFiniteFloat(value.z) && IsFiniteFloat(value.w);
	}

	// 有限且非零的方向向量才可用作相机/视线输入；NaN/Infinity 无法通过 sqrMagnitude 比较，必须显式拦截
	private static bool IsUsableDirection(Vector3 value)
	{
		return IsFiniteVector(value) && value.sqrMagnitude >= 0.0001f;
	}

	private static bool IsFiniteVector2(Vector2 value)
	{
		return IsFiniteFloat(value.x) && IsFiniteFloat(value.y);
	}

	// 退出变身后净化源角色视线状态：任何 NaN/Infinity 残留都会让游戏相机永久黑屏
	private static void SanitizeCharacterLookState(Character character)
	{
		if (character == null || character.data == null)
		{
			return;
		}

		try
		{
			Vector2 lookValues = character.data.lookValues;
			if (!IsFiniteVector2(lookValues))
			{
				character.data.lookValues = Vector2.zero;
			}

			if (!IsUsableDirection(character.data.lookDirection)
				|| !IsUsableDirection(character.data.lookDirection_Flat))
			{
				UnityEngine.Transform characterTransform = ((Component)character).transform;
				Vector3 flatLook = Vector3.forward;
				if (characterTransform != null)
				{
					Vector3 projected = Vector3.ProjectOnPlane(characterTransform.forward, Vector3.up);
					if (IsUsableDirection(projected))
					{
						flatLook = projected.normalized;
					}
				}

				character.data.lookDirection = flatLook;
				character.data.lookDirection_Flat = flatLook;
				character.data.lookDirection_Right = Vector3.Cross(Vector3.up, flatLook).normalized;
				character.data.lookDirection_Up = Vector3.Cross(flatLook, character.data.lookDirection_Right).normalized;
			}

			if (!IsUsableDirection(character.data.lookDirection_Right))
			{
				character.data.lookDirection_Right = Vector3.Cross(Vector3.up, character.data.lookDirection_Flat).normalized;
			}
			if (!IsUsableDirection(character.data.lookDirection_Up))
			{
				character.data.lookDirection_Up = Vector3.Cross(character.data.lookDirection, character.data.lookDirection_Right).normalized;
			}
		}
		catch
		{
		}
	}

	private static bool ShouldSkipPeakStatsCharacter(Character character)
	{
		if (IsInPeakStatsCleanupGraceWindow())
		{
			return true;
		}

		return ShouldSkipThirdPartyCharacterTracking(character) || IsInvalidPeakStatsCharacter(character);
	}

	private static bool PeakStatsProximityManagerUpdatePrefix(object __instance)
	{
		PrunePeakStatsControlledScoutmasterBars(__instance);
		return true;
	}

	private static bool PeakStatsCreateStaminaBarPrefix(Character observedCharacter)
	{
		return !ShouldSkipPeakStatsCharacter(observedCharacter);
	}

	private static bool PeakStatsCharacterStaminaBarUpdatePrefix(object __instance)
	{
		if (!ShouldSkipPeakStatsObject(__instance))
		{
			return true;
		}

		DisablePeakStatsComponent(__instance);
		return false;
	}

	private static bool PeakStatsCharacterBarAfflictionFetchReferencesPrefix(object __instance)
	{
		if (!ShouldSkipPeakStatsAffliction(__instance))
		{
			return true;
		}

		DisablePeakStatsComponent(__instance);
		return false;
	}

	private static bool ShouldSkipPeakStatsObject(object instance)
	{
		if (instance == null || PeakStatsObservedCharacterField == null)
		{
			return false;
		}

		try
		{
			return ShouldSkipPeakStatsCharacter(PeakStatsObservedCharacterField.GetValue(instance) as Character);
		}
		catch
		{
			return false;
		}
	}

	private static bool ShouldSkipPeakStatsAffliction(object instance)
	{
		if (instance == null || PeakStatsAfflictionBarField == null)
		{
			return false;
		}

		try
		{
			object staminaBar = PeakStatsAfflictionBarField.GetValue(instance);
			return ShouldSkipPeakStatsObject(staminaBar);
		}
		catch
		{
			return false;
		}
	}

	private static void DisablePeakStatsComponent(object instance)
	{
		try
		{
			if (!(instance is Behaviour behaviour))
			{
				return;
			}

			if (behaviour.enabled)
			{
				behaviour.enabled = false;
			}

			GameObject gameObject = behaviour.gameObject;
			if (gameObject != null && gameObject.activeSelf)
			{
				gameObject.SetActive(false);
			}
		}
		catch
		{
		}
	}

	private static void CleanupPeakStatsUi(bool aggressive = false)
	{
		CleanupPeakStatsObjects(PeakStatsStaminaBarType, aggressive ? ShouldCleanupPeakStatsObjectAggressive : ShouldSkipPeakStatsObject);
		CleanupPeakStatsObjects(PeakStatsAfflictionType, aggressive ? ShouldCleanupPeakStatsAfflictionAggressive : ShouldSkipPeakStatsAffliction);
	}

	private static bool ShouldCleanupPeakStatsObjectAggressive(object instance)
	{
		if (instance == null || PeakStatsObservedCharacterField == null)
		{
			return true;
		}

		try
		{
			return ShouldSkipPeakStatsCharacter(PeakStatsObservedCharacterField.GetValue(instance) as Character);
		}
		catch
		{
			return true;
		}
	}

	private static bool ShouldCleanupPeakStatsAfflictionAggressive(object instance)
	{
		if (instance == null || PeakStatsAfflictionBarField == null)
		{
			return true;
		}

		try
		{
			object staminaBar = PeakStatsAfflictionBarField.GetValue(instance);
			return ShouldCleanupPeakStatsObjectAggressive(staminaBar);
		}
		catch
		{
			return true;
		}
	}

	private static void CleanupPeakStatsObjects(Type componentType, Func<object, bool> shouldRemove)
	{
		if (componentType == null || shouldRemove == null)
		{
			return;
		}

		try
		{
			Object[] objects = Resources.FindObjectsOfTypeAll(componentType);
			if (objects == null || objects.Length == 0)
			{
				return;
			}

			for (int i = 0; i < objects.Length; i++)
			{
				object instance = objects[i];
				if (instance == null || !shouldRemove(instance))
				{
					continue;
				}

				DisablePeakStatsComponent(instance);
				if (instance is Component component && component != null)
				{
					try
					{
						Object.Destroy(component.gameObject);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	private static void PrunePeakStatsControlledScoutmasterBars(object managerInstance)
	{
		if (managerInstance == null || PeakStatsStaminaBarsField == null)
		{
			return;
		}

		try
		{
			if (!(PeakStatsStaminaBarsField.GetValue(managerInstance) is IDictionary dictionary) || dictionary.Count == 0)
			{
				return;
			}

			List<object> keysToRemove = null;
			foreach (DictionaryEntry entry in dictionary)
			{
				if (!(entry.Key is Character character) || !ShouldSkipPeakStatsCharacter(character))
				{
					continue;
				}

				keysToRemove ??= new List<object>();
				keysToRemove.Add(entry.Key);
				DisablePeakStatsComponent(entry.Value);
			}

			if (keysToRemove == null)
			{
				return;
			}

			for (int i = 0; i < keysToRemove.Count; i++)
			{
				dictionary.Remove(keysToRemove[i]);
			}
		}
		catch
		{
		}
	}

	internal static bool IsControlledScoutmaster(Scoutmaster scoutmaster)
	{
		if (scoutmaster == null)
		{
			return false;
		}
		if (PlayerScoutmasterController.IsControlled(scoutmaster))
		{
			return true;
		}
		Character character = ((Component)scoutmaster).GetComponent<Character>();
		if (character != null && character == _cameraOverrideCharacter)
		{
			return true;
		}
		if (_controlledScoutmasterInstanceIds.Contains(scoutmaster.GetInstanceID()))
		{
			return true;
		}

		PhotonView view = scoutmaster.GetComponent<PhotonView>();
		if (view != null && TryRegisterControlledScoutmasterFromInstantiationData(scoutmaster, view))
		{
			return true;
		}
		return view != null && view.ViewID > 0 && _controlledScoutmasterViewIds.Contains(view.ViewID);
	}

	internal static bool IsControlledScoutmasterCharacter(Character character)
	{
		if (character == null)
		{
			return false;
		}
		if (character == _cameraOverrideCharacter || _controlledScoutmasterCharacterInstanceIds.Contains(character.GetInstanceID()))
		{
			return true;
		}

		try
		{
			return IsControlledScoutmaster(character.GetComponent<Scoutmaster>());
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsLocallyControlledScoutmasterCharacter(Character character)
	{
		if (character == null)
		{
			return false;
		}
		if (character == _cameraOverrideCharacter)
		{
			return true;
		}

		try
		{
			Scoutmaster scoutmaster = character.GetComponent<Scoutmaster>();
			if (!PlayerScoutmasterController.IsControlled(scoutmaster))
			{
				return false;
			}
			// 新增：校验本地确为该领队的 Photon owner。所有权信息缺失时按本地受控处理，
			// 仅在能明确判定“owner 不是本地玩家”时才返回 false，避免误伤核心变身流程。
			return IsLocalOwnerOfScoutmaster(scoutmaster);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsLocalOwnerOfScoutmaster(Scoutmaster scoutmaster)
	{
		if (scoutmaster == null)
		{
			return true;
		}
		PhotonView view = scoutmaster.GetComponent<PhotonView>();
		if (view == null || view.ViewID <= 0 || view.Owner == null)
		{
			return true;
		}
		int localActor = GetLocalActorNumber();
		if (localActor <= 0)
		{
			return true;
		}
		return view.Owner.ActorNumber == localActor;
	}

	internal static bool ShouldSuppressControlledScoutmasterInputSample(CharacterInput input)
	{
		if (input == null)
		{
			return false;
		}

		try
		{
			return IsLocallyControlledScoutmasterCharacter(input.GetComponent<Character>());
		}
		catch
		{
			return false;
		}
	}

	internal static Character GetControlledScoutmasterCharacter()
	{
		if (!IsValidCharacter(_cameraOverrideCharacter, requireData: true, requireRefs: true) || !IsLocallyControlledScoutmasterCharacter(_cameraOverrideCharacter))
		{
			return null;
		}

		return _cameraOverrideCharacter;
	}

	internal static bool TryGetControlledScoutmasterForPhotonPlayer(Photon.Realtime.Player photonPlayer, out Character controlledScoutmaster)
	{
		controlledScoutmaster = null;
		if (photonPlayer == null)
		{
			return false;
		}

		int actorNumber = photonPlayer.ActorNumber;

		// 本地玩家：直接返回本地受控领队（保持既有行为）。
		if (IsLocalActor(actorNumber))
		{
			Character local = GetControlledScoutmasterCharacter();
			if (local != null)
			{
				controlledScoutmaster = local;
				return true;
			}
			return false;
		}

		// 远端玩家：按 owner actor number 解析其受控领队（支持多人同时变身）。
		if (_controlledScoutmasterByOwnerActorNumber.TryGetValue(actorNumber, out Character remote))
		{
			if (IsValidCharacter(remote, requireData: false, requireRefs: false))
			{
				controlledScoutmaster = remote;
				return true;
			}
			_controlledScoutmasterByOwnerActorNumber.Remove(actorNumber);
		}
		return false;
	}

	private static int GetLocalActorNumber()
	{
		try
		{
			if (Player.localPlayer != null && Player.localPlayer.photonView != null)
			{
				Photon.Realtime.Player owner = Player.localPlayer.photonView.Owner;
				if (owner != null)
				{
					return owner.ActorNumber;
				}
			}
		}
		catch
		{
		}
		try
		{
			if (PhotonNetwork.LocalPlayer != null)
			{
				return PhotonNetwork.LocalPlayer.ActorNumber;
			}
		}
		catch
		{
		}
		return 0;
	}

	private static bool IsLocalActor(int actorNumber)
	{
		if (actorNumber <= 0)
		{
			return false;
		}
		int localActor = GetLocalActorNumber();
		return localActor > 0 && localActor == actorNumber;
	}

	private static bool IsValidCharacter(Character character, bool requireData, bool requireRefs)
	{
		if (character == null)
		{
			return false;
		}

		try
		{
			GameObject gameObject = ((Component)character).gameObject;
			if (gameObject == null)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}

		if (requireData && character.data == null)
		{
			return false;
		}
		if (requireRefs && character.refs == null)
		{
			return false;
		}

		return true;
	}

	internal static bool IsStashedSourceCharacter(Character character)
	{
		return character != null && _stashedSourceCharacterIds.Contains(character.GetInstanceID());
	}

	internal static bool ShouldDisableInventoryForCharacter(Character character)
	{
		return IsLocallyControlledScoutmasterCharacter(character) || IsStashedSourceCharacter(character);
	}

	internal static void ResetDisabledInventoryState(CharacterItems items, Character character)
	{
		if (character == null && items != null)
		{
			character = items.GetComponent<Character>();
		}
		if (items == null && character != null)
		{
			items = character.refs?.items ?? ((Component)character).GetComponent<CharacterItems>();
		}

		if (items != null
			&& ReferenceEquals(items, _lastDisabledInventoryResetItems)
			&& _lastDisabledInventoryResetFrame == Time.frameCount)
		{
			return;
		}
		if (items != null)
		{
			_lastDisabledInventoryResetItems = items;
			_lastDisabledInventoryResetFrame = Time.frameCount;
		}

		if (items != null)
		{
			items.throwChargeLevel = 0f;
			items.isChargingThrow = false;
		}

		if (!IsLocallyControlledScoutmasterCharacter(character))
		{
			return;
		}

		ClearSelectedInventorySlots(items);
		if (character?.data == null)
		{
			return;
		}

		try
		{
			Item currentItem = character.data.currentItem;
			if (currentItem != null)
			{
				currentItem.CancelUsePrimary();
				currentItem.CancelUseSecondary();
			}
			if (items != null)
			{
				items.UnAttachEquippedItem();
			}
			else
			{
				character.data.currentItem = null;
			}
		}
		catch
		{
			character.data.currentItem = null;
		}
	}

	internal static bool ShouldSkipCharacterMovementUpdate(Character character)
	{
		return IsStashedSourceCharacter(character);
	}

	internal static bool ShouldSkipCharacterFixedUpdate(Character character)
	{
		return IsStashedSourceCharacter(character)
			|| (_skipControlledCharacterFixedUpdate && IsLocallyControlledScoutmasterCharacter(character));
	}

	internal static bool ShouldSkipCharacterNetworkInterpolation(Character character)
	{
		return IsStashedSourceCharacter(character);
	}

	internal static void SmoothRemoteControlledScoutmasterInterpolation(CharacterSyncer syncer, Character character)
	{
		if (syncer == null
			|| character == null
			|| !IsControlledScoutmasterCharacter(character)
			|| IsLocallyControlledScoutmasterCharacter(character)
			|| IsStashedSourceCharacter(character)
			|| CharacterSyncerTargetPositionField == null)
		{
			return;
		}

		try
		{
			object rawTarget = CharacterSyncerTargetPositionField.GetValue(syncer);
			if (!(rawTarget is Vector3 targetPosition) || !IsFiniteVector(targetPosition))
			{
				return;
			}

			Bodypart hip = character.refs?.hip ?? GetBodypart(character, BodypartType.Hip);
			Rigidbody hipRig = hip != null ? hip.Rig : null;
			Vector3 currentPosition = hipRig != null ? hipRig.position : character.Center;
			if (!IsFiniteVector(currentPosition))
			{
				return;
			}

			Vector3 delta = targetPosition - currentPosition;
			float distance = delta.magnitude;
			if (distance < 0.015f)
			{
				return;
			}

			CharacterRagdoll ragdoll = character.refs?.ragdoll;
			if (ragdoll == null)
			{
				return;
			}

			if (distance > 7f)
			{
				ragdoll.MoveAllRigsInDirection(delta);
				ragdoll.HaltBodyVelocity(false);
				return;
			}

			float blend = Mathf.Clamp01(Time.fixedDeltaTime * 8f);
			ragdoll.MoveAllRigsInDirection(delta * blend);
		}
		catch
		{
		}
	}

	internal static bool ShouldSkipCharacterUpdate(Character character)
	{
		// 藏匿的源角色处于冻结/离场状态，其每帧 Character.Update 会在
		// CharacterData.UpdateHasParachute 首行因反向引用缺失而空引用崩溃，
		// 故直接跳过其主更新（与 FixedUpdate/Movement/Items 的跳过策略一致）。
		return IsStashedSourceCharacter(character);
	}

	internal static Character GetCharacterDataOwningCharacter(CharacterData data)
	{
		if (data == null || CharacterDataCharacterField == null)
		{
			return null;
		}
		try
		{
			return CharacterDataCharacterField.GetValue(data) as Character;
		}
		catch
		{
			return null;
		}
	}

	private static void EnsureCharacterDataBackReference(Character character)
	{
		if (character == null || character.data == null || CharacterDataCharacterField == null)
		{
			return;
		}
		try
		{
			if (GetCharacterDataOwningCharacter(character.data) == null)
			{
				Character fallback = character.data.GetComponent<Character>();
				CharacterDataCharacterField.SetValue(character.data, fallback ?? character);
			}
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Failed to ensure CharacterData.character back-reference: " + ex.Message);
		}
	}

	internal static bool ShouldSkipCharacterAfflictionWeightUpdate(CharacterAfflictions afflictions, Character fieldCharacter)
	{
		Character character = fieldCharacter;
		if (character == null && afflictions != null)
		{
			try
			{
				character = CharacterAfflictionsCharacterField?.GetValue(afflictions) as Character;
			}
			catch
			{
				character = afflictions.GetComponent<Character>();
			}
		}

		if (character == null || character.data == null)
		{
			return true;
		}
		if (IsControlledScoutmasterCharacter(character) || IsStashedSourceCharacter(character) || IsCharacterInControlledCreationRoot(character))
		{
			return true;
		}

		try
		{
			Player player = character.player;
			return player == null || player.itemSlots == null || player.backpackSlot == null;
		}
		catch
		{
			return true;
		}
	}

	internal static bool ShouldSuppressControlledCharacterFall(Character character)
	{
		if (!IsStashedSourceCharacter(character))
		{
			return false;
		}

		if (character?.data != null)
		{
			character.data.fallSeconds = 0f;
			character.data.passedOut = false;
			character.data.fullyPassedOut = false;
			character.data.currentRagdollControll = 1f;
		}
		return true;
	}

	internal static bool IsControlledScoutmasterIncapacitated(Character character)
	{
		if (!IsControlledScoutmasterCharacter(character) || character?.data == null)
		{
			return false;
		}

		try
		{
			return character.data.passedOut
				|| character.data.fullyPassedOut
				|| character.data.fallSeconds > 0.05f;
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryHandleControlledScoutmasterJump(Character character)
	{
		if (!IsLocallyControlledScoutmasterCharacter(character) || IsControlledScoutmasterIncapacitated(character))
		{
			return false;
		}

		if (!HasControlledScoutmasterJumpStamina(character))
		{
			ClearControlledScoutmasterJumpInput(character);
			return true;
		}

		if (ApplyControlledScoutmasterLocalJump(character))
		{
			ConsumeControlledScoutmasterJumpStamina(character);
		}
		return true;
	}

	internal static bool ShouldSuppressControlledScoutmasterJumpRpc(Character character)
	{
		return IsLocallyControlledScoutmasterCharacter(character);
	}

	internal static void TriggerControlledScoutmasterManualFall(Character character)
	{
		if (!IsLocallyControlledScoutmasterCharacter(character) || character?.data == null)
		{
			return;
		}
		if (IsControlledScoutmasterIncapacitated(character))
		{
			return;
		}

		try
		{
			Character grabbedPlayer = CharacterDataGrabbedPlayerField?.GetValue(character.data) as Character;
			if (character.data.grabJoint != null || grabbedPlayer != null || character.data.isReaching)
			{
				BroadcastControlledScoutmasterGrabUnattach(character);
				BroadcastControlledScoutmasterStopReaching(character);
			}
		}
		catch
		{
		}

		try
		{
			StopControlledScoutmasterClimb(character.refs?.climbing, character, ControlledScoutmasterManualFallSeconds);
		}
		catch
		{
		}

		try
		{
			character.data.isReaching = false;
			character.data.isSprinting = false;
			if (CharacterFallMethod != null)
			{
				CharacterFallMethod.Invoke(character, new object[] { ControlledScoutmasterManualFallSeconds, 0f });
			}
			else if (character.refs?.view != null)
			{
				character.refs.view.RPC("RPCA_Fall", RpcTarget.All, ControlledScoutmasterManualFallSeconds);
			}
			Log?.LogInfo("[I'm Scoutmaster] Controlled Scoutmaster entered manual fall.");
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Manual fall failed: " + ex.Message);
		}
	}

	internal static bool TryHandleControlledScoutmasterStopClimb(CharacterClimbing climbing, Character character, float setFall)
	{
		if (climbing == null || !IsLocallyControlledScoutmasterCharacter(character) || IsControlledScoutmasterIncapacitated(character))
		{
			return false;
		}

		StopControlledScoutmasterClimb(climbing, character, setFall);
		return true;
	}

	internal static void StopControlledScoutmasterClimb(CharacterClimbing climbing, Character character, float setFall)
	{
		if (character?.data == null)
		{
			return;
		}

		character.data.isClimbing = false;
		character.data.isRopeClimbing = false;
		character.data.isVineClimbing = false;
		character.data.isJumping = false;
		character.data.sinceGrounded = setFall;
		character.data.sincePressClimb = 1f;
		character.data.sinceCanClimb = 0f;
		character.data.climbNormal = Vector3.zero;

		if (character.input != null)
		{
			character.input.usePrimaryIsPressed = false;
			character.input.usePrimaryWasPressed = false;
			character.input.usePrimaryWasReleased = true;
		}

		try
		{
			// 游戏更新后 CharacterClimbing.playerSlide 字段改为私有 _playerSlide + 公共属性 playerSlide。
			// 优先按可用性选择字段或属性写入。
			if (CharacterClimbingPlayerSlideField != null)
			{
				CharacterClimbingPlayerSlideField.SetValue(climbing, Vector2.zero);
			}
			else if (CharacterClimbingPlayerSlideFieldRenamed != null)
			{
				CharacterClimbingPlayerSlideFieldRenamed.SetValue(climbing, Vector2.zero);
			}
			else if (CharacterClimbingPlayerSlideProperty != null && CharacterClimbingPlayerSlideProperty.CanWrite)
			{
				CharacterClimbingPlayerSlideProperty.SetValue(climbing, Vector2.zero);
			}
			CharacterClimbingClimbToggledOnField?.SetValue(climbing, false);
		}
		catch
		{
		}
	}

	private static bool ApplyControlledScoutmasterLocalJump(Character character)
	{
		if (character == null || character.data == null)
		{
			return false;
		}

		try
		{
			if (!character.data.isGrounded && character.data.sinceGrounded > 0.25f && !character.data.isClimbing && !character.data.isRopeClimbing && !character.data.isVineClimbing)
			{
				ClearControlledScoutmasterJumpInput(character);
				return false;
			}

			bool wasGrounded = character.data.isGrounded || character.data.sinceGrounded <= 0.1f;
			if (wasGrounded)
			{
				NudgeControlledScoutmasterForJump(character);
			}

			if (character.refs?.ragdoll?.partList != null)
			{
				foreach (Bodypart part in character.refs.ragdoll.partList)
				{
					Rigidbody rig = part != null ? part.Rig : null;
					if (rig == null || rig.isKinematic)
					{
						continue;
					}

					Vector3 velocity = rig.linearVelocity;
					if (velocity.y < ControlledScoutmasterJumpVelocity)
					{
						rig.linearVelocity = new Vector3(velocity.x, ControlledScoutmasterJumpVelocity, velocity.z);
					}
					rig.WakeUp();
				}
			}

			character.data.sinceJump = 0f;
			character.data.isJumping = true;
			character.data.isGrounded = false;
			character.data.sinceGrounded = 0.25f;
			character.data.groundedFor = 0f;
			ClearControlledScoutmasterJumpInput(character);
			return true;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Local jump failed: " + ex.Message);
			return false;
		}
	}

	private static bool HasControlledScoutmasterJumpStamina(Character character)
	{
		return character?.data != null && character.data.currentStamina >= ControlledScoutmasterJumpStaminaCost;
	}

	private static void ConsumeControlledScoutmasterJumpStamina(Character character)
	{
		if (character?.data == null) return;
		character.data.currentStamina = Mathf.Max(0f, character.data.currentStamina - ControlledScoutmasterJumpStaminaCost);
		character.data.extraStamina = 0f;
	}

	private static void ClearControlledScoutmasterJumpInput(Character character)
	{
		if (character?.input == null) return;
		character.input.jumpWasPressed = false;
		character.input.jumpIsPressed = false;
	}

	private static void NudgeControlledScoutmasterForJump(Character character)
	{
		if (character == null)
		{
			return;
		}

		Vector3 delta = Vector3.up * ControlledScoutmasterJumpGroundClearance;
		try
		{
			((Component)character).transform.position += delta;
			if (character.refs?.ragdoll?.partList == null)
			{
				return;
			}

			foreach (Bodypart part in character.refs.ragdoll.partList)
			{
				if (part == null)
				{
					continue;
				}

				Rigidbody rig = part.Rig;
				if (rig != null)
				{
					rig.position += delta;
					rig.WakeUp();
				}
				else
				{
					part.transform.position += delta;
				}
			}
		}
		catch
		{
		}
	}

	internal static bool TryHandleControlledScoutmasterStartWallClimb(CharacterClimbing climbing, Character character, bool forceAttempt, Vector3 overrideDirection, bool botGrab, float raycastDistance)
	{
		if (!IsLocallyControlledScoutmasterCharacter(character) || IsControlledScoutmasterIncapacitated(character))
		{
			return false;
		}

		if (climbing == null || character == null || character.data == null)
		{
			return true;
		}

		if (character.data.isClimbing || character.data.isRopeClimbing || character.data.isVineClimbing)
		{
			return true;
		}

		if (!CanControlledScoutmasterClimb(climbing))
		{
			return true;
		}

		Vector3 origin = character.Center;
		Vector3 direction = overrideDirection;
		if (!forceAttempt || direction.sqrMagnitude < 0.0001f)
		{
			direction = character.data.lookDirection;
		}
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = character.data.lookDirection_Flat;
		}
		if (direction.sqrMagnitude < 0.0001f && Camera.main != null)
		{
			direction = Camera.main.transform.forward;
		}
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = ((Component)character).transform.forward;
		}
		direction.Normalize();

		float distance = Mathf.Clamp(raycastDistance > 0f ? raycastDistance : 1.65f, 0.75f, 3f);
		if (!TryFindControlledScoutmasterClimbHit(character, origin, direction, distance, out RaycastHit hit))
		{
			return true;
		}

		TryStartControlledScoutmasterClimb(climbing, character, hit.point, hit.normal, "Controlled Scoutmaster climb");
		return true;
	}

	internal static bool CanControlledScoutmasterClimb(CharacterClimbing climbing)
	{
		if (climbing == null)
		{
			return false;
		}

		try
		{
			if (CharacterClimbingCanClimbMethod != null && CharacterClimbingCanClimbMethod.Invoke(climbing, null) is bool canClimb)
			{
				return canClimb;
			}
		}
		catch
		{
		}

		return true;
	}

	internal static bool TryStartControlledScoutmasterClimb(CharacterClimbing climbing, Character character, Vector3 climbPos, Vector3 climbNormal, string context)
	{
		if (climbing == null)
		{
			return false;
		}

		if (CharacterClimbingStartClimbRpcMethod == null)
		{
			Log?.LogWarning("[I'm Scoutmaster] " + context + " start failed: StartClimbRpc method not found.");
			return false;
		}

		try
		{
			object[] args = BuildStartClimbRpcArguments(CharacterClimbingStartClimbRpcMethod, climbPos, climbNormal);
			if (args == null)
			{
				Log?.LogWarning("[I'm Scoutmaster] " + context + " start failed: unsupported StartClimbRpc signature.");
				return false;
			}

			CharacterClimbingStartClimbRpcMethod.Invoke(climbing, args);
			CharacterClimbingClimbToggledOnField?.SetValue(climbing, false);
			CharacterClimbingSinceLastClimbStartedField?.SetValue(climbing, 0f);
			if (character?.data != null)
			{
				character.data.sincePressClimb = 0f;
				character.data.sinceCanClimb = 0f;
			}
			return true;
		}
		catch (TargetInvocationException ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] " + context + " start failed: " + (ex.InnerException?.Message ?? ex.Message));
			return false;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] " + context + " start failed: " + ex.Message);
			return false;
		}
	}

	private static MethodInfo FindCharacterClimbingStartClimbRpcMethod()
	{
		MethodInfo fallback = null;
		foreach (MethodInfo method in typeof(CharacterClimbing).GetMethods(InstanceFlags))
		{
			if (method.Name != "StartClimbRpc")
			{
				continue;
			}

			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length < 2 || parameters[0].ParameterType != typeof(Vector3) || parameters[1].ParameterType != typeof(Vector3))
			{
				continue;
			}

			if (parameters.Length == 2)
			{
				return method;
			}

			fallback ??= method;
		}

		return fallback;
	}

	private static object[] BuildStartClimbRpcArguments(MethodInfo method, Vector3 climbPos, Vector3 climbNormal)
	{
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length < 2)
		{
			return null;
		}
		if (parameters[0].ParameterType != typeof(Vector3) || parameters[1].ParameterType != typeof(Vector3))
		{
			return null;
		}

		object[] args = new object[parameters.Length];
		args[0] = climbPos;
		args[1] = climbNormal;
		for (int i = 2; i < parameters.Length; i++)
		{
			Type parameterType = parameters[i].ParameterType;
			args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
		}

		return args;
	}

	internal static bool TryFindControlledScoutmasterClimbHit(Character character, Vector3 origin, Vector3 direction, float distance, out RaycastHit climbHit)
	{
		climbHit = default;
		RaycastHit[] hits;
		try
		{
			hits = Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Ignore);
		}
		catch
		{
			return false;
		}

		if (hits == null || hits.Length == 0)
		{
			return false;
		}

		Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
		foreach (RaycastHit hit in hits)
		{
			if (hit.collider == null || hit.collider.isTrigger)
			{
				continue;
			}
			Character hitCharacter = hit.collider.GetComponentInParent<Character>();
			if (hitCharacter == character)
			{
				continue;
			}
			if (Vector3.Dot(hit.normal, Vector3.up) > 0.75f)
			{
				continue;
			}

			climbHit = hit;
			return true;
		}

		return false;
	}

	private static Character GetGrabbedPlayer(Character character)
	{
		if (character?.data == null || CharacterDataGrabbedPlayerField == null)
		{
			return null;
		}

		try
		{
			return CharacterDataGrabbedPlayerField.GetValue(character.data) as Character;
		}
		catch
		{
			return null;
		}
	}

	internal static bool TryHandleControlledScoutmasterGrabbingUpdate(CharacterGrabbing grabbing, Character character)
	{
		if (!IsLocallyControlledScoutmasterCharacter(character))
		{
			return false;
		}
		if (grabbing == null || character?.data == null)
		{
			return true;
		}

		return true;
	}

	internal static bool TryHandleControlledScoutmasterGrabAction(CharacterGrabbing grabbing, Character character, Collision collision)
	{
		if (!IsLocallyControlledScoutmasterCharacter(character) || IsControlledScoutmasterIncapacitated(character))
		{
			return false;
		}
		if (grabbing == null || character?.data == null || collision == null)
		{
			return true;
		}
		if (character.data.grabJoint != null || !character.data.isReaching || character.data.sinceLetGoOfFriend < 0.35f)
		{
			return true;
		}
		if (collision.rigidbody == null || collision.collider == null)
		{
			return true;
		}

		if (!CharacterRagdoll.TryGetCharacterFromCollider(collision.collider, out Character target) || target == null || target == character)
		{
			return true;
		}

		BodypartType bodypartType = GetPartType(target, collision.rigidbody);
		if ((int)bodypartType < 0)
		{
			return true;
		}

		try
		{
			Rigidbody scoutHand = GetBodypart(character, BodypartType.Hand_R)?.Rig;
			if (scoutHand == null || target.photonView == null)
			{
				return true;
			}

			Vector3 relativePos = collision.rigidbody.transform.InverseTransformPoint(scoutHand.transform.position);
			SendControlledScoutmasterGrabbingRpc(character, "RPCA_StartReaching");
			if (!SendControlledScoutmasterGrabbingRpc(character, "RPCA_GrabAttach", target.photonView, (int)bodypartType, relativePos))
			{
				CharacterGrabbingGrabAttachMethod?.Invoke(grabbing, new object[] { target.photonView, (int)bodypartType, relativePos });
			}
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Grab attach failed: " + ex.Message);
		}

		return true;
	}

	internal static bool TryGrabControlledScoutmasterTarget(CharacterGrabbing grabbing, Character character, Character sourceCharacter, Vector3 origin, Vector3 direction, float distance, float radius)
	{
		if (grabbing == null || character?.data == null || character.data.grabJoint != null || !character.data.isReaching)
		{
			return false;
		}
		if (direction.sqrMagnitude < 0.0001f)
		{
			return false;
		}

		RaycastHit[] hits;
		try
		{
			hits = Physics.SphereCastAll(origin, Mathf.Max(radius, 0.1f), direction.normalized, Mathf.Max(distance, 0.25f), ~0, QueryTriggerInteraction.Ignore);
		}
		catch
		{
			return false;
		}
		if (hits == null || hits.Length == 0)
		{
			return false;
		}

		Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
		foreach (RaycastHit hit in hits)
		{
			if (TryAttachControlledScoutmasterGrab(grabbing, character, sourceCharacter, hit))
			{
				return true;
			}
		}

		return false;
	}

	private static bool TryAttachControlledScoutmasterGrab(CharacterGrabbing grabbing, Character character, Character sourceCharacter, RaycastHit hit)
	{
		if (hit.collider == null || hit.collider.isTrigger)
		{
			return false;
		}

		Character target = ResolveGrabTargetCharacter(hit.collider);
		if (target == null || target == character || target == sourceCharacter || IsControlledScoutmasterCharacter(target))
		{
			return false;
		}

		Rigidbody targetRig = hit.rigidbody != null ? hit.rigidbody : hit.collider.attachedRigidbody;
		if (targetRig == null)
		{
			Bodypart bodypart = hit.collider.GetComponentInParent<Bodypart>();
			targetRig = bodypart != null ? bodypart.Rig : null;
		}
		if (targetRig == null)
		{
			return false;
		}

		BodypartType bodypartType = GetPartType(target, targetRig);
		if ((int)bodypartType < 0 || target.photonView == null)
		{
			return false;
		}

		try
		{
			SendControlledScoutmasterGrabbingRpc(character, "RPCA_StartReaching");
			Vector3 attachPoint = ResolveControlledGrabAttachPoint(character, targetRig, hit);
			Vector3 relativePos = targetRig.transform.InverseTransformPoint(attachPoint);
			if (!SendControlledScoutmasterGrabbingRpc(character, "RPCA_GrabAttach", target.photonView, (int)bodypartType, relativePos))
			{
				CharacterGrabbingGrabAttachMethod?.Invoke(grabbing, new object[] { target.photonView, (int)bodypartType, relativePos });
			}
			Log?.LogInfo("[I'm Scoutmaster] Controlled Scoutmaster grabbed " + target.characterName + ".");
			return true;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Direct grab failed: " + ex.Message);
			return false;
		}
	}

	private static Vector3 ResolveControlledGrabAttachPoint(Character character, Rigidbody targetRig, RaycastHit hit)
	{
		Vector3 handPosition = ResolveControlledGrabHandPosition(character);
		Vector3 attachPoint = Vector3.zero;
		bool hasAttachPoint = false;
		if (hit.collider != null && IsFiniteVector(handPosition))
		{
			try
			{
				Vector3 closestPoint = hit.collider.ClosestPoint(handPosition);
				if (IsFiniteVector(closestPoint) && Vector3.Distance(closestPoint, handPosition) <= ControlledGrabAttachPointMaxHandDistance)
				{
					attachPoint = closestPoint;
					hasAttachPoint = true;
				}
			}
			catch
			{
			}
		}

		if (!hasAttachPoint && IsFiniteVector(hit.point))
		{
			attachPoint = hit.point;
			hasAttachPoint = true;
		}

		if (!hasAttachPoint && IsFiniteVector(handPosition))
		{
			attachPoint = handPosition;
			hasAttachPoint = true;
		}

		if (!hasAttachPoint && targetRig != null)
		{
			attachPoint = targetRig.worldCenterOfMass;
		}

		return IsFiniteVector(attachPoint) ? attachPoint : Vector3.zero;
	}

	internal static Vector3 ResolveControlledGrabHandPosition(Character character)
	{
		Rigidbody scoutHand = GetBodypart(character, BodypartType.Hand_R)?.Rig;
		if (scoutHand != null && IsFiniteVector(scoutHand.transform.position))
		{
			return scoutHand.transform.position;
		}

		if (character?.data != null)
		{
			Vector3 fallback = character.Center + ResolveThirdPersonLookDirection(character);
			if (IsFiniteVector(fallback))
			{
				return fallback;
			}
		}

		return character != null ? ((Component)character).transform.position : Vector3.zero;
	}

	private static bool SendControlledScoutmasterGrabbingRpc(Character character, string methodName, params object[] args)
	{
		PhotonView view = character?.refs?.view ?? character?.photonView;
		if (view == null || string.IsNullOrEmpty(methodName))
		{
			return false;
		}

		try
		{
			view.RPC(methodName, RpcTarget.All, args ?? Array.Empty<object>());
			return true;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Grab RPC " + methodName + " failed: " + ex.Message);
			return false;
		}
	}

	internal static void BroadcastControlledScoutmasterGrabUnattach(Character character)
	{
		SendControlledScoutmasterGrabbingRpc(character, "RPCA_GrabUnattach");
	}

	internal static void BroadcastControlledScoutmasterStopReaching(Character character)
	{
		SendControlledScoutmasterGrabbingRpc(character, "RPCA_StopReaching");
	}

	private static Character ResolveGrabTargetCharacter(Collider collider)
	{
		if (collider == null)
		{
			return null;
		}
		if (CharacterRagdoll.TryGetCharacterFromCollider(collider, out Character ragdollCharacter) && ragdollCharacter != null)
		{
			return ragdollCharacter;
		}

		Character character = collider.GetComponentInParent<Character>();
		if (character != null)
		{
			return character;
		}

		Bodypart bodypart = collider.GetComponentInParent<Bodypart>();
		if (bodypart == null || BodypartCharacterField == null)
		{
			return null;
		}

		try
		{
			return BodypartCharacterField.GetValue(bodypart) as Character;
		}
		catch
		{
			return null;
		}
	}

	internal static bool TryApplyCameraOverride(Character character, ref Vector3 cameraPosition)
	{
		return false;
	}

	internal static void SetCameraOverride(Character character)
	{
		_cameraOverrideCharacter = character;
		ResetThirdPersonCameraSmoothing();
	}

	internal static void ClearCameraOverride()
	{
		_cameraOverrideCharacter = null;
		ResetThirdPersonCameraSmoothing();
	}

	// 变身期间 HUD 隐藏/恢复统一走共享 TransformHud；常规游玩保留状态栏，
	// 第三方自由相机激活时隐藏领队状态栏并让出其它 HUD。

	internal static void RefreshControlledScoutmasterCamera(object movement)
	{
		// 外部自由相机（PeakSpectatorMode / PeakCinema）激活期间让路，避免双方逐帧互相覆盖相机。
		if (global::Transform.Core.ThirdPartyCameras.ExternalCameraActive)
		{
			return;
		}

		Character controlled = GetControlledScoutmasterCharacter();
		if (movement == null || controlled == null)
		{
			return;
		}

		GetSmoothedThirdPersonCameraPose(controlled, out Vector3 cameraPosition, out Quaternion cameraRotation);
		if (!IsFiniteVector(cameraPosition) || !IsFiniteQuaternion(cameraRotation))
		{
			return;
		}
		try
		{
			MainCameraSpecCharacterProperty?.SetValue(null, null, null);
			MainCameraIsSpectatingField?.SetValue(movement, false);
			MainCameraRagdollCamField?.SetValue(movement, 0f);
			MainCameraCurrentForwardOffsetField?.SetValue(movement, 0f);
			MainCameraTargetPlayerPovPositionField?.SetValue(movement, cameraPosition);
			MainCameraPhysicsRotField?.SetValue(movement, cameraRotation);
			if (movement is Component component)
			{
				component.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
			}
		}
		catch
		{
		}
	}

	private static Vector3 GetThirdPersonCameraPosition(Character character)
	{
		Vector3 forward = character != null && character.data != null ? character.data.lookDirection_Flat : Vector3.zero;
		forward = Vector3.ProjectOnPlane(forward, Vector3.up);
		if (!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f)
		{
			forward = character != null ? Vector3.ProjectOnPlane(((Component)character).transform.forward, Vector3.up) : Vector3.zero;
		}
		if (!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}
		forward.Normalize();
		float distance = Mathf.Clamp(ThirdPersonDistance != null ? ThirdPersonDistance.Value : 7.5f, 2f, 16f);
		return GetThirdPersonCameraTarget(character) - forward * distance;
	}

	private static void GetSmoothedThirdPersonCameraPose(Character character, out Vector3 cameraPosition, out Quaternion cameraRotation)
	{
		Vector3 targetPosition = GetThirdPersonCameraPosition(character);
		Quaternion targetRotation = GetThirdPersonCameraRotation(character);
		if (!IsFiniteVector(targetPosition))
		{
			targetPosition = ResolveThirdPersonCenter(character);
		}
		if (!IsFiniteQuaternion(targetRotation))
		{
			targetRotation = Quaternion.identity;
		}

		bool shouldSnap = !_hasSmoothedThirdPersonCameraPose || Vector3.Distance(_smoothedThirdPersonCameraPosition, targetPosition) > ThirdPersonCameraSnapDistance;
		if (shouldSnap)
		{
			_smoothedThirdPersonCameraPosition = targetPosition;
			_smoothedThirdPersonCameraRotation = targetRotation;
			_hasSmoothedThirdPersonCameraPose = true;
			cameraPosition = targetPosition;
			cameraRotation = targetRotation;
			return;
		}

		_smoothedThirdPersonCameraPosition = Vector3.Lerp(_smoothedThirdPersonCameraPosition, targetPosition, GetExponentialLerp(ThirdPersonCameraPositionSharpness));
		_smoothedThirdPersonCameraRotation = Quaternion.Slerp(_smoothedThirdPersonCameraRotation, targetRotation, GetExponentialLerp(ThirdPersonCameraRotationSharpness));
		cameraPosition = _smoothedThirdPersonCameraPosition;
		cameraRotation = _smoothedThirdPersonCameraRotation;
	}

	private static Vector3 GetThirdPersonCameraTarget(Character character)
	{
		Vector3 target = ResolveThirdPersonCenter(character) + Vector3.up * Mathf.Clamp(ThirdPersonHeightOffset != null ? ThirdPersonHeightOffset.Value : 1.35f, -2f, 6f);
		if (!IsFiniteVector(target))
		{
			target = character != null ? ((Component)character).transform.position : Vector3.zero;
		}
		if (!_hasSmoothedThirdPersonCameraTarget || Vector3.Distance(_smoothedThirdPersonCameraTarget, target) > ThirdPersonCameraSnapDistance)
		{
			_smoothedThirdPersonCameraTarget = target;
			_hasSmoothedThirdPersonCameraTarget = true;
			return target;
		}
		_smoothedThirdPersonCameraTarget = Vector3.Lerp(_smoothedThirdPersonCameraTarget, target, GetExponentialLerp(ThirdPersonCameraFollowSharpness));
		return _smoothedThirdPersonCameraTarget;
	}

	private static Vector3 ResolveThirdPersonCenter(Character character)
	{
		if (character == null)
		{
			return Vector3.zero;
		}
		try
		{
			Vector3 center = character.Center;
			if (IsFiniteVector(center))
			{
				return center;
			}
		}
		catch
		{
		}
		Vector3 position = ((Component)character).transform.position;
		return IsFiniteVector(position) ? position : Vector3.zero;
	}

	private static Quaternion GetThirdPersonCameraRotation(Character character)
	{
		Vector3 lookDirection = ResolveThirdPersonLookDirection(character);
		if (!IsFiniteVector(lookDirection) || lookDirection.sqrMagnitude < 0.0001f)
		{
			lookDirection = Vector3.forward;
		}
		return Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
	}

	private static Vector3 ResolveThirdPersonLookDirection(Character character)
	{
		Vector3 lookDirection = character != null && character.data != null ? character.data.lookDirection : Vector3.zero;
		if (!IsFiniteVector(lookDirection) || lookDirection.sqrMagnitude < 0.0001f)
		{
			lookDirection = character != null && character.data != null ? character.data.lookDirection_Flat : Vector3.zero;
		}
		if (!IsFiniteVector(lookDirection) || lookDirection.sqrMagnitude < 0.0001f)
		{
			lookDirection = character != null ? ((Component)character).transform.forward : Vector3.zero;
		}
		if (!IsFiniteVector(lookDirection) || lookDirection.sqrMagnitude < 0.0001f)
		{
			lookDirection = Vector3.forward;
		}
		return lookDirection.normalized;
	}

	private static float GetExponentialLerp(float sharpness)
	{
		float deltaTime = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime;
		return Mathf.Clamp01(1f - Mathf.Exp(-Mathf.Max(sharpness, 0.01f) * Mathf.Max(deltaTime, 0f)));
	}

	private static void ResetThirdPersonCameraSmoothing()
	{
		_hasSmoothedThirdPersonCameraPose = false;
		_smoothedThirdPersonCameraPosition = Vector3.zero;
		_smoothedThirdPersonCameraRotation = Quaternion.identity;
		_hasSmoothedThirdPersonCameraTarget = false;
		_smoothedThirdPersonCameraTarget = Vector3.zero;
	}

	internal static bool ShouldForceShowControlledScoutmaster(HideTheBody hideTheBody, Character fallbackCharacter)
	{
		if (hideTheBody == null)
		{
			return false;
		}
		Character controlled = GetControlledScoutmasterCharacter();
		if (controlled == null)
		{
			return false;
		}
		if (fallbackCharacter != null && fallbackCharacter == controlled)
		{
			return true;
		}

		Character parentCharacter = hideTheBody.GetComponentInParent<Character>();
		return parentCharacter != null && parentCharacter == controlled;
	}

	internal static void RefreshHideTheBodyVisuals(HideTheBody hideTheBody)
	{
		if (hideTheBody == null)
		{
			return;
		}

		RevealRendererMaterials(hideTheBody.body);
		RevealRendererMaterials(hideTheBody.headRend);
		RevealRendererMaterials(hideTheBody.sash);
		if (hideTheBody.costumes != null)
		{
			foreach (SkinnedMeshRenderer costume in hideTheBody.costumes)
			{
				RevealRendererMaterials(costume);
			}
		}
	}

	internal static void RefreshControlledScoutmasterVisuals()
	{
		if (_viewScoutmasterObject == null)
		{
			return;
		}
		if (GetControlledScoutmasterCharacter() == null)
		{
			_viewScoutmasterObject = null;
			return;
		}

		RestoreRendererVisibility(_viewScoutmasterObject);
		foreach (HideTheBody hideTheBody in _viewScoutmasterObject.GetComponentsInChildren<HideTheBody>(true))
		{
			RefreshHideTheBodyVisuals(hideTheBody);
		}
	}

	private static void HideSourceRenderers(GameObject obj)
	{
		if (obj == null)
		{
			return;
		}

		foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>(true))
		{
			HideRenderer(renderer, _sourceRendererVisualStates);
		}

		foreach (Light light in obj.GetComponentsInChildren<Light>(true))
		{
			HideSourceLight(light);
		}
	}

	private static void RestoreSourceRenderers(GameObject obj)
	{
		if (obj == null)
		{
			return;
		}

		foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>(true))
		{
			RestoreRendererVisibility(renderer, _sourceRendererVisualStates);
		}

		foreach (Light light in obj.GetComponentsInChildren<Light>(true))
		{
			RestoreSourceLight(light);
		}
	}

	private static void HideSourceLight(Light light)
	{
		if (light == null)
		{
			return;
		}

		int id = light.GetInstanceID();
		if (!_sourceLightVisualStates.ContainsKey(id))
		{
			_sourceLightVisualStates[id] = light.enabled;
		}

		light.enabled = false;
	}

	private static void RestoreSourceLight(Light light)
	{
		if (light == null)
		{
			return;
		}

		int id = light.GetInstanceID();
		if (_sourceLightVisualStates.TryGetValue(id, out bool wasEnabled))
		{
			light.enabled = wasEnabled;
			_sourceLightVisualStates.Remove(id);
		}
	}

	private static void RestoreRendererVisibility(GameObject obj)
	{
		if (obj == null)
		{
			return;
		}

		foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>(true))
		{
			RestoreRendererVisibility(renderer, _rendererVisualStates);
		}
	}

	private static void HideRenderer(Renderer renderer, Dictionary<int, RendererVisualState> stateStore)
	{
		if (renderer == null)
		{
			return;
		}

		int id = renderer.GetInstanceID();
		if (!stateStore.ContainsKey(id))
		{
			stateStore[id] = new RendererVisualState(renderer.enabled, renderer.forceRenderingOff);
		}

		renderer.forceRenderingOff = true;
		renderer.enabled = false;
		// 材质级隐藏：游戏代码（如 OnPlayerDataChange）可能重新启用渲染器，
		// 透明材质可以确保即使渲染器被重新启用，模型依然不可见。
		SetRendererMaterialFloat(renderer, "_VertexGhost", 1f);
		SetRendererMaterialFloat(renderer, "_Opacity", 0f);
		SetRendererMaterialFloat(renderer, "_Alpha", 0f);
	}

	private static void RestoreRendererVisibility(Renderer renderer, Dictionary<int, RendererVisualState> stateStore)
	{
		if (renderer == null)
		{
			return;
		}

		int id = renderer.GetInstanceID();
		if (stateStore.TryGetValue(id, out RendererVisualState state))
		{
			renderer.enabled = state.Enabled;
			renderer.forceRenderingOff = state.ForceRenderingOff;
			stateStore.Remove(id);
			if (renderer.enabled && !renderer.forceRenderingOff)
			{
				RevealRendererMaterials(renderer);
			}
			return;
		}

		renderer.enabled = true;
		renderer.forceRenderingOff = false;
	}

	private static void RevealRendererMaterials(Renderer renderer)
	{
		if (renderer == null)
		{
			return;
		}

		SetRendererMaterialFloat(renderer, "_VertexGhost", 0f);
		SetRendererMaterialFloat(renderer, "_Opacity", 1f);
		SetRendererMaterialFloat(renderer, "_Alpha", 1f);
	}

	private static void SetRendererMaterialFloat(Renderer renderer, string propertyName, float value)
	{
		if (renderer == null || string.IsNullOrWhiteSpace(propertyName))
		{
			return;
		}

		try
		{
			Material[] materials = renderer.materials;
			for (int i = 0; i < materials.Length; i++)
			{
				if (materials[i] != null && materials[i].HasProperty(propertyName))
				{
					materials[i].SetFloat(propertyName, value);
				}
			}
		}
		catch
		{
		}
	}

	private static bool HasScoutmasterComponent(Character character)
	{
		if (character == null)
		{
			return false;
		}

		try
		{
			return ((Component)character).GetComponent<Scoutmaster>() != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsCharacterInControlledCreationRoot(Character character)
	{
		if (_controlledScoutmasterCreationRoot == null || character == null)
		{
			return false;
		}

		try
		{
			UnityEngine.Transform characterTransform = ((Component)character).transform;
			return characterTransform == _controlledScoutmasterCreationRoot || characterTransform.IsChildOf(_controlledScoutmasterCreationRoot);
		}
		catch
		{
			return false;
		}
	}

	private static void RegisterControlledScoutmaster(Scoutmaster scoutmaster)
	{
		if (scoutmaster == null)
		{
			return;
		}

		ImScoutmasterPrefabPool.CacheScoutmasterPrefabBackup(scoutmaster.gameObject, "controlled Scoutmaster registration");

		_controlledScoutmasterInstanceIds.Add(scoutmaster.GetInstanceID());
		Character character = scoutmaster.GetComponent<Character>();
		if (character != null)
		{
			_controlledScoutmasterCharacterInstanceIds.Add(character.GetInstanceID());
		}

		PhotonView view = scoutmaster.GetComponent<PhotonView>();
		if (view != null && view.ViewID > 0)
		{
			_controlledScoutmasterViewIds.Add(view.ViewID);
			if (TryReadControlledScoutmasterInstantiationData(view, out int ownerActorNumber))
			{
				_controlledScoutmasterOwnerActorNumbersByViewId[view.ViewID] = ownerActorNumber;
				MapControlledScoutmasterOwner(character, ownerActorNumber);
			}
		}
		EnsureControlledScoutmasterVisualSync(scoutmaster, character, view);
	}

	private static void UnregisterControlledScoutmaster(Scoutmaster scoutmaster)
	{
		if (scoutmaster == null)
		{
			return;
		}

		Character character = scoutmaster.GetComponent<Character>();
		UnmapControlledScoutmasterOwner(character);
		_controlledScoutmasterInstanceIds.Remove(scoutmaster.GetInstanceID());
		if (character != null)
		{
			_controlledScoutmasterCharacterInstanceIds.Remove(character.GetInstanceID());
		}

		PhotonView view = scoutmaster.GetComponent<PhotonView>();
		if (view != null && view.ViewID > 0)
		{
			_controlledScoutmasterViewIds.Remove(view.ViewID);
			_controlledScoutmasterOwnerActorNumbersByViewId.Remove(view.ViewID);
		}
	}

	internal static void UnregisterControlledScoutmasterInstance(Character character, PhotonView view)
	{
		UnmapControlledScoutmasterOwner(character);
		if (character != null)
		{
			_controlledScoutmasterCharacterInstanceIds.Remove(character.GetInstanceID());
			try
			{
				Scoutmaster scoutmaster = character.GetComponent<Scoutmaster>();
				if (scoutmaster != null)
				{
					_controlledScoutmasterInstanceIds.Remove(scoutmaster.GetInstanceID());
				}
			}
			catch
			{
			}
		}

		if (view != null && view.ViewID > 0)
		{
			_controlledScoutmasterViewIds.Remove(view.ViewID);
			_controlledScoutmasterOwnerActorNumbersByViewId.Remove(view.ViewID);
		}
	}

	private static void MapControlledScoutmasterOwner(Character character, int ownerActorNumber)
	{
		if (character == null || ownerActorNumber <= 0)
		{
			return;
		}
		_controlledScoutmasterByOwnerActorNumber[ownerActorNumber] = character;
	}

	private static void UnmapControlledScoutmasterOwner(Character character)
	{
		if (character == null)
		{
			return;
		}

		int instanceId = character.GetInstanceID();
		List<int> staleActors = new List<int>();
		foreach (KeyValuePair<int, Character> kv in _controlledScoutmasterByOwnerActorNumber)
		{
			Character value = kv.Value;
			if (value == null)
			{
				staleActors.Add(kv.Key);
			}
			else if (value.GetInstanceID() == instanceId)
			{
				staleActors.Add(kv.Key);
			}
		}
		foreach (int actor in staleActors)
		{
			_controlledScoutmasterByOwnerActorNumber.Remove(actor);
		}
	}

	private static bool IsActorStillInRoom(int actorNumber)
	{
		if (actorNumber <= 0)
		{
			return false;
		}
		if (IsLocalActor(actorNumber))
		{
			return true;
		}
		Photon.Realtime.Player[] players = PhotonNetwork.PlayerList;
		if (players == null)
		{
			return false;
		}
		for (int i = 0; i < players.Length; i++)
		{
			if (players[i] != null && players[i].ActorNumber == actorNumber)
			{
				return true;
			}
		}
		return false;
	}

	private static void PruneControlledScoutmastersForActor(int actorNumber)
	{
		if (actorNumber <= 0)
		{
			return;
		}

		// 1) 精确移除该 actor 的领队映射，并同步清理实例/ViewID 注册。
		if (_controlledScoutmasterByOwnerActorNumber.TryGetValue(actorNumber, out Character character))
		{
			_controlledScoutmasterByOwnerActorNumber.Remove(actorNumber);
			if (character != null)
			{
				try
				{
					UnregisterControlledScoutmasterInstance(character, character.GetComponent<PhotonView>());
				}
				catch
				{
				}
			}
		}

		// 2) 清扫按 ViewID 的 owner 记录中残留的该 actor 条目。
		List<int> staleViewIds = new List<int>();
		foreach (KeyValuePair<int, int> kv in _controlledScoutmasterOwnerActorNumbersByViewId)
		{
			if (kv.Value == actorNumber)
			{
				staleViewIds.Add(kv.Key);
			}
		}
		foreach (int viewId in staleViewIds)
		{
			_controlledScoutmasterOwnerActorNumbersByViewId.Remove(viewId);
			_controlledScoutmasterViewIds.Remove(viewId);
		}
	}

	private static void SweepStaleControlledScoutmasterOwners()
	{
		if (!PhotonNetwork.InRoom)
		{
			return;
		}

		List<int> staleActors = new List<int>();
		foreach (int actor in _controlledScoutmasterByOwnerActorNumber.Keys)
		{
			if (!IsActorStillInRoom(actor))
			{
				staleActors.Add(actor);
			}
		}
		foreach (int actor in staleActors)
		{
			PruneControlledScoutmastersForActor(actor);
		}

		List<int> staleViewIds = new List<int>();
		foreach (KeyValuePair<int, int> kv in _controlledScoutmasterOwnerActorNumbersByViewId)
		{
			if (kv.Value > 0 && !IsActorStillInRoom(kv.Value))
			{
				staleViewIds.Add(kv.Key);
			}
		}
		foreach (int viewId in staleViewIds)
		{
			_controlledScoutmasterOwnerActorNumbersByViewId.Remove(viewId);
			_controlledScoutmasterViewIds.Remove(viewId);
		}
	}

	internal static void EnsureControlledScoutmasterRegistered(Character character)
	{
		if (character == null)
		{
			return;
		}

		try
		{
			Scoutmaster scoutmaster = character.GetComponent<Scoutmaster>();
			if (scoutmaster == null)
			{
				return;
			}

			PhotonView view = scoutmaster.GetComponent<PhotonView>();
			TryRegisterControlledScoutmasterFromInstantiationData(scoutmaster, view);
		}
		catch
		{
		}
	}

	private static bool TryRegisterControlledScoutmasterFromInstantiationData(Scoutmaster scoutmaster, PhotonView view)
	{
		if (scoutmaster == null || view == null || view.ViewID <= 0)
		{
			return false;
		}
		if (!TryReadControlledScoutmasterInstantiationData(view, out int ownerActorNumber))
		{
			return false;
		}

		ImScoutmasterPrefabPool.CacheScoutmasterPrefabBackup(scoutmaster.gameObject, "controlled Scoutmaster instantiation data");
		_controlledScoutmasterInstanceIds.Add(scoutmaster.GetInstanceID());
		Character character = scoutmaster.GetComponent<Character>();
		if (character != null)
		{
			EnsureCharacterDataBackReference(character);
			_controlledScoutmasterCharacterInstanceIds.Add(character.GetInstanceID());
		}
		_controlledScoutmasterViewIds.Add(view.ViewID);
		_controlledScoutmasterOwnerActorNumbersByViewId[view.ViewID] = ownerActorNumber;
		MapControlledScoutmasterOwner(character, ownerActorNumber);
		EnsureControlledScoutmasterVisualSync(scoutmaster, character, view);
		return true;
	}

	private static bool TryReadControlledScoutmasterInstantiationData(PhotonView view, out int ownerActorNumber)
	{
		ownerActorNumber = 0;
		if (view == null)
		{
			return false;
		}

		object[] data;
		try
		{
			data = view.InstantiationData;
		}
		catch
		{
			return false;
		}
		if (data == null || data.Length < 2 || !(data[0] is string marker) || marker != ControlledScoutmasterInstantiationMarker)
		{
			return false;
		}

		if (data.Length >= 3)
		{
			try
			{
				ownerActorNumber = Convert.ToInt32(data[2], CultureInfo.InvariantCulture);
			}
			catch
			{
				ownerActorNumber = 0;
			}
		}
		return true;
	}

	private static void EnsureControlledScoutmasterVisualSync(Scoutmaster scoutmaster, Character character, PhotonView view)
	{
		if (scoutmaster == null)
		{
			return;
		}

		try
		{
			GameObject obj = scoutmaster.gameObject;
			ControlledScoutmasterVisualSync sync = obj.GetComponent<ControlledScoutmasterVisualSync>() ?? obj.AddComponent<ControlledScoutmasterVisualSync>();
			sync.Initialize(character ?? obj.GetComponent<Character>(), view ?? obj.GetComponent<PhotonView>());
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm Scoutmaster] Failed to install controlled Scoutmaster visual sync: " + ex.Message);
		}
	}

	private static void RegisterStashedSourceCharacter(Character character)
	{
		if (character != null)
		{
			EnsureCharacterDataBackReference(character);
			_stashedSourceCharacterIds.Add(character.GetInstanceID());
		}
	}

	private static void UnregisterStashedSourceCharacter(Character character)
	{
		if (character != null)
		{
			_stashedSourceCharacterIds.Remove(character.GetInstanceID());
		}
	}

	internal static void ClearAssistJumpState(Character character)
	{
		if (character == null || character.data == null)
		{
			return;
		}

		character.data.sincePalJump = ClearBoostReticleTimer;
		character.data.sinceStandOnPlayer = ClearBoostReticleTimer;
		character.data.lastStoodOnPlayer = null;
		character.data.launchedByCannon = false;
	}

	private static UnityEngine.Transform ResolveHeadTransform(Character character)
	{
		if (character == null)
		{
			return null;
		}

		try
		{
			if (character.refs?.head != null)
			{
				return character.refs.head.transform;
			}

			Bodypart head = GetBodypart(character, BodypartType.Head);
			return head != null ? head.transform : null;
		}
		catch
		{
			return null;
		}
	}

	internal static Bodypart GetBodypart(Character character, BodypartType bodypartType)
	{
		if (character == null || CharacterGetBodypartMethod == null)
		{
			return null;
		}

		try
		{
			return CharacterGetBodypartMethod.Invoke(character, new object[] { bodypartType }) as Bodypart;
		}
		catch
		{
			return null;
		}
	}

	private static BodypartType GetPartType(Character character, Rigidbody rigidbody)
	{
		if (character == null || rigidbody == null || CharacterGetPartTypeMethod == null)
		{
			return (BodypartType)(-1);
		}

		try
		{
			object result = CharacterGetPartTypeMethod.Invoke(character, new object[] { rigidbody });
			return result is BodypartType bodypartType ? bodypartType : (BodypartType)(-1);
		}
		catch
		{
			return (BodypartType)(-1);
		}
	}

	internal static void ClearScoutmasterAiState(Scoutmaster scoutmaster)
	{
		if (scoutmaster == null)
		{
			return;
		}

		try
		{
			ScoutmasterTargetForcedUntilField?.SetValue(scoutmaster, 0f);
			ScoutmasterCurrentTargetField?.SetValue(scoutmaster, null);
			ScoutmasterChillForSecondsField?.SetValue(scoutmaster, 0f);
			ScoutmasterIsThrowingField?.SetValue(scoutmaster, false);
		}
		catch
		{
		}
	}

	private static void SetGameObjectActive(GameObject obj, bool active)
	{
		if (obj != null && obj.activeSelf != active)
		{
			obj.SetActive(active);
		}
	}

	private static void ForcePlayerCharacterLookup(Character character)
	{
		if (character == null || Player.localPlayer == null || Player.localPlayer.photonView == null)
		{
			return;
		}

		try
		{
			PlayerHandler handler = PlayerHandlerInstanceProperty?.GetValue(null, null) as PlayerHandler;
			if (handler == null || !(PlayerHandlerCharacterLookupField?.GetValue(handler) is IDictionary<int, Character> lookup))
			{
				return;
			}

			int actorNumber = Player.localPlayer.photonView.OwnerActorNr;
			lookup[actorNumber] = character;
		}
		catch
		{
		}
	}

	private static object CreateOptionableNoneValue(Type optionableType)
	{
		if (optionableType == null)
		{
			return null;
		}

		try
		{
			PropertyInfo noneProperty = optionableType.GetProperty("None", StaticFlags);
			if (noneProperty != null)
			{
				return noneProperty.GetValue(null, null);
			}
		}
		catch
		{
		}

		try
		{
			return Activator.CreateInstance(optionableType);
		}
		catch
		{
			return null;
		}
	}

	private static void ClearSelectedInventorySlots(CharacterItems items)
	{
		if (items == null || CharacterItemsNoneSlotValue == null)
		{
			return;
		}

		try
		{
			CharacterItemsCurrentSelectedSlotField?.SetValue(items, CharacterItemsNoneSlotValue);
			CharacterItemsLastSelectedSlotField?.SetValue(items, CharacterItemsNoneSlotValue);
		}
		catch
		{
		}
	}

	private static void CopyLookStateForRestore(Character from, Character to, Quaternion restoreRotation)
	{
		if (from == null || to == null || from.data == null || to.data == null)
		{
			return;
		}

		// 受控童军领队（尤其是瞬间退出、尚未产生视线输入的会话）可能携带 NaN 视线状态，禁止复制
		to.data.lookValues = IsFiniteVector2(from.data.lookValues) ? from.data.lookValues : Vector2.zero;
		Vector3 flatForward = Vector3.ProjectOnPlane(restoreRotation * Vector3.forward, Vector3.up);
		if (!IsUsableDirection(flatForward))
		{
			flatForward = from.data.lookDirection_Flat;
		}
		if (!IsUsableDirection(flatForward))
		{
			flatForward = ((Component)from).transform.forward;
		}
		if (!IsUsableDirection(flatForward))
		{
			flatForward = Vector3.forward;
		}

		if (!IsUsableDirection(flatForward))
		{
			flatForward = Vector3.forward;
		}

		flatForward.Normalize();
		Vector3 lookDirection = from.data.lookDirection;
		if (!IsUsableDirection(lookDirection))
		{
			lookDirection = flatForward;
		}
		float verticalLook = Mathf.Clamp(lookDirection.y, -0.95f, 0.95f);
		float flatScale = Mathf.Sqrt(Mathf.Max(0f, 1f - verticalLook * verticalLook));
		lookDirection = (flatForward * flatScale + Vector3.up * verticalLook).normalized;
		if (!IsUsableDirection(lookDirection))
		{
			lookDirection = flatForward;
		}

		to.data.lookDirection = lookDirection;
		to.data.lookDirection_Flat = flatForward;
		to.data.lookDirection_Right = Vector3.Cross(Vector3.up, lookDirection).normalized;
		to.data.lookDirection_Up = Vector3.Cross(lookDirection, to.data.lookDirection_Right).normalized;
		((Component)to).transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
	}

	private static void BeginCameraRestoreAssist(Character character)
	{
		if (character == null)
		{
			_cameraRestoreCharacter = null;
			_cameraRestoreUntil = 0f;
			_cameraHealUntil = 0f;
			return;
		}

		_cameraRestoreCharacter = character;
		_cameraRestoreUntil = Time.unscaledTime + CameraRestoreAssistSeconds;
		_cameraHealUntil = Time.unscaledTime + CameraRestoreAssistSeconds + CameraHealSeconds;
		RefreshRestoredPlayerCamera(snapCamera: true);
	}

	private static void RefreshRestoredPlayerCamera()
	{
		RefreshRestoredPlayerCamera(snapCamera: false);
	}

	private static void RefreshRestoredPlayerCamera(bool snapCamera)
	{
		// 外部自由相机激活期间冻结恢复窗口（到期清算顺延），对方关闭后再继续接管。
		if (global::Transform.Core.ThirdPartyCameras.ExternalCameraActive)
		{
			if (_cameraRestoreCharacter != null)
			{
				float now = Time.unscaledTime;
				_cameraRestoreUntil = Mathf.Max(_cameraRestoreUntil, now + CameraRestoreAssistSeconds);
				_cameraHealUntil = Mathf.Max(_cameraHealUntil, now + CameraRestoreAssistSeconds + CameraHealSeconds);
			}
			return;
		}

		if (_cameraRestoreCharacter == null)
		{
			return;
		}
		if (_cameraOverrideCharacter != null)
		{
			_cameraRestoreCharacter = null;
			_cameraRestoreUntil = 0f;
			_cameraHealUntil = 0f;
			return;
		}
		if (_cameraRestoreCharacter.data == null)
		{
			return;
		}

		Character.localCharacter = _cameraRestoreCharacter;

		if (Time.unscaledTime > _cameraRestoreUntil)
		{
			if (Time.unscaledTime > _cameraHealUntil)
			{
				_cameraRestoreCharacter = null;
				_cameraRestoreUntil = 0f;
				_cameraHealUntil = 0f;
				return;
			}

			// 辅助窗口结束后进入自愈观察窗口：游戏相机接管后，若读取到退化的身体部件变换或 FOV，
			// Lerp(x, NaN, t) 在 t=0 时也会因 0*NaN=NaN 永久污染相机（机场瞬间进出变身已复现）。
			// 仅在检测到退化时修复，避免干扰正常相机行为。
			if (!IsCameraDegenerate(_cameraRestoreCharacter))
			{
				return;
			}

			Log?.LogWarning("[I'm Scoutmaster] Detected degenerate camera state after restore; repairing camera, bodyparts and look state.");
			UnityEngine.Transform characterTransform = ((Component)_cameraRestoreCharacter).transform;
			Vector3 anchorPosition = characterTransform.position;
			if (!IsFiniteVector(anchorPosition))
			{
				anchorPosition = Vector3.zero;
			}
			SanitizeCharacterBodyparts(_cameraRestoreCharacter, anchorPosition, characterTransform.rotation);
			SanitizeCharacterLookState(_cameraRestoreCharacter);
			_cameraRestoreUntil = Time.unscaledTime + 1f;
			ResetMainCameraState(_cameraRestoreCharacter, snapCamera: true);
			SanitizeMainCameraProjection();
			return;
		}

		ResetMainCameraState(_cameraRestoreCharacter, snapCamera);
		SanitizeMainCameraProjection();
	}

	// 全局相机自愈巡检：机场出生等未变身场景下主相机/本地角色也可能退化（视锥报错刷屏），
	// 每 0.5 秒校验相机变换/FOV/与角色的距离，仅在确认退化时修复，不干扰正常相机行为与观战。
	private void PatrolLocalCameraHealth()
	{
		// 外部自由相机激活期间不巡检：观战相机会离角色很远且不设置 isSpectating/specCharacter，
		// 会被误判为退化相机而遭到"修复"，与对方模组抢夺镜头。
		if (global::Transform.Core.ThirdPartyCameras.ExternalCameraActive)
		{
			return;
		}

		if (Time.unscaledTime < _nextCameraPatrolTime)
		{
			return;
		}
		_nextCameraPatrolTime = Time.unscaledTime + CameraPatrolIntervalSeconds;

		if (_switching || _session?.IsActive == true || _cameraOverrideCharacter != null || _cameraRestoreCharacter != null)
		{
			return;
		}

		Character character = Character.localCharacter;
		if (character == null || character.data == null)
		{
			return;
		}

		if (!ReferenceEquals(character, _lastCameraPatrolCharacter))
		{
			_lastCameraPatrolCharacter = character;
			_cameraPatrolConsecutiveRepairs = 0;
		}

		// 死亡/昏迷时游戏会主动把相机切到布娃娃/死亡镜头，
		// 此时相机离角色很远是正常现象，不能当作退化修复，否则会反复刷屏。
		try
		{
			if (character.data.dead || character.data.passedOut || character.data.fullyPassedOut)
			{
				return;
			}
		}
		catch
		{
		}

		try
		{
			Camera mainCamera = Camera.main;
			if (mainCamera != null)
			{
				Component movement = mainCamera.GetComponent(typeof(MainCameraMovement)) as Component;
				if (movement != null)
				{
					object specCharacter = MainCameraSpecCharacterProperty?.GetValue(null, null);
					bool isSpectating = MainCameraIsSpectatingField?.GetValue(movement) is bool spectating && spectating;
					if (isSpectating || specCharacter != null)
					{
						return;
					}
				}
			}
		}
		catch
		{
		}

		if (!IsCameraDegenerate(character))
		{
			_cameraPatrolConsecutiveRepairs = 0;
			return;
		}

		// 连续修复仍检测为退化（如游戏过场/结算动画故意把相机拉远）时退避，
		// 避免与游戏镜头争夺导致每 0.5 秒刷一条警告。
		_cameraPatrolConsecutiveRepairs++;
		if (_cameraPatrolConsecutiveRepairs > CameraPatrolMaxConsecutiveRepairs)
		{
			_nextCameraPatrolTime = Time.unscaledTime + CameraPatrolBackoffSeconds;
			return;
		}

		Log?.LogWarning("[I'm Scoutmaster] Detected degenerate camera state outside transform flow; repairing camera, bodyparts and look state.");
		UnityEngine.Transform characterTransform = ((Component)character).transform;
		Vector3 anchorPosition = characterTransform.position;
		if (!IsFiniteVector(anchorPosition))
		{
			anchorPosition = Vector3.zero;
		}
		SanitizeCharacterBodyparts(character, anchorPosition, characterTransform.rotation);
		SanitizeCharacterLookState(character);
		ResetMainCameraState(character, snapCamera: true);
		SanitizeMainCameraProjection();
	}

	private static void ResetMainCameraState(Character character, bool snapCamera)
	{
		if (character == null || character.data == null)
		{
			return;
		}

		try
		{
			MainCameraSpecCharacterProperty?.SetValue(null, null, null);
		}
		catch
		{
		}

		SanitizeCharacterLookState(character);
		Vector3 cameraPosition = ResolvePlayerCameraPosition(character);
		Quaternion cameraRotation = ResolvePlayerCameraRotation(character);
		if (!IsFiniteVector(cameraPosition))
		{
			cameraPosition = ((Component)character).transform.position + Vector3.up;
		}
		if (!IsFiniteQuaternion(cameraRotation))
		{
			cameraRotation = Quaternion.identity;
		}
		try
		{
			Object[] cameraMovements = FindObjectsOfTypeByTypeMethod?.Invoke(null, new object[] { typeof(MainCameraMovement) }) as Object[];
			if (cameraMovements != null)
			{
				foreach (Object movement in cameraMovements)
				{
					if (movement == null)
					{
						continue;
					}

					MainCameraIsSpectatingField?.SetValue(movement, false);
					MainCameraRagdollCamField?.SetValue(movement, 0f);
					MainCameraCurrentForwardOffsetField?.SetValue(movement, 0f);
					MainCameraTargetPlayerPovPositionField?.SetValue(movement, cameraPosition);
					MainCameraPhysicsRotField?.SetValue(movement, cameraRotation);
				}
			}
		}
		catch
		{
		}

		if (snapCamera && Camera.main != null && IsFiniteVector(cameraPosition) && IsFiniteQuaternion(cameraRotation))
		{
			try
			{
				Camera.main.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
			}
			catch
			{
			}
		}
	}

	private static Vector3 ResolvePlayerCameraPosition(Character character)
	{
		UnityEngine.Transform head = ResolveHeadTransform(character);
		if (head != null)
		{
			Vector3 headPosition = head.TransformPoint(Vector3.up);
			if (IsFiniteVector(headPosition))
			{
				return headPosition;
			}
		}

		Vector3 characterHead = character.Head;
		if (IsFiniteVector(characterHead))
		{
			return characterHead;
		}

		Vector3 characterPosition = ((Component)character).transform.position;
		return IsFiniteVector(characterPosition) ? characterPosition + Vector3.up : Vector3.zero;
	}

	private static Quaternion ResolvePlayerCameraRotation(Character character)
	{
		Vector3 lookDirection = character != null && character.data != null ? character.data.lookDirection : Vector3.zero;
		if (!IsUsableDirection(lookDirection) && Camera.main != null)
		{
			lookDirection = Camera.main.transform.forward;
		}
		if (!IsUsableDirection(lookDirection) && character != null)
		{
			lookDirection = ((Component)character).transform.forward;
		}
		if (!IsUsableDirection(lookDirection))
		{
			lookDirection = Vector3.forward;
		}

		return Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
	}

	private static bool IsCameraDegenerate(Character character)
	{
		Camera mainCamera = Camera.main;
		if (mainCamera == null)
		{
			return false;
		}

		UnityEngine.Transform cameraTransform = mainCamera.transform;
		if (!IsFiniteVector(cameraTransform.position) || !IsFiniteQuaternion(cameraTransform.rotation))
		{
			return true;
		}

		float fieldOfView = mainCamera.fieldOfView;
		if (float.IsNaN(fieldOfView) || float.IsInfinity(fieldOfView) || fieldOfView < 1f || fieldOfView > 179f)
		{
			return true;
		}

		if (character != null)
		{
			Vector3 expectedPosition = ResolvePlayerCameraPosition(character);
			if (IsFiniteVector(expectedPosition) && Vector3.Distance(cameraTransform.position, expectedPosition) > CameraHealMaxDistance)
			{
				return true;
			}
		}

		return false;
	}

	private static void SanitizeMainCameraProjection()
	{
		Camera mainCamera = Camera.main;
		if (mainCamera == null)
		{
			return;
		}

		float fieldOfView = mainCamera.fieldOfView;
		if (float.IsNaN(fieldOfView) || float.IsInfinity(fieldOfView) || fieldOfView < 1f || fieldOfView > 179f)
		{
			mainCamera.fieldOfView = CameraHealDefaultFieldOfView;
		}
	}

	// 净化角色布娃娃所有部件的变换：藏匿/恢复传送中若某部件已退化为 NaN/Infinity，
	// 会经 SetCharacterPositionImmediate 的增量计算传染给所有部件，进而被游戏 CharacterCam
	// 每帧读取并永久污染主相机（视锥报错/黑屏）。此处将退化部件强制对齐到锚点姿态。
	private static void SanitizeCharacterBodyparts(Character character, Vector3 anchorPosition, Quaternion anchorRotation)
	{
		if (character == null)
		{
			return;
		}

		try
		{
			UnityEngine.Transform characterTransform = ((Component)character).transform;
			if (!IsFiniteVector(characterTransform.position) || !IsFiniteQuaternion(characterTransform.rotation))
			{
				characterTransform.SetPositionAndRotation(
					IsFiniteVector(anchorPosition) ? anchorPosition : Vector3.zero,
					IsFiniteQuaternion(anchorRotation) ? anchorRotation : Quaternion.identity);
			}

			if (character.refs?.ragdoll?.partList == null)
			{
				return;
			}

			Vector3 safePosition = IsFiniteVector(anchorPosition) ? anchorPosition : characterTransform.position;
			Quaternion safeRotation = IsFiniteQuaternion(anchorRotation) ? anchorRotation : characterTransform.rotation;
			foreach (Bodypart part in character.refs.ragdoll.partList)
			{
				if (part == null)
				{
					continue;
				}

				Rigidbody rig = part.Rig;
				if (rig != null)
				{
					if (!IsFiniteVector(rig.position))
					{
						rig.position = safePosition;
					}
					if (!IsFiniteQuaternion(rig.rotation))
					{
						rig.rotation = safeRotation;
					}
					if (!rig.isKinematic)
					{
						if (!IsFiniteVector(rig.linearVelocity))
						{
							rig.linearVelocity = Vector3.zero;
						}
						if (!IsFiniteVector(rig.angularVelocity))
						{
							rig.angularVelocity = Vector3.zero;
						}
					}
				}
				else if (part.transform != null && (!IsFiniteVector(part.transform.position) || !IsFiniteQuaternion(part.transform.rotation)))
				{
					part.transform.SetPositionAndRotation(safePosition, safeRotation);
				}
			}
		}
		catch
		{
		}
	}

	private static void DestroyScoutmasterObject(GameObject scoutmasterObject)
	{
		if (scoutmasterObject == null)
		{
			return;
		}

		PhotonView view = scoutmasterObject.GetComponent<PhotonView>();
		if (view != null && (PhotonNetwork.InRoom || PhotonNetwork.OfflineMode))
		{
			try
			{
				if (view.ViewID > 0 && (view.IsMine || PhotonNetwork.IsMasterClient))
				{
					PhotonNetwork.Destroy(scoutmasterObject);
					return;
				}
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Photon destroy failed: " + ex.Message);
			}
		}
		Object.Destroy(scoutmasterObject);
	}

	private static void ClampConfigValues()
	{
		ClampFloat(ThrowForce, 100f, 2500f);
		ClampFloat(ThrowUpBias, 0f, 0.8f);
		ClampFloat(ThrowFallSeconds, 0f, 10f);
		ClampFloat(ThirdPersonHeightOffset, -2f, 6f);
		ClampFloat(ThirdPersonDistance, 2f, 16f);
		ClampFloat(SourceStashDistance, 10f, 200f);
		ClampFloat(RestoreGroundOffset, 0.2f, 5f);
	}

	private static void ClampFloat(ConfigEntry<float> entry, float min, float max)
	{
		if (entry != null)
		{
			entry.Value = Mathf.Clamp(entry.Value, min, max);
		}
	}

	private static string GetSectionName(ConfigKey configKey)
	{
		switch (configKey)
		{
			case ConfigKey.ToggleKey:
				return ControlsConfigSectionName;
			case ConfigKey.ThrowForce:
			case ConfigKey.ThrowUpBias:
			case ConfigKey.ThrowFallSeconds:
				return ScoutmasterControlConfigSectionName;
			case ConfigKey.ThirdPersonHeightOffset:
			case ConfigKey.ThirdPersonDistance:
				return CameraConfigSectionName;
			case ConfigKey.SourceStashDistance:
			case ConfigKey.RestoreAtScoutmasterPosition:
			case ConfigKey.RestoreGroundOffset:
				return PlayerRestoreConfigSectionName;
			default:
				return ScoutmasterControlConfigSectionName;
		}
	}

	private static string GetKeyName(ConfigKey configKey)
	{
		switch (configKey)
		{
			case ConfigKey.ToggleKey:
				return "ToggleKey";
			case ConfigKey.ThrowForce:
				return "ThrowForce";
			case ConfigKey.ThrowUpBias:
				return "ThrowUpBias";
			case ConfigKey.ThrowFallSeconds:
				return "ThrowFallSeconds";
			case ConfigKey.ThirdPersonHeightOffset:
				return "ThirdPersonHeightOffset";
			case ConfigKey.ThirdPersonDistance:
				return "ThirdPersonDistance";
			case ConfigKey.SourceStashDistance:
				return "SourceStashDistance";
			case ConfigKey.RestoreAtScoutmasterPosition:
				return "RestoreAtScoutmasterPosition";
			case ConfigKey.RestoreGroundOffset:
				return "RestoreGroundOffset";
			default:
				return string.Empty;
		}
	}

	private static string GetLocalizedDescription(ConfigKey configKey)
	{
		switch (configKey)
		{
			case ConfigKey.ToggleKey:
				return "Short-press key for the manual fall while transformed (form switching itself is driven by the unified Transform menu). Default: G.";
			case ConfigKey.ThrowForce:
				return "Force used when Scoutmaster throws a grabbed player.";
			case ConfigKey.ThrowUpBias:
				return "Upward bias added to throw direction.";
			case ConfigKey.ThrowFallSeconds:
				return "Fall/ragdoll seconds applied to the thrown player.";
			case ConfigKey.ThirdPersonHeightOffset:
				return "Height of the third-person camera target.";
			case ConfigKey.ThirdPersonDistance:
				return "Distance behind Scoutmaster in third person.";
			case ConfigKey.SourceStashDistance:
				return "How far the original player body is dropped below the transform point (plus a fixed depth) so it stays out of sight for everyone while transformed.";
			case ConfigKey.RestoreAtScoutmasterPosition:
				return "Restore the player near Scoutmaster, matching Scoutmaster facing, when leaving Scoutmaster form.";
			case ConfigKey.RestoreGroundOffset:
				return "Height kept above the detected ground when restoring the player, reducing ground clipping after returning from Scoutmaster form.";
			default:
				return string.Empty;
		}
	}

	private sealed class ActiveScoutmasterSession
	{
		private Character _sourceCharacter;
		private readonly GameObject _scoutmasterObject;
		private readonly Scoutmaster _scoutmaster;
		private readonly Character _scoutmasterCharacter;
		private readonly Vector3 _sourceStartPosition;
		private readonly Vector3 _sourceStashPosition;
		private readonly Quaternion _sourceStartRotation;
		private readonly bool _sourceWasActive;
		private readonly List<BodypartPhysicsState> _sourceBodypartPhysicsStates = new List<BodypartPhysicsState>();
		private PlayerScoutmasterController _controller;
		private bool _sourcePhysicsSuspended;
		private bool _sourceViewDisabledByMod;
		private bool _sourceViewSyncFrozenByMod;
		private ViewSynchronization _sourceViewSynchronizationBackup;
		private bool _sourceSyncerDisabledByMod;
		private bool _sourceStashBroadcastPending;

		public bool IsActive => _sourceCharacter != null && _scoutmasterObject != null && _scoutmaster != null && _scoutmasterCharacter != null;

		public ActiveScoutmasterSession(Character sourceCharacter, GameObject scoutmasterObject, Scoutmaster scoutmaster, Character scoutmasterCharacter, bool sourceWasActive, Vector3 transformAnchor)
		{
			_sourceCharacter = sourceCharacter;
			_scoutmasterObject = scoutmasterObject;
			_scoutmaster = scoutmaster;
			_scoutmasterCharacter = scoutmasterCharacter;
			// 使用 TransformSequence 锁定的变身锚点（已验证的玩家位置），而不是再次实时读取
			// sourceCharacter.Center：首次实例化/重试/玩家移动期间位置可能漂移，
			// 全程统一锚点可避免"第一次变身位置和恢复位置都偏"。
			_sourceStartPosition = transformAnchor;
			// 藏匿点：先向下射线找"真实地面"，把源角色放到地面以下，保证被地形遮挡；
			// 找不到地面（虚空掉落中变身等极端情况）时回退到相对下探 + 固定深坑。
			// 目的：远端客户端（其源角色渲染器不会被本模组隐藏，仅本地隐藏）在任何位置变身都看不到原玩家模型。
			_sourceStashPosition = ResolveSourceStashPosition(_sourceStartPosition);
			// 变身朝向与玩家朝向一致：用玩家"看向"的水平方向（lookDirection_Flat）构建初始朝向，
			// 而不是 transform.rotation（身体/模型朝向——玩家站立转头时与看向方向不一致，
			// 会导致领队身体朝向与玩家视角/相机朝向打架）。与 GetSpawnRotation（Instantiate 用）保持一致。
			_sourceStartRotation = GetSpawnRotation(sourceCharacter);
			_sourceWasActive = sourceWasActive;
		}

		// 向下射线找真实地面，藏匿点取"地面以下"位置，确保被地形几何完全遮挡。
		// 只把非角色碰撞体当候选地面，避免把源角色自身/受控领队/其他玩家当成地面；
		// 命中失败（如虚空掉落中变身、下方无碰撞体）时回退到相对下探 + 固定深坑，兜底隐藏。
		private static Vector3 ResolveSourceStashPosition(Vector3 startPosition)
		{
			float drop = Mathf.Max(SourceStashDistance.Value, 10f);
			// 与变身预检共用同一套"真实地面"探测：命中则把源角色放到地面实体内部；
			// 未命中（虚空/非游戏场景，正常流程已被 CanTransform 拦截）回退固定深坑兜底。
			if (TryFindStandingGroundBelow(startPosition, 600f, out RaycastHit groundHit))
			{
				return groundHit.point + Vector3.down * Mathf.Max(drop, 25f);
			}
			return startPosition + Vector3.down * (drop + 400f);
		}

		private readonly struct BodypartPhysicsState
		{
			public readonly Rigidbody Rig;
			public readonly bool IsKinematic;
			public readonly bool DetectCollisions;
			public readonly bool UseGravity;

			public BodypartPhysicsState(Rigidbody rig)
			{
				Rig = rig;
				IsKinematic = rig != null && rig.isKinematic;
				DetectCollisions = rig != null && rig.detectCollisions;
				UseGravity = rig != null && rig.useGravity;
			}
		}

		public IEnumerator Enter()
		{
			PreconfigureScoutmasterForLocalControl();
			_controller = _scoutmasterObject.GetComponent<PlayerScoutmasterController>() ?? _scoutmasterObject.AddComponent<PlayerScoutmasterController>();
			_controller.Initialize(_sourceCharacter, _scoutmaster, _scoutmasterCharacter);
			RegisterControlledScoutmaster(_scoutmaster);
			Character.localCharacter = _scoutmasterCharacter;
			SetGameObjectActive(_scoutmasterObject, true);
			AlignScoutmasterBodyToSource();
			yield return null;

			AlignScoutmasterBodyToSource();
			PrepareScoutmaster();
			// 变身时把玩家的视线状态（lookValues/lookDirection 系）复制给领队，
			// 确保相机切换的第一帧领队就朝向玩家变身前的方向，而不是领队预制体的默认 AI 朝向。
			CopyLookStateForRestore(_sourceCharacter, _scoutmasterCharacter, _sourceStartRotation);
			SetCameraOverride(_scoutmasterCharacter);
			_viewScoutmasterObject = _scoutmasterObject;
			TransformHud.TickHideKeepStatusUnlessExternalCamera();
			RestoreRendererVisibility(_scoutmasterObject);
			// 首次实例化：Unity 物理场景初始化滞后，领队碰撞体可能尚未完成注册。
			// 保持 kinematic 等待片刻再激活物理，避免首帧穿透地板（"第一次变身掉入地下"）。
			// 后续变身物理已就绪，跳过等待即时激活。
			if (!_scoutmasterPhysicsWarmedUp)
			{
				yield return new WaitForSecondsRealtime(0.25f);
				EnableScoutmasterPhysics();
				_scoutmasterPhysicsWarmedUp = true;
			}
			_controller.ActivateDynamicRagdollControl();
			HideSourceCharacter();
			// 等 Photon 完成一次序列化周期，把藏匿点位置广播给远端（sendRate 默认 20Hz），
			// 让远端"最后同步位置"停留在藏匿点（地下）而不是变身位置；
			// 随后再切断同步，远端就永远冻结在藏匿点，看不到原玩家模型。
			yield return new WaitForSecondsRealtime(0.1f);
			if (PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode)
			{
				try
				{
					PhotonNetwork.SendAllOutgoingCommands();
				}
				catch { }
			}
			FreezeSourceNetworkSync();
			_sourceStashBroadcastPending = false;
			Character.localCharacter = _scoutmasterCharacter;
		}

		private void PreconfigureScoutmasterForLocalControl()
		{
			_scoutmasterCharacter.isScoutmaster = true;
			_scoutmasterCharacter.isZombie = false;
			// 注意：这里不激活物理！物理统一由 AlignScoutmasterBodyToSource 在领队
			// SetCharacterPositionImmediate 到玩家位置之后激活（EnableScoutmasterPhysics）。
			// 过早激活（领队尚未就位、预制体物理数据刚注册）会让刚体在瞬移窗口穿透地板，
			// 表现为"变身后掉入地下"（首次变身尤其明显：碰撞体/物理注册未就绪）。
		}

		private void AlignScoutmasterBodyToSource()
		{
			if (_sourceCharacter == null || _scoutmasterCharacter == null || _scoutmasterObject == null)
			{
				return;
			}

			try
			{
				// 用 session 锁定的变身锚点，而不是实时读取 Center：Enter 期间玩家移动
				// 不会让领队位置漂移，领队生成位置 = 恢复锚点，全程一致（首次变身位置/恢复位置不偏）。
				Vector3 sourceCenter = IsFiniteVector(_sourceStartPosition) ? _sourceStartPosition : _sourceCharacter.Center;
				SetCharacterPositionImmediate(_scoutmasterCharacter, sourceCenter, _sourceStartRotation);

				// 防御：备份实例布局异常时，部件可能未随根对齐到玩家位置
				// （表现为领队出现在虚空中）。对齐后校验躯干中心与玩家位置，
				// 偏差过大则用源角色的部件相对布局强制重建。
				Vector3 controlledCenter = _scoutmasterCharacter.Center;
				if (!IsFiniteVector(controlledCenter)
					|| !IsFiniteVector(sourceCenter)
					|| Vector3.Distance(controlledCenter, sourceCenter) > 3f)
				{
					ReanchorScoutmasterBodyToSource(sourceCenter);
				}

				AlignCharacterFeetToGround(_scoutmasterCharacter, _sourceCharacter);
				ApplyControlledScoutmasterRagdollBlend(_scoutmasterCharacter);
				_scoutmasterCharacter.data.fallSeconds = 0f;
				_scoutmasterCharacter.data.isGrounded = _sourceCharacter.data != null && _sourceCharacter.data.isGrounded;
				_scoutmasterCharacter.data.groundPos = _sourceCharacter.data != null ? _sourceCharacter.data.groundPos : _sourceCharacter.Center;
				// 强制物理引擎立即同步刚体/碰撞体的世界包围盒：SetCharacterPositionImmediate 直接改
				// transform 不会即时更新物理包围盒，首帧物理 tick 会用旧包围盒（预制体默认位置）计算碰撞，
				// 导致领队穿透地板。SyncTransforms 覆盖本方法内 Align/Reanchor/贴地的所有位置修改。
				Physics.SyncTransforms();
				if (_scoutmasterPhysicsWarmedUp)
				{
					// 首次变身时此处保持 kinematic（物理由 Enter 协程在碰撞体注册完成后激活）。
					EnableScoutmasterPhysics();
				}
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to align controlled body: " + ex.Message);
			}
		}

		// 备份布局异常时的兜底：用源角色的各部件世界位置直接重设领队部件，
		// 确保领队身体完整出现在玩家位置，而不是散落在场景其他位置（虚空）。
		private void ReanchorScoutmasterBodyToSource(Vector3 sourceCenter)
		{
			if (_scoutmasterCharacter?.refs?.ragdoll?.partList == null || _sourceCharacter?.refs?.ragdoll?.partList == null)
			{
				return;
			}

			try
			{
				((Component)_scoutmasterCharacter).transform.SetPositionAndRotation(sourceCenter, _sourceStartRotation);

				List<Bodypart> sourceParts = _sourceCharacter.refs.ragdoll.partList;
				List<Bodypart> scoutParts = _scoutmasterCharacter.refs.ragdoll.partList;
				for (int i = 0; i < scoutParts.Count; i++)
				{
					Bodypart part = scoutParts[i];
					if (part == null)
					{
						continue;
					}

					// 优先复用源角色同索引部件的世界位置（保持身体形态），
					// 源部件缺失/异常时退回领队中心点。
					Vector3 target = sourceCenter;
					if (i < sourceParts.Count && sourceParts[i] != null)
					{
						Bodypart sourcePart = sourceParts[i];
						Vector3 sourcePartPosition = sourcePart.Rig != null ? sourcePart.Rig.position : sourcePart.transform.position;
						if (IsFiniteVector(sourcePartPosition))
						{
							target = sourcePartPosition;
						}
					}

					if (part.Rig != null)
					{
						part.Rig.position = target;
						if (!part.Rig.isKinematic)
						{
							part.Rig.linearVelocity = Vector3.zero;
							part.Rig.angularVelocity = Vector3.zero;
						}
					}
					else if (part.transform != null)
					{
						part.transform.position = target;
					}
				}
			}
			catch
			{
			}
		}

		private void EnableScoutmasterPhysics()
		{
			if (_scoutmasterCharacter == null || _scoutmasterCharacter.refs?.ragdoll?.partList == null)
			{
				return;
			}

			try
			{
				foreach (Bodypart part in _scoutmasterCharacter.refs.ragdoll.partList)
				{
					Rigidbody rig = part != null ? part.Rig : null;
					if (rig == null)
					{
						continue;
					}

					rig.isKinematic = false;
					rig.detectCollisions = true;
					rig.useGravity = true;
					rig.WakeUp();
				}
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to enable controlled physics: " + ex.Message);
			}
		}

		public void Tick()
		{
			if (!IsActive)
			{
				Log?.LogWarning("[I'm Scoutmaster] Session became inactive.");
				Instance?.ExitScoutmasterForm(restorePlayer: true);
				return;
			}

			Character.localCharacter = _scoutmasterCharacter;
			KeepScoutmasterAwake();
			if (_sourceStashBroadcastPending)
			{
				// 等待 Photon 把藏匿点位置序列化广播给远端：期间让源角色稳定停在藏匿点，
				// 不执行 KeepSourceOutOfPlay / FollowSourceCharacterToControlledBodySafely，
				// 否则源角色被拉回领队身边，远端最后同步到的还是可见的变身位置。
				return;
			}
			KeepSourceOutOfPlay();
			FollowSourceCharacterToControlledBodySafely();
		}

		private void FollowSourceCharacterToControlledBodySafely()
		{
			if (_sourceCharacter == null || _scoutmasterCharacter == null)
			{
				return;
			}

			try
			{
				Vector3 targetCenter = _scoutmasterCharacter.Center;
				if (!IsFiniteVector(targetCenter))
				{
					return;
				}

				Vector3 currentCenter = _sourceCharacter.Center;
				if (IsFiniteVector(currentCenter) && (targetCenter - currentCenter).sqrMagnitude < 0.0001f)
				{
					return;
				}

				Quaternion targetRotation = ((Component)_scoutmasterCharacter).transform.rotation;
				if (!IsFiniteQuaternion(targetRotation))
				{
					targetRotation = _sourceStartRotation;
				}

				SetCharacterPositionImmediate(_sourceCharacter, targetCenter, targetRotation);
				SanitizeCharacterBodyparts(_sourceCharacter, targetCenter, targetRotation);
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to safely follow source character to controlled body: " + ex.Message);
			}
		}

		public void Exit(bool restorePlayer)
		{
			if (restorePlayer && _sourceCharacter == null)
			{
				Character recovered = TryRecoverOriginalLocalCharacter();
				if (recovered != null)
				{
					_sourceCharacter = recovered;
					Log?.LogInfo("[I'm Scoutmaster] Recovered source character reference during exit: " + recovered.name + ".");
				}
			}

			Vector3 restorePosition = ResolveRestorePosition();
			Quaternion restoreRotation = ResolveRestoreRotation();

			if (_controller != null)
			{
				_controller.enabled = false;
			}

			RestoreRendererVisibility(_scoutmasterObject);
			ClearCameraOverride();
			_viewScoutmasterObject = null;
			TransformHud.Restore();

			if (restorePlayer && _sourceCharacter != null)
			{
				CopyLookStateForRestore(_scoutmasterCharacter, _sourceCharacter, restoreRotation);
				RestoreSourceCharacter(restorePosition, restoreRotation);
				SanitizeCharacterLookState(_sourceCharacter);
				ForcePlayerCharacterLookup(_sourceCharacter);
				RestoreSourceControlState(_sourceCharacter);
				Character.localCharacter = _sourceCharacter;
				BeginCameraRestoreAssist(_sourceCharacter);
			}

			UnregisterStashedSourceCharacter(_sourceCharacter);
			ResumeSourceNetworkSync();
			UnregisterControlledScoutmaster(_scoutmaster);
			BeginPeakStatsCleanupGraceWindow();
			CleanupPeakStatsUi(aggressive: true);
			DestroyScoutmasterObject(_scoutmasterObject);
		}

		private Vector3 ResolveRestorePosition()
		{
			// 默认恢复到童军领队当前位置；用户在配置里关闭 RestoreAtScoutmasterPosition 时，
			// 改为恢复到变身起点，避免领队被炸飞/掉入虚空后把玩家一起带进虚空。
			bool restoreAtScoutmaster = Plugin.RestoreAtScoutmasterPosition == null || Plugin.RestoreAtScoutmasterPosition.Value;
			Vector3 requestedPosition = _sourceStartPosition;
			if (restoreAtScoutmaster && _scoutmasterCharacter != null)
			{
				Vector3 center = _scoutmasterCharacter.Center;
				if (IsFiniteVector(center))
				{
					requestedPosition = center;
				}
				else
				{
					Vector3 transformPosition = ((Component)_scoutmasterCharacter).transform.position;
					if (IsFiniteVector(transformPosition))
					{
						requestedPosition = transformPosition;
					}
				}
			}

			return ResolveSafeRestorePosition(requestedPosition, _scoutmasterCharacter, _sourceCharacter, _sourceStartPosition, _sourceStashPosition);
		}

		private Quaternion ResolveRestoreRotation()
		{
			if (_scoutmasterCharacter == null)
			{
				return _sourceStartRotation;
			}

			Quaternion rotation = ((Component)_scoutmasterCharacter).transform.rotation;
			Vector3 flatForward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
			if (flatForward.sqrMagnitude < 0.0001f && _scoutmasterCharacter.data != null)
			{
				flatForward = _scoutmasterCharacter.data.lookDirection_Flat;
			}
			if (flatForward.sqrMagnitude < 0.0001f && _scoutmasterCharacter.data != null)
			{
				flatForward = Vector3.ProjectOnPlane(_scoutmasterCharacter.data.lookDirection, Vector3.up);
			}
			if (flatForward.sqrMagnitude < 0.0001f)
			{
				return _sourceStartRotation;
			}

			return Quaternion.LookRotation(flatForward.normalized, Vector3.up);
		}

		private static Character TryRecoverOriginalLocalCharacter()
		{
			try
			{
				Photon.Realtime.Player localOwner = Player.localPlayer?.photonView?.Owner;
				if (localOwner == null || Character.AllCharacters == null)
				{
					return null;
				}

				foreach (Character candidate in Character.AllCharacters)
				{
					if (candidate == null || candidate.data == null || candidate.photonView == null)
					{
						continue;
					}
					if (candidate.photonView.Owner != localOwner)
					{
						continue;
					}
					if (IsControlledScoutmasterCharacter(candidate) || candidate.isZombie || candidate.isScoutmaster)
					{
						continue;
					}

					return candidate;
				}
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to recover original local character: " + ex.Message);
			}

			return null;
		}

		private static void AlignCharacterFeetToGround(Character controlledCharacter, Character sourceCharacter)
		{
			if (controlledCharacter == null || sourceCharacter == null)
			{
				return;
			}

			if (!TryGetCharacterGroundY(sourceCharacter, out float sourceGroundY) || !TryGetCharacterGroundY(controlledCharacter, out float controlledGroundY))
			{
				return;
			}

			float groundOffset = sourceGroundY - controlledGroundY;
			if (float.IsNaN(groundOffset) || float.IsInfinity(groundOffset))
			{
				return;
			}

			// 只允许抬升（领队陷地/低于源地面时上抬），绝不下移：
			// 下移会把领队压入地下，或在地面探测异常时把领队拉向下方虚空。
			// 抬升量也限制在 2m 内，避免地面探测误判时大幅弹跳。
			if (groundOffset <= 0.001f || groundOffset > 2f)
			{
				return;
			}

			SetCharacterPositionImmediate(
				controlledCharacter,
				controlledCharacter.Center + Vector3.up * groundOffset,
				((Component)controlledCharacter).transform.rotation);
		}

		private static bool TryGetCharacterGroundY(Character character, out float groundY)
		{
			groundY = 0f;
			if (character == null)
			{
				return false;
			}

			Vector3 center = character.Center;
			if (character.data != null && IsFiniteVector(character.data.groundPos))
			{
				Vector3 groundPos = character.data.groundPos;
				Vector3 flatDelta = Vector3.ProjectOnPlane(groundPos - center, Vector3.up);
				if (flatDelta.sqrMagnitude <= 64f && Mathf.Abs(groundPos.y - center.y) <= 12f)
				{
					groundY = groundPos.y;
					return true;
				}
			}

			if (TryGetCharacterColliderBottomY(character, out groundY))
			{
				return true;
			}

			if (IsFiniteVector(center))
			{
				groundY = center.y;
				return true;
			}

			Vector3 position = ((Component)character).transform.position;
			if (!IsFiniteVector(position))
			{
				return false;
			}

			groundY = position.y;
			return true;
		}

		private static bool TryGetCharacterColliderBottomY(Character character, out float bottomY)
		{
			bottomY = 0f;
			if (character?.refs?.ragdoll?.partList == null)
			{
				return false;
			}

			bool found = false;
			foreach (Bodypart part in character.refs.ragdoll.partList)
			{
				if (part == null)
				{
					continue;
				}

				Collider[] colliders = part.GetComponentsInChildren<Collider>(true);
				if (colliders != null)
				{
					foreach (Collider collider in colliders)
					{
						if (collider == null || !collider.enabled)
						{
							continue;
						}

						Bounds bounds = collider.bounds;
						Vector3 min = bounds.min;
						if (!IsFiniteVector(min))
						{
							continue;
						}

						if (!found || min.y < bottomY)
						{
							bottomY = min.y;
							found = true;
						}
					}
				}

				Vector3 partPosition = part.Rig != null ? part.Rig.position : part.transform.position;
				if (!IsFiniteVector(partPosition))
				{
					continue;
				}

				if (!found || partPosition.y < bottomY)
				{
					bottomY = partPosition.y;
					found = true;
				}
			}

			return found;
		}

		private static Vector3 ResolveSafeRestorePosition(Vector3 requestedPosition, Character scoutmasterCharacter, Character sourceCharacter, Vector3 fallbackPosition, Vector3 sourceStashPosition)
		{
			float groundOffset = Mathf.Clamp(RestoreGroundOffset.Value, 0.2f, 5f);
			Vector3 anchor = IsFiniteVector(requestedPosition) ? requestedPosition : fallbackPosition;
			if (IsStashRestoreAnchor(anchor, sourceStashPosition, fallbackPosition))
			{
				Log?.LogWarning("[I'm Scoutmaster] Restore anchor matched the hidden source stash column; falling back to transform start.");
				anchor = fallbackPosition;
			}
			if (!IsFiniteVector(anchor))
			{
				anchor = sourceCharacter != null ? ((Component)sourceCharacter).transform.position : Vector3.zero;
			}

			// 先检查当前童军领队位置附近的脚下地面；只接受不高于角色中心的地面。
			// 若当前位置不安全，再回退到变身起点附近，避免被头顶可站立面吸上去。
			float anchorMaxGroundY = anchor.y + GroundProbeMaxAboveCenter;
			float fallbackMaxGroundY = IsFiniteVector(fallbackPosition)
				? fallbackPosition.y + GroundProbeMaxAboveCenter
				: float.PositiveInfinity;
			if (TryFindRestoreGround(anchor + Vector3.up * 5f, 30f, scoutmasterCharacter, sourceCharacter, anchorMaxGroundY, out RaycastHit hit)
				|| TryFindRestoreGround(anchor + Vector3.up * 12f, 80f, scoutmasterCharacter, sourceCharacter, anchorMaxGroundY, out hit)
				|| TryFindRestoreGround(fallbackPosition + Vector3.up * 5f, 60f, scoutmasterCharacter, sourceCharacter, fallbackMaxGroundY, out hit)
				|| TryFindRestoreGround(fallbackPosition + Vector3.up * 60f, 200f, scoutmasterCharacter, sourceCharacter, fallbackMaxGroundY, out hit))
			{
				Vector3 candidate = hit.point + Vector3.up * groundOffset;
				if (!IsStashRestoreAnchor(candidate, sourceStashPosition, fallbackPosition))
				{
					return candidate;
				}

				Log?.LogWarning("[I'm Scoutmaster] Restore ground resolved near the hidden source stash; retrying from transform start.");
				if (TryFindRestoreGround(fallbackPosition + Vector3.up * 5f, 60f, scoutmasterCharacter, sourceCharacter, fallbackMaxGroundY, out hit)
					|| TryFindRestoreGround(fallbackPosition + Vector3.up * 60f, 200f, scoutmasterCharacter, sourceCharacter, fallbackMaxGroundY, out hit))
				{
					return hit.point + Vector3.up * groundOffset;
				}
			}

			// 4 次 raycast 全部失败：anchor 极大概率位于虚空（飞出地图/坠入深渊）。
			// 此时绝不能把玩家恢复到 anchor，必须回退到变身起点 fallbackPosition；
			// 如果连 fallbackPosition 都无效，再退到 sourceCharacter 当前位置作为最后保底。
			Vector3 safeFallback = IsFiniteVector(fallbackPosition) ? fallbackPosition : anchor;
			if (sourceCharacter != null && !IsFiniteVector(safeFallback))
			{
				safeFallback = ((Component)sourceCharacter).transform.position;
			}
			if (IsStashRestoreAnchor(safeFallback, sourceStashPosition, fallbackPosition))
			{
				safeFallback = IsFiniteVector(fallbackPosition) ? fallbackPosition : safeFallback + Vector3.up * Mathf.Max(SourceStashDistance.Value, 10f);
			}

			Log?.LogWarning("[I'm Scoutmaster] No restore ground found below or above anchor " + anchor + "; restoring at safe fallback " + safeFallback + " instead.");
			return safeFallback + Vector3.up * groundOffset;
		}

		private static bool IsStashRestoreAnchor(Vector3 candidate, Vector3 stashPosition, Vector3 transformStartPosition)
		{
			if (!IsFiniteVector(candidate) || !IsFiniteVector(stashPosition) || !IsFiniteVector(transformStartPosition))
			{
				return false;
			}

			Vector2 candidateFlat = new Vector2(candidate.x, candidate.z);
			Vector2 stashFlat = new Vector2(stashPosition.x, stashPosition.z);
			Vector2 startFlat = new Vector2(transformStartPosition.x, transformStartPosition.z);
			float stashColumnDistance = Vector2.Distance(candidateFlat, stashFlat);
			float startColumnDistance = Vector2.Distance(candidateFlat, startFlat);
			float buriedDepth = transformStartPosition.y - candidate.y;
			float stashDepthDelta = Mathf.Abs(candidate.y - stashPosition.y);

			return (stashColumnDistance <= 8f || startColumnDistance <= 8f)
				&& buriedDepth >= 8f
				&& stashDepthDelta <= Mathf.Max(SourceStashDistance.Value, 10f) + 20f;
		}

		// 向上 raycast：用于 anchor 已掉到地图深处（虚空）的场景。
		// 此时向下 raycast 永远不会命中真实地面，必须向上才能找到玩家头顶的地形。
		private static bool TryFindRestoreGroundUpward(Vector3 origin, float distance, Character scoutmasterCharacter, Character sourceCharacter, out RaycastHit groundHit)
		{
			groundHit = default;
			if (!IsFiniteVector(origin))
			{
				return false;
			}

			RaycastHit[] hits;
			try
			{
				hits = Physics.RaycastAll(origin, Vector3.up, distance, ~0, QueryTriggerInteraction.Ignore);
			}
			catch
			{
				return false;
			}

			// 取最近的一个有效地面（向上 raycast 命中的第一个就是 anchor 上方最近的地形底面）。
			Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
			foreach (RaycastHit hit in hits)
			{
				if (hit.collider == null || hit.collider.isTrigger)
				{
					continue;
				}
				// 向上 raycast 命中的是地形底面，法线朝下；跳过法线朝上（Dot > 0.2）的天花板。
				if (Vector3.Dot(hit.normal, Vector3.up) > 0.2f)
				{
					continue;
				}
				if (IsRestoreIgnoredCollider(hit.collider, scoutmasterCharacter, sourceCharacter))
				{
					continue;
				}

				// 命中地形底面后，再从该点上方做一次向下 raycast 找到地表顶面，
				// 否则把玩家恢复到 hit.point 会卡在地形内部（地形有厚度）。
				Vector3 topProbe = hit.point + Vector3.up * 12f;
				if (TryFindRestoreGround(topProbe, 30f, scoutmasterCharacter, sourceCharacter, float.PositiveInfinity, out RaycastHit topHit))
				{
					groundHit = topHit;
				}
				else
				{
					groundHit = hit;
				}
				return true;
			}

			return false;
		}

		private static bool TryFindRestoreGround(Vector3 origin, float distance, Character scoutmasterCharacter, Character sourceCharacter, float maxGroundY, out RaycastHit groundHit)
		{
			groundHit = default;
			if (!IsFiniteVector(origin))
			{
				return false;
			}

			RaycastHit[] hits;
			try
			{
				hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
			}
			catch
			{
				return false;
			}

			Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
			foreach (RaycastHit hit in hits)
			{
				if (hit.collider == null || hit.collider.isTrigger)
				{
					continue;
				}
				if (hit.point.y > maxGroundY)
				{
					continue;
				}
				// 阈值放宽到 -0.2（接受法线与 up 夹角 < ~101° 的表面）。
				// PEAK 地形（火山口、Alpine 陡崖）常出现坡度 > 78° 的可站立斜坡，
				// 旧阈值 0.2 会把它们当作"非地面"跳过，明明脚下有地面却进入虚空回退。
				if (Vector3.Dot(hit.normal, Vector3.up) < -0.2f)
				{
					continue;
				}
				if (IsRestoreIgnoredCollider(hit.collider, scoutmasterCharacter, sourceCharacter))
				{
					continue;
				}

				groundHit = hit;
				return true;
			}

			return false;
		}

		private static bool IsRestoreIgnoredCollider(Collider collider, Character scoutmasterCharacter, Character sourceCharacter)
		{
			if (collider == null)
			{
				return true;
			}

			UnityEngine.Transform colliderTransform = collider.transform;
			return IsColliderOwnedBy(colliderTransform, scoutmasterCharacter) || IsColliderOwnedBy(colliderTransform, sourceCharacter);
		}

		private static bool IsColliderOwnedBy(UnityEngine.Transform colliderTransform, Character character)
		{
			if (colliderTransform == null || character == null)
			{
				return false;
			}

			UnityEngine.Transform characterTransform = ((Component)character).transform;
			return colliderTransform == characterTransform || colliderTransform.IsChildOf(characterTransform);
		}

		private static bool IsFiniteVector(Vector3 value)
		{
			return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z);
		}

		private static bool IsFiniteFloat(float value)
		{
			return !float.IsNaN(value) && !float.IsInfinity(value);
		}

		private void PrepareScoutmaster()
		{
			ClearScoutmasterAiState(_scoutmaster);

			_scoutmasterCharacter.isScoutmaster = true;
			_scoutmasterCharacter.isZombie = false;
			_scoutmasterCharacter.data.isScoutmaster = true;
			SetCharacterDeadWithoutReconnect(_scoutmasterCharacter, false);
			_scoutmasterCharacter.data.zombified = false;
			_scoutmasterCharacter.data.passedOut = false;
			_scoutmasterCharacter.data.fullyPassedOut = false;
			_scoutmasterCharacter.data.fallSeconds = 0f;
			ApplyControlledScoutmasterRagdollBlend(_scoutmasterCharacter);
			_scoutmasterCharacter.data.currentStamina = 1f;
			_scoutmasterCharacter.data.extraStamina = 0f;
			CopySourceMovementRates();
			ClearAssistJumpState(_scoutmasterCharacter);
		}

		private void CopySourceMovementRates()
		{
			try
			{
				CharacterMovement scoutmasterMovement = _scoutmasterCharacter != null ? _scoutmasterCharacter.GetComponent<CharacterMovement>() : null;
				CharacterMovement sourceMovement = _sourceCharacter != null ? _sourceCharacter.GetComponent<CharacterMovement>() : null;
				if (scoutmasterMovement == null || sourceMovement == null)
				{
					return;
				}

				// The Scoutmaster body reads as a slide when it crawls at prefab defaults. Use the
				// player's locomotion tuning as the base and nudge movementForce up more aggressively so the
				// animation cadence matches visible displacement better.
				scoutmasterMovement.movementForce = Mathf.Max(scoutmasterMovement.movementForce, sourceMovement.movementForce * 1.45f);
				scoutmasterMovement.movementModifier = Mathf.Max(scoutmasterMovement.movementModifier, sourceMovement.movementModifier);
				scoutmasterMovement.sprintMultiplier = Mathf.Max(scoutmasterMovement.sprintMultiplier, sourceMovement.sprintMultiplier);
				scoutmasterMovement.movementTurnSpeed = Mathf.Max(scoutmasterMovement.movementTurnSpeed, sourceMovement.movementTurnSpeed);
				scoutmasterMovement.drag = Mathf.Max(scoutmasterMovement.drag, sourceMovement.drag);
			}
			catch { }
		}


		private void HideSourceCharacter()
		{
			if (_sourceCharacter == null)
			{
				return;
			}

			try
			{
				// 进入"藏匿点广播等待"：期间 Tick 不再把源角色拉回领队身边，
				// 让源角色稳定停在藏匿点，等 Photon 序列化把藏匿点位置广播给远端，
				// 使远端"最后同步位置" = 藏匿点（地下），彻底消除远端在变身位置看到源模型的问题。
				// 注意：游戏 Character.WarpPlayer 的 IL 里有 `if (IsLocal)` 守卫（见反编译），
				// WarpPlayerRPC 只对本地角色生效、对远端完全无效，不能依赖它移动远端看到的源角色。
				_sourceStashBroadcastPending = true;
				RegisterStashedSourceCharacter(_sourceCharacter);
				_sourceCharacter.data.fallSeconds = 0f;
				_sourceCharacter.data.passedOut = false;
				_sourceCharacter.data.fullyPassedOut = false;
				ClearAssistJumpState(_sourceCharacter);
				if (_sourceWasActive && !_sourceCharacter.gameObject.activeSelf)
				{
					_sourceCharacter.gameObject.SetActive(true);
				}
				HideSourceRenderers(_sourceCharacter.gameObject);
				SetCharacterPositionImmediate(_sourceCharacter, _sourceStashPosition, ((Component)_sourceCharacter).transform.rotation);
				SuspendSourcePhysics();
				// 保留对远端的传送 RPC（对远端实际无效，见上方注释），仅作兜底。
				SendSourceCharacterOutOfPlay(_sourceStashPosition);
				// FreezeSourceNetworkSync() 由 Enter 协程在等待 Photon 序列化藏匿点之后调用，
				// 确保远端最后收到的是藏匿点位置，而不是变身位置。
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to stash source character: " + ex.Message);
			}
		}

		private void RestoreSourceCharacter(Vector3 position, Quaternion rotation)
		{
			try
			{
				// 恢复路径保险：清掉"藏匿点广播等待"标志，避免异常路径残留导致 Tick 跳过跟随。
				_sourceStashBroadcastPending = false;
				if (_sourceWasActive && !_sourceCharacter.gameObject.activeSelf)
				{
					_sourceCharacter.gameObject.SetActive(true);
				}

				SetCharacterDeadWithoutReconnect(_sourceCharacter, false);
				_sourceCharacter.data.zombified = false;
				_sourceCharacter.isZombie = false;
				_sourceCharacter.data.passedOut = false;
				_sourceCharacter.data.fullyPassedOut = false;
				_sourceCharacter.data.fallSeconds = 0f;
				_sourceCharacter.data.deathTimer = 0f;
				_sourceCharacter.data.currentRagdollControll = 1f;
				_sourceCharacter.data.isGrounded = false;
				_sourceCharacter.data.sinceGrounded = 0f;
				_sourceCharacter.data.groundedFor = 0f;
				_sourceCharacter.data.groundPos = position;
				_sourceCharacter.data.avarageVelocity = Vector3.zero;
				_sourceCharacter.data.avarageLastFrameVelocity = Vector3.zero;
				ClearAssistJumpState(_sourceCharacter);

				SetCharacterPositionImmediate(_sourceCharacter, position, rotation);
				SanitizeCharacterBodyparts(_sourceCharacter, position, rotation);
				ResumeSourceNetworkSync();
				SendSourceCharacterRestore(position);
				SetCharacterPositionImmediate(_sourceCharacter, position, rotation);
				SanitizeCharacterBodyparts(_sourceCharacter, position, rotation);
				RestoreSourcePhysics();
				RestoreSourceControlState(_sourceCharacter);
				RestoreSourceRenderers(_sourceCharacter.gameObject);
				UnregisterStashedSourceCharacter(_sourceCharacter);
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to restore source character: " + ex.Message);
			}
		}

		private static void RestoreSourceControlState(Character character)
		{
			if (character == null)
			{
				return;
			}

			try
			{
				if (!character.gameObject.activeSelf)
				{
					character.gameObject.SetActive(true);
				}

				CharacterMovement movement = character.GetComponent<CharacterMovement>();
				if (movement != null && !movement.enabled)
				{
					movement.enabled = true;
				}

				CharacterInput inputComponent = character.GetComponent<CharacterInput>();
				if (inputComponent != null && !inputComponent.enabled)
				{
					inputComponent.enabled = true;
				}

				CharacterSyncer syncer = character.GetComponent<CharacterSyncer>();
				if (syncer != null && !syncer.enabled)
				{
					syncer.enabled = true;
				}

				PhotonView view = character.photonView;
				if (view != null && !view.enabled)
				{
					view.enabled = true;
				}

				if (character.input != null)
				{
					character.input.movementInput = Vector2.zero;
					character.input.lookInput = Vector2.zero;
					character.input.jumpWasPressed = false;
					character.input.jumpIsPressed = false;
					character.input.sprintIsPressed = false;
					character.input.sprintWasPressed = false;
					character.input.sprintToggleWasPressed = false;
					character.input.usePrimaryWasPressed = false;
					character.input.usePrimaryIsPressed = false;
					character.input.useSecondaryWasPressed = false;
					character.input.useSecondaryIsPressed = false;
					character.input.crouchWasPressed = false;
					character.input.crouchIsPressed = false;
					character.input.crouchToggleWasPressed = false;
					character.input.interactWasPressed = false;
					character.input.interactIsPressed = false;
					character.input.dropWasPressed = false;
					character.input.dropIsPressed = false;
				}

				if (character.data != null)
				{
					character.data.isSprinting = false;
					character.data.isCrouching = false;
					character.data.isJumping = false;
					character.data.isClimbing = false;
					character.data.isRopeClimbing = false;
					character.data.isVineClimbing = false;
					character.data.isReaching = false;
					character.data.worldMovementInput = Vector3.zero;
					character.data.worldMovementInput_Grounded = Vector3.zero;
				}
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to restore source control state: " + ex.Message);
			}
		}

		private void SendSourceCharacterOutOfPlay(Vector3 stashPosition)
		{
			SendSourceCharacterStateRpc(stashPosition, "[I'm Scoutmaster] Failed to move source character out of play: ");
		}

		// 联机时，本地每帧把源角色传送到领队身边（用于高度统计/胜利判定）。
		// PEAK 的位置同步由 CharacterSyncer(IPunObservable) 走 PhotonView 的
		// ObservedComponents 序列化通道，仅设置 view.enabled=false 拦不住它，
		// 远端会持续收到跟随位置，把藏匿点传送不断拉回地表（变身期间原模型可见）。
		// 因此必须：1) 关闭视图自动同步(Synchronization=Off)；2) 停用源角色上的
		// CharacterSyncer 组件，双保险切断位置广播；恢复时全部还原。
		private void FreezeSourceNetworkSync()
		{
			if (_sourceCharacter == null)
			{
				return;
			}

			try
			{
				PhotonView view = _sourceCharacter.photonView;
				if (view != null)
				{
					if (view.enabled)
					{
						view.enabled = false;
						_sourceViewDisabledByMod = true;
					}

					if (!_sourceViewSyncFrozenByMod)
					{
						_sourceViewSynchronizationBackup = view.Synchronization;
						_sourceViewSyncFrozenByMod = true;
					}
					view.Synchronization = ViewSynchronization.Off;
				}

				CharacterSyncer syncer = ((Component)_sourceCharacter).GetComponent<CharacterSyncer>();
				if (syncer != null && syncer.enabled)
				{
					syncer.enabled = false;
					_sourceSyncerDisabledByMod = true;
				}
			}
			catch { }
		}

		private void ResumeSourceNetworkSync()
		{
			if (!_sourceViewDisabledByMod && !_sourceViewSyncFrozenByMod && !_sourceSyncerDisabledByMod)
			{
				return;
			}

			if (_sourceCharacter == null)
			{
				return;
			}

			try
			{
				PhotonView view = _sourceCharacter.photonView;
				if (view != null)
				{
					if (_sourceViewDisabledByMod && !view.enabled)
					{
						view.enabled = true;
					}

					if (_sourceViewSyncFrozenByMod)
					{
						view.Synchronization = _sourceViewSynchronizationBackup;
						_sourceViewSyncFrozenByMod = false;
					}
				}

				if (_sourceSyncerDisabledByMod)
				{
					CharacterSyncer syncer = ((Component)_sourceCharacter).GetComponent<CharacterSyncer>();
					if (syncer != null && !syncer.enabled)
					{
						syncer.enabled = true;
					}
					_sourceSyncerDisabledByMod = false;
				}
				_sourceViewDisabledByMod = false;
			}
			catch { }
		}

		// 中途加入的玩家会按初始生成位置看到源角色，
		// 向其单独补发一次藏匿点传送，确保变身期间新玩家也看不到源模型。
		internal void PushSourceStashPositionToPlayer(Photon.Realtime.Player player)
		{
			if (player == null || _sourceCharacter == null || _sourceCharacter.photonView == null || (!PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode))
			{
				return;
			}
		
			try
			{
				_sourceCharacter.photonView.RPC("WarpPlayerRPC", player, _sourceStashPosition, false);
			}
			catch { }
		}

		private void SendSourceCharacterRestore(Vector3 restorePosition)
		{
			SendSourceCharacterStateRpc(restorePosition, "[I'm Scoutmaster] Failed to restore source character over network: ");
		}

		private void SendSourceCharacterStateRpc(Vector3 position, string warningPrefix)
		{
			if (_sourceCharacter == null || _sourceCharacter.photonView == null || (!PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode))
			{
				return;
			}

			try
			{
				PhotonView view = _sourceCharacter.photonView;
			view.RPC("RPCA_UnFall", RpcTarget.Others);
			view.RPC("ReviveCharacter", RpcTarget.Others, false);
			view.RPC("WarpPlayerRPC", RpcTarget.Others, position, false);
			}
			catch (Exception ex)
			{
				Log?.LogWarning(warningPrefix + ex.Message);
			}
		}

		private void SuspendSourcePhysics()
		{
			if (_sourceCharacter == null || _sourceCharacter.refs?.ragdoll?.partList == null)
			{
				return;
			}

			try
			{
				if (!_sourcePhysicsSuspended)
				{
					_sourceBodypartPhysicsStates.Clear();
				}

				foreach (Bodypart part in _sourceCharacter.refs.ragdoll.partList)
				{
					Rigidbody rig = part != null ? part.Rig : null;
					if (rig == null)
					{
						continue;
					}

					if (!_sourcePhysicsSuspended)
					{
						_sourceBodypartPhysicsStates.Add(new BodypartPhysicsState(rig));
					}

					if (!rig.isKinematic)
					{
						rig.linearVelocity = Vector3.zero;
						rig.angularVelocity = Vector3.zero;
					}
					rig.detectCollisions = false;
					rig.useGravity = false;
					rig.isKinematic = true;
				}

				_sourcePhysicsSuspended = true;
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to suspend source physics: " + ex.Message);
			}
		}

		private void RestoreSourcePhysics()
		{
			if (!_sourcePhysicsSuspended)
			{
				return;
			}

			try
			{
				foreach (BodypartPhysicsState state in _sourceBodypartPhysicsStates)
				{
					Rigidbody rig = state.Rig;
					if (rig == null)
					{
						continue;
					}

					rig.isKinematic = state.IsKinematic;
					if (!rig.isKinematic)
					{
						rig.linearVelocity = Vector3.zero;
						rig.angularVelocity = Vector3.zero;
					}
					rig.detectCollisions = state.DetectCollisions;
					rig.useGravity = state.UseGravity;
				}
			}
			catch (Exception ex)
			{
				Log?.LogWarning("[I'm Scoutmaster] Failed to restore source physics: " + ex.Message);
			}
			finally
			{
				_sourceBodypartPhysicsStates.Clear();
				_sourcePhysicsSuspended = false;
			}
		}

		private void KeepScoutmasterAwake()
		{
			if (_scoutmasterCharacter?.data == null)
			{
				return;
			}

			ClearScoutmasterAiState(_scoutmaster);
			SetCharacterDeadWithoutReconnect(_scoutmasterCharacter, false);
			_scoutmasterCharacter.isScoutmaster = true;
			_scoutmasterCharacter.isZombie = false;
			_scoutmasterCharacter.data.isScoutmaster = true;
			_scoutmasterCharacter.data.zombified = false;

			if (IsControlledScoutmasterIncapacitated(_scoutmasterCharacter))
			{
				return;
			}

			_scoutmasterCharacter.data.passedOut = false;
			_scoutmasterCharacter.data.fullyPassedOut = false;
			_scoutmasterCharacter.data.fallSeconds = 0f;
			ApplyControlledScoutmasterRagdollBlend(_scoutmasterCharacter);
			_scoutmasterCharacter.data.extraStamina = 0f;
			ClearAssistJumpState(_scoutmasterCharacter);
		}

		private void KeepSourceOutOfPlay()
		{
			if (_sourceCharacter == null || _sourceCharacter.data == null)
			{
				return;
			}
			if (_sourceCharacter == Character.localCharacter && _scoutmasterCharacter != null)
			{
				Character.localCharacter = _scoutmasterCharacter;
			}

			SetCharacterDeadWithoutReconnect(_sourceCharacter, false);
			_sourceCharacter.data.zombified = false;
			_sourceCharacter.data.passedOut = false;
			_sourceCharacter.data.fullyPassedOut = false;
			_sourceCharacter.data.fallSeconds = 0f;
			ClearAssistJumpState(_sourceCharacter);
			HideSourceRenderers(_sourceCharacter.gameObject);
			SuspendSourcePhysics();
		}
	}

	private static void SetCharacterPositionImmediate(Character character, Vector3 position, Quaternion rotation)
	{
		if (character == null)
		{
			return;
		}

		try
		{
			UnityEngine.Transform characterTransform = ((Component)character).transform;
			Vector3 oldCenter = character.Center;
			Quaternion oldRotation = characterTransform.rotation;
			// Degenerate (NaN) source state must never feed into the delta math:
			// rotationDelta * (NaN - oldCenter) poisons every ragdoll part and is
			// then read by the game camera every frame (frustum errors/black screen).
			bool oldCenterValid = IsFiniteVector(oldCenter);
			bool oldRotationValid = IsFiniteQuaternion(oldRotation);
			Vector3 delta = oldCenterValid ? position - oldCenter : Vector3.zero;
			Quaternion rotationDelta = oldRotationValid ? rotation * Quaternion.Inverse(oldRotation) : Quaternion.identity;
			Vector3 oldRootPosition = characterTransform.position;
			characterTransform.SetPositionAndRotation(
				IsFiniteVector(oldRootPosition) ? oldRootPosition + delta : position,
				rotation);
			if (character.refs?.ragdoll?.partList != null)
			{
				foreach (Bodypart part in character.refs.ragdoll.partList)
				{
					if (part == null)
					{
						continue;
					}
					if (part.Rig != null)
					{
						Vector3 oldPartPosition = part.Rig.position;
						Quaternion oldPartRotation = part.Rig.rotation;
						if (oldCenterValid && oldRotationValid && IsFiniteVector(oldPartPosition) && IsFiniteQuaternion(oldPartRotation))
						{
							part.Rig.position = position + rotationDelta * (oldPartPosition - oldCenter);
							part.Rig.rotation = rotationDelta * oldPartRotation;
						}
						else
						{
							part.Rig.position = position;
							part.Rig.rotation = rotation;
						}
						if (!part.Rig.isKinematic)
						{
							part.Rig.linearVelocity = Vector3.zero;
							part.Rig.angularVelocity = Vector3.zero;
						}
					}
					else
					{
						Vector3 oldPartPosition = part.transform.position;
						Quaternion oldPartRotation = part.transform.rotation;
						if (oldCenterValid && oldRotationValid && IsFiniteVector(oldPartPosition) && IsFiniteQuaternion(oldPartRotation))
						{
							part.transform.SetPositionAndRotation(
								position + rotationDelta * (oldPartPosition - oldCenter),
								rotationDelta * oldPartRotation);
						}
						else
						{
							part.transform.SetPositionAndRotation(position, rotation);
						}
					}
				}
			}
		}
		catch
		{
			((Component)character).transform.SetPositionAndRotation(position, rotation);
		}
	}

	private static bool IsImScoutmasterPrefabPoolInChain(IPunPrefabPool pool)
	{
		HashSet<object> visited = new HashSet<object>();
		while (pool != null && visited.Add(pool))
		{
			if (pool is ImScoutmasterPrefabPool)
			{
				return true;
			}

			PropertyInfo innerProp = pool.GetType().GetProperty("Inner", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (innerProp == null)
			{
				break;
			}

			pool = innerProp.GetValue(pool) as IPunPrefabPool;
		}

		return false;
	}

	private static void EnsureScoutmasterPrefabPoolWrapper()
	{
		if (Time.unscaledTime < _nextPrefabPoolWrapperEnsureTime)
		{
			return;
		}

		_nextPrefabPoolWrapperEnsureTime = Time.unscaledTime + PrefabPoolWrapperRetryIntervalSeconds;

		try
		{
			IPunPrefabPool currentPool = PhotonNetwork.PrefabPool;
			if (IsImScoutmasterPrefabPoolInChain(currentPool))
			{
				return;
			}

			_prefabPoolWrapper = new ImScoutmasterPrefabPool(currentPool);
			PhotonNetwork.PrefabPool = _prefabPoolWrapper;
			Log?.LogInfo("[I'm Scoutmaster] Installed ImScoutmaster prefab pool wrapper around " + (currentPool != null ? currentPool.GetType().FullName : "null") + ".");
		}
		catch { }
	}

	private static void WarmScoutmasterPrefabCache()
	{
		if (Time.unscaledTime < _nextScoutmasterPrefabWarmTime)
		{
			return;
		}

		_nextScoutmasterPrefabWarmTime = Time.unscaledTime + ScoutmasterPrefabWarmIntervalSeconds;

		try
		{
			// 预热：只要场景中出现过领队，就立即缓存预制体，
			// 确保玩家按键变身时不需要临时解析。
			ImScoutmasterPrefabPool.ResolveScoutmasterPrefab();
		}
		catch
		{
		}
	}

	private sealed class ImScoutmasterPrefabPool : IPunPrefabPool
	{
		private readonly IPunPrefabPool _inner;
		private static GameObject _cachedScoutmasterPrefab;
		private static GameObject _runtimePrefabRoot;
		private static bool _loggedMissingScoutmasterPrefab;
		// 小退（回到大厅/离开房间重进）后，"Character" 预制体资源可能无法再加载
		// （Resources.Load 返回 null，PEAKLib/ImZombie 的兜底也失效），导致角色永远
		// 无法生成、卡在大厅。首次成功生成角色时克隆一份 DontDestroyOnLoad 的备份，
		// 小退后用它实例化，彻底绕开失效的加载链。
		private static GameObject _cachedCharacterPrefab;
		private static GameObject _runtimeCharacterPrefabRoot;
		private static bool _loggedMissingCharacterPrefab;

		public ImScoutmasterPrefabPool(IPunPrefabPool inner)
		{
			_inner = inner;
		}

		public IPunPrefabPool Inner => _inner;

		// 小退后 Photon DefaultPool.ResourceCache 里可能残留已销毁的预制体引用（Unity fake null）。
		// 从 PhotonNetwork.PrefabPool 链头开始，递归检查链上每个池对象及其字段，
		// 找到所有 DefaultPool 实例并清除缓存中的失效条目，让 DefaultPool 下次重新加载。
		private static bool PruneStaleDefaultPoolResourceCache()
		{
			bool pruned = false;
			try
			{
				Type defaultPoolType = typeof(Photon.Pun.DefaultPool);
				FieldInfo resourceCacheField = defaultPoolType.GetField("ResourceCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (resourceCacheField == null)
				{
					return false;
				}

				HashSet<object> visited = new HashSet<object>();
				Queue<object> pending = new Queue<object>();
				IPunPrefabPool head = PhotonNetwork.PrefabPool;
				if (head != null)
				{
					pending.Enqueue(head);
				}

				while (pending.Count > 0)
				{
					object candidate = pending.Dequeue();
					if (candidate == null || !visited.Add(candidate))
					{
						continue;
					}

					if (defaultPoolType.IsInstanceOfType(candidate))
					{
						pruned |= PruneSingleDefaultPoolCache(resourceCacheField, candidate);
					}

					Type candidateType = candidate.GetType();
					// 遍历字段：查找 DefaultPool 实例或嵌套的 IPunPrefabPool（如 CustomPrefabPool 内部持有的 DefaultPool）。
					FieldInfo[] fields = candidateType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					foreach (FieldInfo field in fields)
					{
						object fieldValue = null;
						try
						{
							fieldValue = field.GetValue(candidate);
						}
						catch
						{
						}
						if (fieldValue == null)
						{
							continue;
						}
						if (defaultPoolType.IsInstanceOfType(fieldValue))
						{
							pruned |= PruneSingleDefaultPoolCache(resourceCacheField, fieldValue);
						}
						else if (fieldValue is IPunPrefabPool innerPool)
						{
							pending.Enqueue(innerPool);
						}
					}

					// 遍历 Inner 属性链。
					PropertyInfo innerProperty = candidateType.GetProperty("Inner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (innerProperty != null)
					{
						try
						{
							if (innerProperty.GetValue(candidate) is IPunPrefabPool innerPool)
							{
								pending.Enqueue(innerPool);
							}
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
			return pruned;
		}

		private static bool PruneSingleDefaultPoolCache(FieldInfo resourceCacheField, object defaultPoolInstance)
		{
			try
			{
				if (resourceCacheField.GetValue(defaultPoolInstance) is System.Collections.IDictionary cache)
				{
					List<object> staleKeys = new List<object>();
					foreach (System.Collections.DictionaryEntry entry in cache)
					{
						// Unity 的 destroyed 对象对 == null 判定为真（fake null），此处统一清理。
						if (entry.Value is UnityEngine.Object unityObject && unityObject == null)
						{
							staleKeys.Add(entry.Key);
						}
					}
					foreach (object key in staleKeys)
					{
						cache.Remove(key);
					}
					return staleKeys.Count > 0;
				}
			}
			catch
			{
			}
			return false;
		}

		public GameObject Instantiate(string prefabId, Vector3 position, Quaternion rotation)
		{
			bool isCharacter = string.Equals(prefabId, CharacterResourceName, StringComparison.Ordinal);
			bool isScoutmaster = string.Equals(prefabId, ScoutmasterResourceName, StringComparison.Ordinal);
			if (!isCharacter && !isScoutmaster)
			{
				try
				{
					return _inner?.Instantiate(prefabId, position, rotation);
				}
				catch (Exception ex)
				{
					// 小退（回到大厅/离开房间重进）后，Photon DefaultPool 的 ResourceCache
					// 可能仍持有已被 Unity 销毁的预制体引用（fake null），导致
					// Character 生成抛 NullReferenceException、永远无法进入大厅。
					// 清理失效缓存后重试一次；仍失败则原样抛出。
					if (PruneStaleDefaultPoolResourceCache())
					{
						Log?.LogWarning("[I'm Scoutmaster] Pruned stale Photon prefab cache for '" + prefabId + "' after instantiate failure (" + ex.Message + "); retrying.");
						return _inner?.Instantiate(prefabId, position, rotation);
					}
					throw;
				}
			}

			// PEAKLib and other prefab pools may own prefabs that are not in Resources.
			// Preserve that lookup before using the runtime fallback below.
			GameObject innerInstance = null;
			try
			{
				innerInstance = _inner?.Instantiate(prefabId, position, rotation);
			}
			catch
			{
				if (PruneStaleDefaultPoolResourceCache())
				{
					try
					{
						innerInstance = _inner?.Instantiate(prefabId, position, rotation);
					}
					catch { }
				}
			}
			if (innerInstance != null)
			{
				if (isCharacter)
				{
					CacheCharacterPrefabBackup(innerInstance, "successful Character instantiation");
				}
				else
				{
					CacheScoutmasterPrefabBackup(innerInstance, "successful Scoutmaster instantiation");
				}
				return innerInstance;
			}

			if (isCharacter)
			{
				// 小退后 Character 资源失效的兜底：用 DontDestroyOnLoad 的运行时备份实例化。
				GameObject backupInstance = TryInstantiateCachedCharacterPrefab(position, rotation);
				if (backupInstance != null)
				{
					return backupInstance;
				}
				if (!_loggedMissingCharacterPrefab)
				{
					_loggedMissingCharacterPrefab = true;
					Log?.LogWarning("[I'm Scoutmaster] Could not instantiate Character from any pool, Resources, or cached runtime backup.");
				}
				return null;
			}

			GameObject prefab = ResolveScoutmasterPrefab();
			if (prefab == null)
			{
				if (!_loggedMissingScoutmasterPrefab)
				{
					_loggedMissingScoutmasterPrefab = true;
					Log?.LogWarning("[I'm Scoutmaster] Could not resolve Character_Scoutmaster from the active prefab pool, Resources, or loaded Scoutmaster objects.");
				}
				return null;
			}

			return InstantiatePrefabAsRuntimeObject(prefab, position, rotation);
		}

		internal static GameObject ResolveScoutmasterPrefab()
		{
			if (_cachedScoutmasterPrefab != null)
			{
				return _cachedScoutmasterPrefab;
			}

			_cachedScoutmasterPrefab = Resources.Load<GameObject>(ScoutmasterResourceName);
			if (_cachedScoutmasterPrefab != null)
			{
				return _cachedScoutmasterPrefab;
			}

			try
			{
				Scoutmaster source = FindLoadedScoutmasterPrefabSource();
				if (source == null)
				{
					return null;
				}

				CacheScoutmasterPrefabBackup(source.gameObject, "loaded Scoutmaster object");
				return _cachedScoutmasterPrefab;
			}
			catch
			{
				return null;
			}
		}

		internal static void CacheScoutmasterPrefabBackup(GameObject sourceObject, string reason)
		{
			if (_cachedScoutmasterPrefab != null || sourceObject == null)
			{
				return;
			}

			try
			{
				if (IsRuntimeScoutmasterPrefabBackup(sourceObject))
				{
					return;
				}

				Scoutmaster scoutmaster = sourceObject.GetComponent<Scoutmaster>();
				Character character = sourceObject.GetComponent<Character>();
				PhotonView view = sourceObject.GetComponent<PhotonView>();
				if (scoutmaster == null || character == null || view == null)
				{
					return;
				}

				if (_runtimePrefabRoot == null)
				{
					_runtimePrefabRoot = new GameObject("ImScoutmaster_RuntimePrefabRoot");
					_runtimePrefabRoot.SetActive(false);
					Object.DontDestroyOnLoad(_runtimePrefabRoot);
				}

				_cachedScoutmasterPrefab = Object.Instantiate(sourceObject, _runtimePrefabRoot.transform);
				_cachedScoutmasterPrefab.name = ScoutmasterResourceName;
				_cachedScoutmasterPrefab.SetActive(false);
				ResetPhotonViewsForPrefabBackup(_cachedScoutmasterPrefab);
				_loggedMissingScoutmasterPrefab = false;
				Log?.LogInfo("[I'm Scoutmaster] Registered runtime network prefab backup for Character_Scoutmaster from " + reason + ".");
			}
			catch
			{
				_cachedScoutmasterPrefab = null;
			}
		}

		private static Scoutmaster FindLoadedScoutmasterPrefabSource()
		{
			try
			{
				System.Collections.IEnumerable allScoutmasters = ScoutmasterAllScoutmastersField?.GetValue(null) as System.Collections.IEnumerable;
				if (allScoutmasters != null)
				{
					foreach (object entry in allScoutmasters)
					{
						Scoutmaster candidate = entry as Scoutmaster;
						if (IsUsableScoutmasterPrefabSource(candidate, exactName: true))
						{
							return candidate;
						}
					}
					foreach (object entry in allScoutmasters)
					{
						Scoutmaster candidate = entry as Scoutmaster;
						if (IsUsableScoutmasterPrefabSource(candidate, exactName: false))
						{
							return candidate;
						}
					}
				}
			}
			catch
			{
			}

			Scoutmaster[] loadedScoutmasters = Resources.FindObjectsOfTypeAll<Scoutmaster>();
			foreach (Scoutmaster candidate in loadedScoutmasters)
			{
				if (IsUsableScoutmasterPrefabSource(candidate, exactName: true))
				{
					return candidate;
				}
			}
			foreach (Scoutmaster candidate in loadedScoutmasters)
			{
				if (IsUsableScoutmasterPrefabSource(candidate, exactName: false))
				{
					return candidate;
				}
			}

			// 最后手段：直接按名称扫描已加载的 GameObject（含未激活的预制体资产），
			// 覆盖组件扫描未命中的情况。
			try
			{
				GameObject[] loadedObjects = Resources.FindObjectsOfTypeAll<GameObject>();
				foreach (GameObject candidate in loadedObjects)
				{
					if (candidate == null || IsRuntimeScoutmasterPrefabBackup(candidate))
					{
						continue;
					}
					if (!string.Equals(candidate.name, ScoutmasterResourceName, StringComparison.Ordinal))
					{
						continue;
					}
					Scoutmaster scoutmaster = candidate.GetComponent<Scoutmaster>();
					if (scoutmaster != null && candidate.GetComponent<Character>() != null && candidate.GetComponent<PhotonView>() != null)
					{
						return scoutmaster;
					}
				}
			}
			catch
			{
			}

			return null;
		}

		private static bool IsUsableScoutmasterPrefabSource(Scoutmaster candidate, bool exactName)
		{
			if (candidate == null || candidate.gameObject == null || IsRuntimeScoutmasterPrefabBackup(candidate.gameObject))
			{
				return false;
			}
			if (exactName && !string.Equals(candidate.gameObject.name, ScoutmasterResourceName, StringComparison.Ordinal) && !string.Equals(candidate.gameObject.name, ScoutmasterResourceName + "(Clone)", StringComparison.Ordinal))
			{
				return false;
			}
			return candidate.GetComponent<Character>() != null && candidate.GetComponent<PhotonView>() != null;
		}

		private static bool IsRuntimeScoutmasterPrefabBackup(GameObject obj)
		{
			if (obj == null)
			{
				return false;
			}
			if (_cachedScoutmasterPrefab != null && obj == _cachedScoutmasterPrefab)
			{
				return true;
			}
			UnityEngine.Transform transform = obj.transform;
			return _runtimePrefabRoot != null && transform != null && (transform == _runtimePrefabRoot.transform || transform.IsChildOf(_runtimePrefabRoot.transform));
		}

		private static void ResetPhotonViewsForPrefabBackup(GameObject backup)
		{
			PhotonView[] views = backup.GetComponentsInChildren<PhotonView>(true);
			for (int i = 0; i < views.Length; i++)
			{
				views[i].ViewID = 0;
				views[i].sceneViewId = 0;
			}
		}

		// 首次成功生成角色时，把实例克隆为 DontDestroyOnLoad 的预制体备份。
		// 小退后游戏自身的 Character 资源无法重新加载时，用该备份兜底生成角色。
		private static void CacheCharacterPrefabBackup(GameObject sourceInstance, string reason)
		{
			if (_cachedCharacterPrefab != null || sourceInstance == null)
			{
				return;
			}

			try
			{
				if (IsRuntimeCharacterPrefabBackup(sourceInstance))
				{
					return;
				}
				if (sourceInstance.GetComponent<Character>() == null)
				{
					return;
				}

				if (_runtimeCharacterPrefabRoot == null)
				{
					_runtimeCharacterPrefabRoot = new GameObject("ImScoutmaster_RuntimeCharacterPrefabRoot");
					_runtimeCharacterPrefabRoot.SetActive(false);
					Object.DontDestroyOnLoad(_runtimeCharacterPrefabRoot);
				}

				_cachedCharacterPrefab = Object.Instantiate(sourceInstance, _runtimeCharacterPrefabRoot.transform);
				_cachedCharacterPrefab.name = CharacterResourceName;
				_cachedCharacterPrefab.SetActive(false);
				ResetPhotonViewsForPrefabBackup(_cachedCharacterPrefab);
				_loggedMissingCharacterPrefab = false;
				Log?.LogInfo("[I'm Scoutmaster] Cached runtime Character prefab backup from " + reason + ".");
			}
			catch
			{
				_cachedCharacterPrefab = null;
			}
		}

		private static bool IsRuntimeCharacterPrefabBackup(GameObject obj)
		{
			if (obj == null)
			{
				return false;
			}
			if (_cachedCharacterPrefab != null && obj == _cachedCharacterPrefab)
			{
				return true;
			}
			UnityEngine.Transform transform = obj.transform;
			return _runtimeCharacterPrefabRoot != null && transform != null
				&& (transform == _runtimeCharacterPrefabRoot.transform || transform.IsChildOf(_runtimeCharacterPrefabRoot.transform));
		}

		// Character 生成兜底：依次尝试运行时备份、Resources 直载、Photon 池链提取、场景残留对象。
		internal static bool IsRuntimePrefabBackupObject(GameObject obj)
		{
			return IsRuntimeScoutmasterPrefabBackup(obj) || IsRuntimeCharacterPrefabBackup(obj);
		}

		private static GameObject TryInstantiateCachedCharacterPrefab(Vector3 position, Quaternion rotation)
		{
			if (_cachedCharacterPrefab != null)
			{
				GameObject instance = InstantiatePrefabAsRuntimeObject(_cachedCharacterPrefab, position, rotation);
				if (instance != null)
				{
					Log?.LogWarning("[I'm Scoutmaster] Instantiated Character from cached runtime backup (small-exit recovery).");
					return instance;
				}
			}

			GameObject resourcePrefab = Resources.Load<GameObject>(CharacterResourceName);
			if (resourcePrefab != null)
			{
				CacheCharacterPrefabBackup(resourcePrefab, "Resources");
				return InstantiatePrefabAsRuntimeObject(resourcePrefab, position, rotation);
			}

			if (TryCaptureCharacterPrefabFromChain(out GameObject chainPrefab))
			{
				CacheCharacterPrefabBackup(chainPrefab, "prefab pool chain");
				return InstantiatePrefabAsRuntimeObject(chainPrefab, position, rotation);
			}

			return null;
		}

		// 从 Photon PrefabPool 链（含字段递归）提取 "Character" 预制体：
		// 覆盖 PEAKLib CustomPrefabPool.idToGameObject、DefaultPool.ResourceCache、
		// 以及链上其他池的 Dictionary<string, GameObject> 缓存。
		private static bool TryCaptureCharacterPrefabFromChain(out GameObject prefab)
		{
			prefab = null;
			try
			{
				HashSet<object> visited = new HashSet<object>();
				Queue<object> pending = new Queue<object>();
				IPunPrefabPool head = PhotonNetwork.PrefabPool;
				if (head != null)
				{
					pending.Enqueue(head);
				}

				while (pending.Count > 0)
				{
					object candidate = pending.Dequeue();
					if (candidate == null || !visited.Add(candidate))
					{
						continue;
					}

					Type candidateType = candidate.GetType();
					BindingFlags scanFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
					foreach (FieldInfo field in candidateType.GetFields(scanFlags))
					{
						object fieldValue = null;
						try
						{
							fieldValue = field.GetValue(field.IsStatic ? null : candidate);
						}
						catch
						{
						}
						if (TryUseCharacterPrefabPoolValue(fieldValue, pending, out prefab))
						{
							return true;
						}
					}

					foreach (PropertyInfo property in candidateType.GetProperties(scanFlags))
					{
						if (property.GetIndexParameters().Length > 0 || !property.CanRead)
						{
							continue;
						}
						object propertyValue = null;
						try
						{
							MethodInfo getter = property.GetGetMethod(nonPublic: true);
							propertyValue = property.GetValue(getter != null && getter.IsStatic ? null : candidate);
						}
						catch
						{
						}
						if (TryUseCharacterPrefabPoolValue(propertyValue, pending, out prefab))
						{
							return true;
						}
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private static bool TryUseCharacterPrefabPoolValue(object value, Queue<object> pendingPools, out GameObject prefab)
		{
			prefab = null;
			if (value == null)
			{
				return false;
			}

			if (value is Dictionary<string, GameObject> typedMap
				&& typedMap.TryGetValue(CharacterResourceName, out GameObject typedPrefab)
				&& IsUsableCharacterPrefabCandidate(typedPrefab))
			{
				prefab = typedPrefab;
				return true;
			}

			if (value is System.Collections.IDictionary dictionary
				&& dictionary.Contains(CharacterResourceName)
				&& IsUsableCharacterPrefabCandidate(dictionary[CharacterResourceName] as GameObject))
			{
				prefab = dictionary[CharacterResourceName] as GameObject;
				return true;
			}

			if (value is GameObject gameObject && IsUsableCharacterPrefabCandidate(gameObject))
			{
				prefab = gameObject;
				return true;
			}

			if (value is IPunPrefabPool innerPool)
			{
				pendingPools.Enqueue(innerPool);
			}

			return false;
		}

		private static bool IsUsableCharacterPrefabCandidate(GameObject candidate)
		{
			if (candidate == null || candidate.GetComponent<Character>() == null)
			{
				return false;
			}
			if (IsRuntimeCharacterPrefabBackup(candidate))
			{
				return true;
			}

			PhotonView view = candidate.GetComponent<PhotonView>();
			bool hasRuntimeViewId = view != null && view.ViewID > 0;
			bool likelyAssetPrefab = !candidate.scene.IsValid();
			bool looksLikeBackup = candidate.name != null && candidate.name.IndexOf("Backup", StringComparison.OrdinalIgnoreCase) >= 0;
			bool inactiveCleanRuntimePrefab = !candidate.activeInHierarchy && !hasRuntimeViewId;
			return likelyAssetPrefab || looksLikeBackup || inactiveCleanRuntimePrefab;
		}

		private static GameObject InstantiatePrefabAsRuntimeObject(GameObject prefab, Vector3 position, Quaternion rotation)
		{
			if (prefab == null)
			{
				return null;
			}

			bool wasActive = prefab.activeSelf;
			if (wasActive)
			{
				prefab.SetActive(false);
			}
			GameObject instance = Object.Instantiate(prefab, position, rotation);
			if (wasActive)
			{
				prefab.SetActive(true);
			}
			return instance;
		}

		public void Destroy(GameObject gameObject)
		{
			_inner?.Destroy(gameObject);
		}
	}
}


