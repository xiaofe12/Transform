using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ImCritter;

/// <summary>Which vanilla critter the player is transformed into.</summary>
internal enum CritterKind
{
	Frog,
	Beetle,
	Scorpion,
	Coconut,
	Bomb,
	Cactus
}

/// <summary>
/// Runtime controller added to the local player's Character while they are a critter
/// (frog / beetle / scorpion MobItem items, plus the coconut Item, spawned over Photon so every
/// client, including unmodded ones, sees the living critter).
///
/// The critter's vanilla movement AI is suppressed on modded clients by Harmony prefixes on
/// Mob.Update and FrogTongue.CheckAllCharacters (gated on the registry below); the owner then
/// drives the Mob's own Rigidbody with WASD forces — the proven physics-form recipe
/// shared with the tumbleweed and statue forms. FrogTongue.FixedUpdate/LateUpdate keep
/// running everywhere so the tongue pull force and tongue visual replicate through the
/// vanilla RPCs to every client (including unmodded ones). Item interactions are blocked
/// (Item.blockInteraction) so nobody can pick up the transformed player.
///
/// The player's ragdoll is switched to kinematic no-clip (ghost/tumbleweed recipe) and
/// the character root follows the critter every frame; the network broadcast of the
/// player's position is redirected 30m straight down (see CritterHarmonyPatches) so
/// remote clients never see a body inside the critter — the "buried body" approach that
/// proved most stable across the five source mods.
///
/// Controls (aligned with the other physics forms):
///  - WASD: move (camera-relative);
///  - Shift: sprint (drains the shared stamina bar, zombie recipe);
///  - Space: hop — the FROG leaps toward the control direction using direct launch
///    speeds (MaxSpeed = distance, JumpSpeed = height);
///  - RMB: attack. Frog shoots its tongue at the crosshair target via the vanilla
///    RPC-synced FrogTongue.Attack; beetle charges and bonks (RPCA_Fall + physics
///    impulse, vanilla bonkForce/bonkForceUp values); scorpion stings (RPCA_Fall +
///    the vanilla poison affliction); coconut charges and slams toward the aim point.
/// </summary>
[DefaultExecutionOrder(600)]
public sealed class CritterController : MonoBehaviour
{
	private const float CameraLookAhead = 2.5f;
	private const float CameraSmoothTime = 0.08f;
	private const float CameraRotationSharpness = 12f;
	private const float DefaultCameraDistance = 6f;
	private const float DefaultCameraHeight = 2f;
	private const float DefaultCameraFov = 78f;
	private const float JumpCooldown = 0.3f;
	private const float GroundCheckExtra = 0.4f;
	private const float VoidExitHeight = -80f;
	private const float JumpStaminaCost = 0.08f;
	private const float MinSprintStamina = 0.02f;
	private const float AttackRange = 4f;
	private const float FrogTongueMinRange = 14f;
	private const float FrogTongueAimRadius = 3.4f;
	private const float FrogGroundedVelocityEpsilon = 0.08f;
	private const float AttackInputBufferSeconds = 0.2f;
	private const float FrogTongueReadyLength = 0.08f;
	private const float FrogEmptyLickSeconds = 0.35f;
	private const float BeetleMoveAcceleration = 18f;
	private const float ScorpionMoveAcceleration = 12f;
	private const float ItemRollTorque = 16f;
	private const float CoconutMaxChargeSeconds = 1.25f;
	private const float CoconutMinSlamSpeed = 10f;
	private const float CoconutMaxSlamSpeed = 28f;
	private const float CoconutSlamRange = 45f;
	private const float CoconutSlamUpBias = 0.16f;
	private const float CactusLaunchRange = 32f;
	private const float CactusLaunchSpeed = 24f;
	private const float CactusLaunchUpBias = 0.08f;
	private const float BreakRestoreItemRestoreDelaySeconds = 1f;
	private const float ControlledLinearDamping = 0.05f;
	private const float ControlledAngularDamping = 8f;
	private const float MovementInputDeadzone = 0.01f;
	private const float FrogNormalHorizontalLaunchScale = 0.55f;
	private const float ExitGroundMinNormalY = 0.45f;
	private const float ExitCapsuleRadius = 0.45f;
	private const float ExitCapsuleBottom = 0.15f;
	private const float ExitCapsuleTop = 1.85f;
	private const float ExitLocalProbeUp = 2.5f;
	private const float ExitLocalProbeDown = 8f;
	private const float ExitBroadProbeHeight = 12f;
	private const float ExitBroadProbeDepth = 28f;
	private const float ExitMaxSurfaceRise = 4f;
	/// <summary>Landing grace window: after the last confirmed ground contact, a hop pressed
	/// within this many seconds still fires (covers the landing-bounce frames where a strict
	/// grounded check flickers).</summary>
	private const float GroundedJumpBufferSeconds = 0.3f;
	/// <summary>Space is sampled in Update and consumed by FixedUpdate. Reading GetKeyDown
	/// directly in FixedUpdate can miss short taps when the render frame and physics frame do
	/// not line up, which made frog jumping feel completely dead.</summary>
	private const float FrogJumpInputBufferSeconds = 0.25f;

	private static readonly FieldInfo FrogTargetCharacterField = AccessTools.Field(typeof(FrogTongue), "_targetCharacter");
	private static readonly FieldInfo FrogTongueLengthField = AccessTools.Field(typeof(FrogTongue), "_tongueLength");
	private static readonly FieldInfo FrogIsPullingField = AccessTools.Field(typeof(FrogTongue), "_isPulling");
	private static readonly FieldInfo BreakableAlreadyBrokeField = AccessTools.Field(typeof(Breakable), "alreadyBroke");
	private static readonly FieldInfo ItemLastThrownCharacterField = AccessTools.Field(typeof(Item), "lastThrownCharacter");
	private static readonly FieldInfo ItemLastThrownTimeField = AccessTools.Field(typeof(Item), "lastThrownTime");
	private static readonly MethodInfo ItemForceSyncForFramesMethod = AccessTools.Method(typeof(Item), "ForceSyncForFrames");
	private static readonly Dictionary<Type, bool> HasSetKinematicRpcByComponentType = new Dictionary<Type, bool>();
	private static readonly Vector3[] ExitSearchOffsets =
	{
		Vector3.zero,
		new Vector3(0.8f, 0f, 0f),
		new Vector3(-0.8f, 0f, 0f),
		new Vector3(0f, 0f, 0.8f),
		new Vector3(0f, 0f, -0.8f),
		new Vector3(1.6f, 0f, 0f),
		new Vector3(-1.6f, 0f, 0f),
		new Vector3(0f, 0f, 1.6f),
		new Vector3(0f, 0f, -1.6f),
		new Vector3(1.2f, 0f, 1.2f),
		new Vector3(1.2f, 0f, -1.2f),
		new Vector3(-1.2f, 0f, 1.2f),
		new Vector3(-1.2f, 0f, -1.2f),
		new Vector3(2.6f, 0f, 0f),
		new Vector3(-2.6f, 0f, 0f),
		new Vector3(0f, 0f, 2.6f),
		new Vector3(0f, 0f, -2.6f)
	};

	/// <summary>How often the unmodded-room fallback re-pins the critter on remote clients.
	/// Same cadence as the reference FrogSkill proxy sync (0.08s); the vanilla master-client
	/// AI timer (~1s+) and Mob state changes are far slower, so 10Hz is plenty.</summary>
	private const float UnmoddedSyncInterval = 0.1f;

	/// <summary>How often the owner checks the networked critter is still alive, ours and on
	/// the ground (an unmodded master can still run the vanilla pickup RPC that our guards
	/// cannot reach — see <see cref="CheckCritterIntegrity"/>).</summary>
	private const float IntegrityCheckInterval = 0.5f;

	/// <summary>Minimum gap between respawns, so a hostile unmodded master repeatedly stealing
	/// the critter cannot turn the room into a spawn loop.</summary>
	private const float RespawnThrottleSeconds = 3f;

	private Character _character;
	private CritterKind _kind;
	private GameObject _critterRoot;
	private Rigidbody _critterRigidbody;
	private Mob _mob;
	private FrogTongue _frog;
	private Beetle _beetle;
	private Scorpion _scorpion;
	private Dynamite _dynamite;
	private Breakable _breakable;
	private Item _item;
	private PhotonView _critterView;
	private Vector3 _prevCenter;
	private Vector3 _cameraVelocity;
	private Vector3 _cameraSmoothedPosition;
	private Quaternion _cameraSmoothedRotation;
	private bool _cameraHasSmoothedPosition;
	private float _nextJumpAllowedTime;
	private float _nextAttackAllowedTime;
	private float _nextUnmoddedSyncTime;
	private float _nextIntegrityCheckTime;
	private float _nextRespawnTime;
	private float _lastGroundedTime = -10f;
	private float _frogJumpPressedTime = -10f;
	private float _attackPressedTime = -10f;
	private float _frogLocalLickBusyUntil;
	private float _coconutChargeStartTime = -1f;
	private float _itemRestoreAt = -1f;
	private Vector3 _lastKnownCritterPosition;
	private Vector3 _transformEntryRestorePosition;
	private Vector3 _lastSafeExitAnchor;
	private bool _coconutSlamQueued;
	private bool _bombIgnited;
	private bool _restoreAtTransformEntryOnExit;
	private bool _loggedControlledPhysics;
	private Vector3 _controlledFlatVelocity;
	private Renderer[] _localRenderers;
	private readonly List<KeyValuePair<Rigidbody, RigidbodyInterpolation>> _savedInterpolations =
		new List<KeyValuePair<Rigidbody, RigidbodyInterpolation>>();

	/// <summary>True while the form is active.</summary>
	public bool Active { get; private set; }

	/// <summary>The critter kind currently active (valid while Active).</summary>
	internal CritterKind Kind => _kind;

	/// <summary>The local character currently in critter form, or null.</summary>
	public static Character ActiveCritterCharacter { get; private set; }

	// ------------------------------------------------------------------
	// Registry: view id -> kind, shared with the Harmony patches so every modded
	// client can recognize a transformed critter (regardless of owner) and suppress
	// its vanilla AI there too.
	// ------------------------------------------------------------------

	private static readonly Dictionary<int, CritterKind> KindByViewId = new Dictionary<int, CritterKind>();

	/// <summary>Instantiation-data marker on the networked critter. Unlike the owner-only
	/// <see cref="KindByViewId"/> registry, the marker is present on EVERY client (PUN passes
	/// InstantiationData through to remote copies), so remote modded clients — and the pickup
	/// guards in CritterHarmonyPatches — can recognize a transformed critter without registration.</summary>
	internal const string NetworkVisualMarker = "ImCritter.Visual";

	/// <summary>True for any PhotonView that is a transformed critter: the owner's registry,
	/// or the instantiation-data marker visible on every remote client.</summary>
	internal static bool IsCritterView(PhotonView view)
	{
		if (view == null) return false;
		if (KindByViewId.ContainsKey(view.ViewID)) return true;
		object[] data = view.InstantiationData;
		return data != null && data.Length > 0 && data[0] is string marker && marker == NetworkVisualMarker;
	}

	internal static bool TryGetKind(PhotonView view, out CritterKind kind)
	{
		kind = CritterKind.Frog;
		if (view == null) return false;
		if (KindByViewId.TryGetValue(view.ViewID, out kind)) return true;
		object[] data = view.InstantiationData;
		if (data != null
		    && data.Length > 1
		    && data[0] is string marker
		    && marker == NetworkVisualMarker
		    && data[1] is int rawKind
		    && Enum.IsDefined(typeof(CritterKind), rawKind))
		{
			kind = (CritterKind)rawKind;
			return true;
		}
		return false;
	}

	internal static bool IsManualBombIgnitionInProgress(PhotonView view)
	{
		try
		{
			if (view == null || ActiveCritterCharacter == null) return false;
			CritterController ctrl = ((Component)ActiveCritterCharacter).GetComponent<CritterController>();
			return ctrl != null
			       && ctrl.Active
			       && ctrl._kind == CritterKind.Bomb
			       && ctrl._bombIgnited
			       && ctrl._critterView == view;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsLocalCritterCharacter(Character character)
	{
		return character != null && character.IsLocal && ActiveCritterCharacter == character;
	}

	private static void LogInfo(string message) => CritterPlugin.Log?.LogInfo("[Critter] " + message);
	private static void LogError(string entryPoint, Exception ex) => CritterPlugin.Log?.LogError("[Critter] " + entryPoint + ": " + ex);

	// ------------------------------------------------------------------
	// Validity / end-game safety nets
	// ------------------------------------------------------------------

	/// <summary>Validity gate checked by the module every frame.</summary>
	public bool IsValid()
	{
		if (!Active || _character == null) return false;
		if (ShouldRestoreOnPickupHijack()) return true;
		if (IsBreakRestoreItemForm(_kind)) return true;
		if (_critterRoot == null || _critterRigidbody == null) return false;
		if (RequiresMob(_kind) && _mob == null) return false;
		return true;
	}

	private static bool RequiresMob(CritterKind kind)
	{
		return !IsBreakRestoreItemForm(kind);
	}

	private static bool IsBreakRestoreItemForm(CritterKind kind)
	{
		return kind == CritterKind.Coconut || kind == CritterKind.Bomb || kind == CritterKind.Cactus;
	}

	private static bool IsTransformEntryRestoreForm(CritterKind kind)
	{
		return kind == CritterKind.Coconut || kind == CritterKind.Bomb;
	}

	private static bool IsPickupRestoreWhenMasterUnmoddedForm(CritterKind kind)
	{
		return kind == CritterKind.Frog
		       || kind == CritterKind.Scorpion
		       || kind == CritterKind.Coconut
		       || kind == CritterKind.Bomb
		       || kind == CritterKind.Cactus;
	}

	private bool ShouldRestoreOnPickupHijack()
	{
		return IsPickupRestoreWhenMasterUnmoddedForm(_kind) && !CritterPlugin.MasterHasMod();
	}

	private bool ShouldUseLastKnownRestorePosition()
	{
		return _itemRestoreAt > 0f
		       && _lastKnownCritterPosition.sqrMagnitude > 0.0001f
		       && (IsBreakRestoreItemForm(_kind) || ShouldRestoreOnPickupHijack());
	}

	private bool ShouldRestoreAtTransformEntryPosition()
	{
		return _itemRestoreAt > 0f
		       && _restoreAtTransformEntryOnExit
		       && IsTransformEntryRestoreForm(_kind)
		       && IsFiniteVector(_transformEntryRestorePosition)
		       && _transformEntryRestorePosition.sqrMagnitude > 0.0001f;
	}

	/// <summary>
	/// Scene-switch / end-game safety net: force-exit any active critter form. Runs when the new
	/// scene is already active, so ExitCritter skips the position restore (the networked critter
	/// item is destroyed with the old scene) and simply clears state.
	/// </summary>
	internal static void ForceExitForEndGame()
	{
		try
		{
			if (ActiveCritterCharacter != null)
			{
				CritterController ctrl = ((Component)ActiveCritterCharacter).GetComponent<CritterController>();
				if (ctrl != null && ctrl.Active) { ctrl.ExitCritter(); return; }
			}
			foreach (CritterController ctrl in UnityObject.FindObjectsByType<CritterController>(FindObjectsSortMode.None))
			{
				if (ctrl != null && ctrl.Active) { ctrl.ExitCritter(); return; }
			}
		}
		catch (Exception ex)
		{
			LogError("ForceExitForEndGame", ex);
		}
	}

	/// <summary>
	/// The nested Mob.MobState enum is not compile-time visible, so resolve its Walking value
	/// through reflection once and cache the boxed enum value.
	/// </summary>
	private static object _walkingMobState;

	internal static object GetWalkingMobState()
	{
		if (_walkingMobState == null)
		{
			Type stateType = AccessTools.Inner(typeof(Mob), "MobState");
			if (stateType != null && stateType.IsEnum)
			{
				_walkingMobState = Enum.Parse(stateType, "Walking");
			}
		}
		return _walkingMobState;
	}

	// ------------------------------------------------------------------
	// Enter / exit
	// ------------------------------------------------------------------

	internal bool EnterCritter(Character character, CritterKind kind)
	{
		if (Active) return false;
		try
		{
			GameObject spawned = SpawnCritter(character, kind, false);
			if (spawned == null)
			{
				LogInfo("Failed to spawn critter prefab for " + kind + ".");
				return false;
			}

			_character = character;
			_kind = kind;
			_prevCenter = character.Center;
			_cameraVelocity = Vector3.zero;
			_cameraHasSmoothedPosition = false;
			_nextJumpAllowedTime = 0f;
			_nextAttackAllowedTime = 0f;
			_frogJumpPressedTime = -10f;
			_attackPressedTime = -10f;
			_frogLocalLickBusyUntil = 0f;
			_coconutChargeStartTime = -1f;
			_itemRestoreAt = -1f;
			_coconutSlamQueued = false;
			_bombIgnited = false;
			_lastKnownCritterPosition = spawned.transform.position;
			_transformEntryRestorePosition = character.Center;
			_lastSafeExitAnchor = character.Center;
			_restoreAtTransformEntryOnExit = false;
			Active = true;
			ActiveCritterCharacter = character;
			enabled = true;

			SetCritterNoClip(true);
			HideLocalRenderers();
			HideHud();

			LogInfo("Entered " + kind + " form.");
			return true;
		}
		catch (Exception ex)
		{
			LogError("EnterCritter(" + kind + ")", ex);
			TearDownCritter();
			return false;
		}
	}

	private GameObject SpawnCritter(Character character, CritterKind kind, bool useLastKnownPosition)
	{
		Vector3 position = useLastKnownPosition && _lastKnownCritterPosition.sqrMagnitude > 0.0001f
			? _lastKnownCritterPosition
			: character.Center + Vector3.up * 0.5f;
		string[] prefabNames = GetPrefabNameCandidates(kind);
		GameObject spawned = null;
		string prefabName = prefabNames.Length > 0 ? prefabNames[0] : kind.ToString();
		for (int i = 0; i < prefabNames.Length && spawned == null; i++)
		{
			prefabName = prefabNames[i];
			try
			{
				if (PhotonNetwork.InRoom)
				{
					spawned = PhotonNetwork.Instantiate("0_Items/" + prefabName, position, Quaternion.identity,
						0, new object[] { NetworkVisualMarker, (int)kind });
				}
				else
				{
					// Offline / single-player: local-only item (mirrors the tumbleweed/ghost/tornado
					// local fallback). No network marker needed — no remote copies exist.
					GameObject prefab = Resources.Load<GameObject>("0_Items/" + prefabName);
					if (prefab == null) prefab = Resources.Load<GameObject>(prefabName);
					if (prefab != null)
					{
						spawned = UnityObject.Instantiate(prefab, position, Quaternion.identity);
						spawned.name = "CritterLocal_" + prefabName;
					}
				}
			}
			catch (Exception ex)
			{
				LogInfo("Spawn candidate 0_Items/" + prefabName + " failed: " + ex.Message);
				spawned = null;
			}
		}
		if (spawned == null)
		{
			LogInfo("SpawnCritter returned null for " + kind + ".");
			return null;
		}

		_critterRoot = spawned;
		_critterView = spawned.GetComponent<PhotonView>();
		_item = spawned.GetComponent<Item>();
		_critterRigidbody = spawned.GetComponent<Rigidbody>();
		if (_critterRigidbody == null && _item != null) _critterRigidbody = _item.rig;
		_mob = spawned.GetComponent<Mob>();
		_frog = spawned.GetComponent<FrogTongue>();
		_beetle = spawned.GetComponent<Beetle>();
		_scorpion = spawned.GetComponent<Scorpion>();
		_dynamite = spawned.GetComponent<Dynamite>();
		_breakable = spawned.GetComponent<Breakable>();

		if (_critterRigidbody == null || (_mob == null && !IsBreakRestoreItemForm(kind)))
		{
			LogInfo("Prefab 0_Items/" + prefabName + " has no required Rigidbody/Mob for " + kind + ".");
			return null;
		}

		// Physics must never rotate the critter: the tiny beetle used to flip over the moment
		// it touched a bump (collision torque on a 0.2m body). freezeRotation locks ALL
		// physics-driven rotation (collisions, torque, angular velocity) while our own
		// MoveRotation facing control keeps working (it is a direct set, not a torque).
		// Note: Mob.FixedUpdate resets rig.CONSTRAINTS to None every step — that property is
		// separate from freezeRotation, so this flag survives; KeepCritterControlled still
		// re-asserts it each frame as insurance.
		_critterRigidbody.freezeRotation = !IsBreakRestoreItemForm(kind);
		if (kind == CritterKind.Bomb && _dynamite != null)
		{
			// Vanilla dynamite auto-lights when any Character is within lightFuseRadius.
			// In bomb form the hidden rider follows the dynamite, so the default radius would
			// light immediately. Keep the vanilla fuse/explosion path, but make RMB the only igniter.
			_dynamite.lightFuseRadius = 0f;
		}

		// Register BEFORE disabling the brain so the Harmony patches (also on remote modded
		// clients once they see the object — see the PhotonView instantiation callback below)
		// can recognize it. Remote clients register via the CritterHarmonyPatches prefix the
		// first time their own Mob.Update runs on the object.
		if (_critterView != null) KindByViewId[_critterView.ViewID] = kind;

		// Owner-side AI shutdown. Remote copies are handled by the Harmony prefixes.
		if (_mob != null)
		{
			_mob.hasBrain = false;
			// Mob.FixedUpdate otherwise clears the velocity and runs its own Walking AI.
			// This flag only disables vanilla movement; our controller remains responsible
			// for the Rigidbody and the Mob.Update Harmony prefix keeps attacks/animation alive.
			TrySetMobField("forceNoMovement", true);
			TrySetMobStateWalking();
		}
		_controlledFlatVelocity = Vector3.zero;
		ApplyControlledRigidbodySettings(true);
		if (_mob != null && _mob.anim != null)
		{
			// Sleeping would freeze the item physics syncer — keep the critter awake.
			TrySetMobField("sleeping", false);
		}

		// Nobody may pick up the transformed player.
		if (_item != null)
		{
			_item.blockInteraction = true;
		}

		return spawned;
	}

	private static string[] GetPrefabNameCandidates(CritterKind kind)
	{
		switch (kind)
		{
			case CritterKind.Frog:
				return new[] { "Frog" };
			case CritterKind.Beetle:
				return new[] { "Beetle" };
			case CritterKind.Scorpion:
				return new[] { "Scorpion" };
			case CritterKind.Coconut:
				return new[] { "item_coconut", "Item_Coconut", "Coconut", "CocoNut", "CoconutItem" };
			case CritterKind.Bomb:
				return new[] { "Dynamite", "Bomb" };
			case CritterKind.Cactus:
				return new[] { "cactusball", "CactusBall" };
			default:
				throw new ArgumentOutOfRangeException(nameof(kind));
		}
	}
	public void ExitCritter()
	{
		if (!Active && _critterRoot == null) return;
		try
		{
			Active = false;
			ActiveCritterCharacter = null;
			enabled = false;
			PositionCharacterForExit();
			SetCritterNoClip(false);
			DestroyCritter();
			ForceShowLocalRenderers();
			_localRenderers = null;
			RestoreHud();
			_lastKnownCritterPosition = Vector3.zero;
			_transformEntryRestorePosition = Vector3.zero;
			_lastSafeExitAnchor = Vector3.zero;
			_restoreAtTransformEntryOnExit = false;
			LogInfo("Exited " + _kind + " form.");
		}
		catch (Exception ex)
		{
			LogError("ExitCritter", ex);
			TearDownCritter();
		}
	}

	/// <summary>Immediate cleanup used on enter failure and OnDestroy.</summary>
	private void TearDownCritter()
	{
		Active = false;
		ActiveCritterCharacter = null;
		enabled = false;
		SetCritterNoClip(false);
		DestroyCritter();
		_lastKnownCritterPosition = Vector3.zero;
		_transformEntryRestorePosition = Vector3.zero;
		_lastSafeExitAnchor = Vector3.zero;
		_restoreAtTransformEntryOnExit = false;
	}

	private void DestroyCritter()
	{
		if (_critterView != null)
		{
			KindByViewId.Remove(_critterView.ViewID);
		}
	if (_critterRoot != null)
	{
		try
		{
			// Offline critter: no networked view — always destroy locally.
			if (PhotonNetwork.InRoom && _critterView != null && _critterView.IsMine)
			{
				PhotonNetwork.Destroy(_critterRoot);
			}
			else
			{
				UnityObject.Destroy(_critterRoot);
			}
		}
		catch (Exception ex)
		{
			LogError("DestroyCritter", ex);
		}
		}
		_critterRoot = null;
		_critterView = null;
		_critterRigidbody = null;
		_mob = null;
		_item = null;
		_frog = null;
		_beetle = null;
		_breakable = null;
		_dynamite = null;
		_controlledFlatVelocity = Vector3.zero;
		_scorpion = null;
		_coconutChargeStartTime = -1f;
		_itemRestoreAt = -1f;
		_coconutSlamQueued = false;
		_bombIgnited = false;
		_loggedControlledPhysics = false;
	}

	// ------------------------------------------------------------------
	// Unity lifecycle
	// ------------------------------------------------------------------

	private void Update()
	{
		if (!Active || _character == null) return;
		try
		{
			ClearNonMovementInput();
			KeepPlayerAlive();
			UpdatePickupOrBreakRestoreRecovery();
			KeepCritterControlled();
			UpdateUnmoddedSync();
			KeepLocalRenderersHidden();
			HideHud();
			HandleStamina();
			BufferFrogJumpInput();
			BufferAttackInput();

			// Safety nets: the networked critter died / was destroyed / fell out of the world.
			CheckCritterIntegrity();
			if (_critterRoot != null && _critterRoot.transform.position.y < VoidExitHeight)
			{
				LogInfo("Critter fell out of the world; exiting form.");
				ExitCritter();
			}
		}
		catch (Exception ex)
		{
			LogError("CritterController.Update", ex);
		}
	}

	// ------------------------------------------------------------------
	// Pickup-hijack self-heal. Our Item.Interact / Item.RequestPickup guards
	// cover modded pickers and a modded master, but when the room's MASTER is
	// UNMODDED their client runs the vanilla RequestPickup → Player.AddItem
	// ("Only Master Client can add items!") → the critter is granted to the
	// picker and destroyed — the transformed player's model vanishes and the
	// form force-exits. Beetle still rebuilds a fresh networked critter so the
	// player stays transformed; frog / scorpion / coconut / bomb instead restore
	// the player at the last known critter position when this unmodded-master
	// pickup hijack is detected. Coconut and bomb also restore on their normal
	// crack / explosion destruction path.
	// ------------------------------------------------------------------

	private void CheckCritterIntegrity()
	{
		if (Time.unscaledTime < _nextIntegrityCheckTime) return;
		_nextIntegrityCheckTime = Time.unscaledTime + IntegrityCheckInterval;

		if (IsBreakRestoreItemForm(_kind) || ShouldRestoreOnPickupHijack())
		{
			bool missingOrDestroyed = _critterRoot == null || _critterRigidbody == null || _critterView == null
			    || (!IsBreakRestoreItemForm(_kind) && _mob == null)
			    || (IsBreakRestoreItemForm(_kind) && IsBreakableAlreadyBroke());
			if (missingOrDestroyed
			    || (_item != null && _item.itemState != ItemState.Ground)
			    || (_critterView != null && _critterView.Owner != null && _critterView.Owner != PhotonNetwork.LocalPlayer))
			{
				SchedulePickupOrBreakRestore(IsTransformEntryRestoreForm(_kind) && missingOrDestroyed);
			}
			return;
		}

		if (_critterRoot == null || _mob == null || _critterView == null)
		{
			LogInfo("Critter object was destroyed (likely picked up by another player); respawning.");
			RespawnCritter();
			return;
		}
		if (_critterView.Owner != null && _critterView.Owner != PhotonNetwork.LocalPlayer)
		{
			LogInfo("Critter ownership was taken; respawning.");
			RespawnCritter();
			return;
		}
		if (_item != null && _item.itemState != ItemState.Ground)
		{
			LogInfo("Critter was put in a backpack; respawning.");
			RespawnCritter();
		}
	}

	private void RespawnCritter()
	{
		if (!Active || _character == null) return;
		if (IsBreakRestoreItemForm(_kind) || ShouldRestoreOnPickupHijack()) { RestorePlayerAfterPickupOrBreak(); return; }
		if (!PhotonNetwork.InRoom) { ExitCritter(); return; }
		if (Time.unscaledTime < _nextRespawnTime) return;
		_nextRespawnTime = Time.unscaledTime + RespawnThrottleSeconds;

		try
		{
			CritterKind kind = _kind;
			// Destroy the old networked object (owner destroy always succeeds) and clear
			// its registry entry; keep the form active and the player no-clipped/hidden.
			DestroyCritter();
			GameObject spawned = SpawnCritter(_character, kind, true);
			if (spawned == null)
			{
				LogInfo("Respawn failed; exiting critter form.");
				ExitCritter();
				return;
			}
			_prevCenter = _character.Center;
			_nextJumpAllowedTime = Time.time + 0.3f;
			_nextAttackAllowedTime = Time.time + 0.5f;
			_nextUnmoddedSyncTime = 0f;
			_frogJumpPressedTime = -10f;
			_attackPressedTime = -10f;
			_frogLocalLickBusyUntil = 0f;
			_coconutChargeStartTime = -1f;
			_itemRestoreAt = -1f;
			_coconutSlamQueued = false;
			_bombIgnited = false;
			LogInfo("Respawned critter form: " + kind);
		}
		catch (Exception ex)
		{
			LogError("RespawnCritter", ex);
		}
	}

	private void FixedUpdate()
	{
		if (!Active || _character == null) return;
		try
		{
			FollowCritterWithCharacterRoot();
			DriveCritterPhysics();
		}
		catch (Exception ex)
		{
			LogError("CritterController.FixedUpdate", ex);
		}
	}

	private void LateUpdate()
	{
		if (!Active || _character == null) return;
		try
		{
			SyncCharacterData();
			UpdateSafeExitAnchor();
			RefreshCamera();
		}
		catch (Exception ex)
		{
			LogError("CritterController.LateUpdate", ex);
		}
	}

	private void OnDestroy()
	{
		if (Active)
		{
			Active = false;
			ActiveCritterCharacter = null;
			SetCritterNoClip(false);
			DestroyCritter();
			ForceShowLocalRenderers();
			RestoreHud();
		}
	}

	// ------------------------------------------------------------------
	// Mob control surface
	// ------------------------------------------------------------------

	/// <summary>
	/// Re-asserts owner-side control every frame: MobItem/Item updates may re-enable pickup
	/// or kinematic/no-movement state once the item settles on the ground. Our execution order
	/// (600) runs after the game scripts, so clear those locks here before player physics runs.
	/// </summary>
	private void KeepCritterControlled()
	{
		try
		{
			if (_item != null && !_item.blockInteraction) _item.blockInteraction = true;
			if (_critterRigidbody != null)
			{
				ApplyControlledRigidbodySettings(false);
			}
			if (_kind == CritterKind.Bomb && _dynamite != null && _dynamite.lightFuseRadius != 0f)
			{
				_dynamite.lightFuseRadius = 0f;
			}
			if (_mob == null) return;
			if (_mob.hasBrain) _mob.hasBrain = false;
			TrySetMobField("forceNoMovement", true);
			TrySetMobField("sleeping", false);
			// Walk animation driven from the actual displacement (vanilla recipe).
			_mob.UpdateAnimation();
		}
		catch (Exception ex)
		{
			LogError("KeepCritterControlled", ex);
		}
	}

	private void ApplyControlledRigidbodySettings(bool logOnce)
	{
		if (_critterRigidbody == null) return;
		try
		{
			// Mob.FixedUpdate resets constraints to None when forceNoMovement is true.
			// Re-assert the owner controller's physics surface after vanilla has run.
			_critterRigidbody.isKinematic = false;
			_critterRigidbody.useGravity = true;
			_critterRigidbody.detectCollisions = true;
			_critterRigidbody.constraints = IsBreakRestoreItemForm(_kind)
				? RigidbodyConstraints.None
				: RigidbodyConstraints.FreezeRotation;
			_critterRigidbody.linearDamping = ControlledLinearDamping;
			_critterRigidbody.angularDamping = ControlledAngularDamping;
			_critterRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
			_critterRigidbody.sleepThreshold = 0f;
			_critterRigidbody.WakeUp();

			if (logOnce && !_loggedControlledPhysics)
			{
				_loggedControlledPhysics = true;
				LogInfo("Controlled physics for " + _kind
					+ ": linearDamping=" + _critterRigidbody.linearDamping.ToString("0.###")
					+ " angularDamping=" + _critterRigidbody.angularDamping.ToString("0.###")
					+ " constraints=" + _critterRigidbody.constraints
					+ " kinematic=" + _critterRigidbody.isKinematic);
			}
		}
		catch (Exception ex)
		{
			LogError("ApplyControlledRigidbodySettings", ex);
		}
	}

	private void TrySetMobField(string fieldName, object value)
	{
		try
		{
			AccessTools.Field(typeof(Mob), fieldName)?.SetValue(_mob, value);
		}
		catch (Exception ex)
		{
			LogError("TrySetMobField(" + fieldName + ")", ex);
		}
	}

	// ------------------------------------------------------------------
	// Unmodded-room support ("kinematic pin + mob-state broadcast").
	//
	// Unmodded clients run the FULL vanilla critter scripts on our networked
	// prefab — Mob.Update (hasBrain is local-only, so it stays true there),
	// FrogTongue.LateUpdate's master-client AI (CheckAllCharacters → auto
	// licks / hops / repositions) and Mob.FixedUpdate. They cannot be patched
	// remotely, so we use the vanilla prefab's OWN replicated channels to make
	// their copy inert and glued to the rider (the same trick the reference
	// FrogSkill mod uses to steer a vanilla frog proxy on unmodded clients):
	//
	//  1. Item::SetKinematicRPC(true, pos, rot) [only unmodded remote players]
	//     → each unmodded remote client sets its rigidbody kinematic, teleports it to
	//       the rider's real position. A kinematic body ignores every force the
	//       vanilla AI applies, so their copy cannot wander; the next pin re-asserts
	//       the position.
	//  2. Mob::RPC_SyncMobState(0) [only unmodded remote players] → RigidbodyControlled:
	//       Mob.Update's remote branch only attacks when mobState == Walking(1),
	//       so the state broadcast keeps their state machine inert too.
	//
	// The owner's local copy is untouched (Others excludes us) — physics feel
	// is identical to an all-modded room. Modded remote clients are not pinned:
	// their AI is already Harmony-suppressed, and the vanilla ItemPhysicsSyncer
	// can then interpolate normally instead of receiving 10Hz hard teleports.
	// ------------------------------------------------------------------

	private void UpdateUnmoddedSync()
	{
		if (_critterView == null || _critterRoot == null || _critterRigidbody == null)
		{
			return;
		}
		if (!PhotonNetwork.InRoom)
		{
			return;
		}

		// Re-evaluated every tick so the menu toggle / a player joining or leaving
		// flips the mode live. Owner-only: the RPCs below are gated on IsMine.
		bool enabled = CritterPlugin.UnmoddedRoomSupport != null
			&& CritterPlugin.UnmoddedRoomSupport.Value
			&& CritterPlugin.RoomHasUnmoddedPlayers();
		if (!enabled)
		{
			return;
		}
		if (Time.unscaledTime < _nextUnmoddedSyncTime)
		{
			return;
		}
		_nextUnmoddedSyncTime = Time.unscaledTime + UnmoddedSyncInterval;

		try
		{
			// Rigidbody position (not transform): the physics body is what the
			// remote copies and what the vanilla AI would try to push.
			Vector3 pos = _critterRigidbody.position;
			Quaternion rot = _critterRigidbody.rotation;
			if (!IsFiniteVector(pos)) return;

			// Pin only unmodded remote copies. Sending this to modded clients caused visible
			// 10Hz hard corrections even though their AI is already suppressed locally.
			PhotonView itemView = _item != null ? _item.photonView : _critterView;
			bool canPinKinematic = PhotonViewHasRpcMethod(itemView, "SetKinematicRPC");
			foreach (Photon.Realtime.Player player in CritterPlugin.UnmoddedRemotePlayers())
			{
				if (canPinKinematic)
				{
					itemView.RPC("SetKinematicRPC", player, true, pos, rot);
				}

				// Park the vanilla state machine in RigidbodyControlled (0) on unmodded remotes:
				// Mob.Update's non-owner branch only runs Attacking() in Walking(1), and
				// Mob.FixedUpdate just clears constraints for non-owners regardless.
				if (_mob != null) _critterView.RPC("RPC_SyncMobState", player, 0);
			}
		}
		catch (Exception ex)
		{
			LogError("UpdateUnmoddedSync", ex);
		}
	}

	private void ForceCritterPhysicsSync(int frames)
	{
		if (_item == null || !PhotonNetwork.InRoom || _critterView == null || !_critterView.IsMine)
		{
			return;
		}

		try
		{
			ItemForceSyncForFramesMethod?.Invoke(_item, new object[] { Mathf.Max(1, frames) });
		}
		catch
		{
			// Best-effort: regular ItemPhysicsSyncer still runs if this reflective hook changes.
		}
	}

	private static bool IsFiniteVector(Vector3 v)
	{
		return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
			&& !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
	}

	private static bool IsFiniteQuaternion(Quaternion q)
	{
		return !float.IsNaN(q.x) && !float.IsNaN(q.y) && !float.IsNaN(q.z) && !float.IsNaN(q.w)
			&& !float.IsInfinity(q.x) && !float.IsInfinity(q.y) && !float.IsInfinity(q.z) && !float.IsInfinity(q.w);
	}

	private void TrySetMobStateWalking()
	{
		try
		{
			object walking = GetWalkingMobState();
			if (walking != null)
			{
				AccessTools.PropertySetter(typeof(Mob), "mobState")?.Invoke(_mob, new object[] { walking });
			}
		}
		catch (Exception ex)
		{
			LogError("TrySetMobStateWalking", ex);
		}
	}

	// ------------------------------------------------------------------
	// Physics: WASD force, sprint, hop / frog leap, attacks.
	// ------------------------------------------------------------------

	private void DriveCritterPhysics()
	{
		if (_critterRigidbody == null) return;

		if (global::TransformState.MenuOpen) return;
		ApplyControlledRigidbodySettings(false);

		Vector3 forward = GetFlatLookDirection();
		Vector2 input = GetMovementInput();
		Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
		Vector3 move = forward * input.y + right * input.x;
		if (move.sqrMagnitude > 1f) move.Normalize();

		bool sprinting = IsSprintHeld() && CanSprint();
		float multiplier = sprinting ? Mathf.Max(1f, CritterPlugin.SprintMultiplier(_kind).Value) : 1f;
		float speedCap = CritterPlugin.MaxSpeed(_kind).Value * multiplier;

		bool isFrog = _kind == CritterKind.Frog && _frog != null;
		bool isBeetle = _kind == CritterKind.Beetle;
		bool isBreakRestoreItem = IsBreakRestoreItemForm(_kind);
		bool isCoconut = _kind == CritterKind.Coconut;
		bool isBomb = _kind == CritterKind.Bomb;
		bool isCactus = _kind == CritterKind.Cactus;

		if (isFrog)
		{
			// Frog movement is ONLY hops: WASD picks the hop direction; Space applies direct
			// launch speeds.
		}
		else if (isCoconut || isBomb || isCactus)
		{
			// Coconut/Bomb should feel like the tumbleweed: a real rolling rigidbody, not a sliding token.
			if (move.sqrMagnitude > 0.0001f)
			{
				Vector3 velocity = _critterRigidbody.linearVelocity;
				Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
				if (flatVelocity.magnitude < speedCap)
				{
					_critterRigidbody.AddForce(move * CritterPlugin.MovementForce(_kind).Value * multiplier,
						ForceMode.Acceleration);
				}

				Vector3 rollAxis = Vector3.Cross(Vector3.up, move).normalized;
				_critterRigidbody.AddTorque(rollAxis * ItemRollTorque * multiplier, ForceMode.Acceleration);
				ForceCritterPhysicsSync(2);
			}
			if (sprinting && move.sqrMagnitude > 0.0001f)
			{
				UseStamina(Time.fixedDeltaTime * 0.035f);
			}
		}
		else
		{
			// Beetle + scorpion: drive position directly. The vanilla item physics sync can eat
			// owner-written horizontal velocity on grounded critters; MovePosition is the stable
			// scheme that previously moved, without the old extra velocity double-push.
			Vector3 velocity = _critterRigidbody.linearVelocity;
			Vector3 desiredFlat = move.sqrMagnitude > 0.0001f ? move * speedCap : Vector3.zero;
			Vector3 currentFlat = _controlledFlatVelocity;
			float acceleration = _kind == CritterKind.Beetle
				? BeetleMoveAcceleration
				: ScorpionMoveAcceleration;
			Vector3 nextFlat = Vector3.MoveTowards(currentFlat, desiredFlat,
				acceleration * Time.fixedDeltaTime);
			_controlledFlatVelocity = nextFlat;
			_critterRigidbody.linearVelocity = new Vector3(0f, velocity.y, 0f);
			if (nextFlat.sqrMagnitude > 0.000001f)
			{
				_critterRigidbody.MovePosition(_critterRigidbody.position + nextFlat * Time.fixedDeltaTime);
				ForceCritterPhysicsSync(2);
			}
			if ((_kind == CritterKind.Beetle || isBreakRestoreItem) && sprinting && move.sqrMagnitude > 0.0001f)
			{
				UseStamina(Time.fixedDeltaTime * 0.035f);
			}
		}

		// Self-right the beetle when it tips over (grounded but lying on its back/side).
		if (isBeetle && IsCritterGrounded()
		    && Vector3.Dot(_critterRoot.transform.up, Vector3.up) < 0.5f)
		{
			Vector3 flatForward = _critterRoot.transform.forward;
			flatForward.y = 0f;
			if (flatForward.sqrMagnitude < 0.0001f) flatForward = forward;
			Quaternion upright = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
			_critterRigidbody.MoveRotation(Quaternion.Slerp(
				_critterRigidbody.rotation, upright, Time.deltaTime * 6f));
			_critterRigidbody.angularVelocity *= 0.5f;
		}

		// Face the movement direction (vanilla mobs turn toward their walk direction).
		if (!isBreakRestoreItem && move.sqrMagnitude > 0.0001f)
		{
			Quaternion targetRotation = Quaternion.LookRotation(move, Vector3.up);
			float turnSpeed = _kind == CritterKind.Scorpion ? 4.5f : 6.5f;
			_critterRigidbody.MoveRotation(Quaternion.Slerp(
				_critterRigidbody.rotation, targetRotation, Time.fixedDeltaTime * turnSpeed));
		}

		// Space: the FROG leaps toward the input direction (its only way to move — exactly like
		// the vanilla frog, animation included). The beetle has no jump at all.
		if (isFrog && IsCritterGrounded())
		{
			_lastGroundedTime = Time.time;
		}
		if (isFrog && HasBufferedFrogJump())
		{
			if (Time.time < _nextJumpAllowedTime)
			{
				// 冷却中（正常节奏）。
			}
			else if (!CanFrogJumpNow())
			{
				LogInfo("Frog jump blocked: grounded=" + IsCritterGrounded()
					+ " char.isGrounded=" + (_character?.data?.isGrounded ?? false)
					+ " rb.kinematic=" + (_critterRigidbody != null ? _critterRigidbody.isKinematic : true)
					+ " rb.velY=" + (_critterRigidbody != null ? _critterRigidbody.linearVelocity.y.ToString("0.00") : "n/a")
					+ " y=" + (_critterRoot != null ? _critterRoot.transform.position.y.ToString("0.00") : "n/a")
					+ " sinceGround=" + (Time.time - _lastGroundedTime).ToString("0.00") + "s");
				ConsumeFrogJumpInput();
			}
			else if (!UseStamina(JumpStaminaCost))
			{
				LogInfo("Frog jump blocked: stamina=" + (_character?.data?.currentStamina ?? -1f).ToString("0.00"));
				ConsumeFrogJumpInput();
			}
			else
			{
				ConsumeFrogJumpInput();
				_lastGroundedTime = Time.time;
				Vector3 leapDir = move.sqrMagnitude > 0.0001f ? move : forward;
				float power = CritterPlugin.JumpPower(_kind).Value;
				// Vanilla HopAway plays the Jump trigger on the mob animator first.
				if (_mob.anim != null)
				{
					_mob.anim.SetTrigger("Jump");
				}
				// Do not tune height through FrogTongue.jumpUpwardForce. Vanilla HopAway uses AddForce
				// with ForceMode.Force, which is easily swallowed by mass/damping/collision contacts on
				// a player-controlled frog. Use explicit launch speeds instead: MaxSpeed = horizontal
				// distance/speed, JumpSpeed = vertical height. Normal hops are shorter; holding Shift
				// when Space is pressed restores a longer travel hop without changing height.
				leapDir = leapDir.normalized;
				float sprintDistance = IsSprintHeld()
					? Mathf.Max(1f, CritterPlugin.SprintMultiplier(_kind).Value)
					: 1f;
				float forwardLaunch = Mathf.Clamp(CritterPlugin.MaxSpeed(_kind).Value * power
					* FrogNormalHorizontalLaunchScale * sprintDistance, 2f, 40f);
				float upwardLaunch = Mathf.Clamp(CritterPlugin.JumpSpeed(_kind).Value * power, 4f, 30f);
				_critterRigidbody.linearVelocity = leapDir * forwardLaunch + Vector3.up * upwardLaunch;
				ForceCritterPhysicsSync(8);
				_nextJumpAllowedTime = Time.time + JumpCooldown;
			}
		}
		else if (isBreakRestoreItem && HasBufferedFrogJump())
		{
			if (Time.time >= _nextJumpAllowedTime && IsCritterGrounded() && UseStamina(JumpStaminaCost))
			{
				ConsumeFrogJumpInput();
				Vector3 jumpDir = move.sqrMagnitude > 0.0001f ? move.normalized : Vector3.zero;
				float power = CritterPlugin.JumpPower(_kind).Value;
				float upward = Mathf.Clamp(CritterPlugin.JumpSpeed(_kind).Value * power, 3f, 20f);
				_critterRigidbody.linearVelocity = jumpDir * Mathf.Clamp(CritterPlugin.MaxSpeed(_kind).Value * 0.35f, 0f, 10f)
					+ Vector3.up * upward;
				ForceCritterPhysicsSync(6);
				_nextJumpAllowedTime = Time.time + JumpCooldown;
			}
			else if (Time.time >= _nextJumpAllowedTime)
			{
				ConsumeFrogJumpInput();
			}
		}

		// RMB: attack (unified scheme — right-click is attack in every form).
		if (isCoconut && _coconutSlamQueued && Time.time >= _nextAttackAllowedTime)
		{
			_coconutSlamQueued = false;
			if (TryCoconutSlam())
			{
				_nextAttackAllowedTime = Time.time + CritterPlugin.AttackCooldown(_kind).Value;
			}
		}
		else if (isBomb && HasBufferedAttack() && Time.time >= _nextAttackAllowedTime)
		{
			ConsumeAttackInput();
			if (TryIgniteBomb())
			{
				_nextAttackAllowedTime = Time.time + CritterPlugin.AttackCooldown(_kind).Value;
			}
		}
		else if (HasBufferedAttack() && Time.time >= _nextAttackAllowedTime)
		{
			if (isFrog && !IsFrogTongueReady())
			{
				return;
			}
			ConsumeAttackInput();
			if (TryAttack(forward))
			{
				_nextAttackAllowedTime = Time.time + (_kind == CritterKind.Frog
					? Mathf.Min(0.1f, CritterPlugin.AttackCooldown(_kind).Value)
					: CritterPlugin.AttackCooldown(_kind).Value);
			}
		}
	}

	private bool CanFrogJumpNow()
	{
		if (IsCritterGrounded() || Time.time - _lastGroundedTime <= GroundedJumpBufferSeconds)
		{
			return true;
		}
		if (_critterRigidbody == null)
		{
			return false;
		}
		// Current PEAK builds can leave the spawned frog visually sitting on terrain while all
		// ray/sphere ground probes report false. A near-zero vertical velocity is the reliable
		// signal from the log: rb.velY=0.00 at y=0.63 while the frog is clearly resting.
		return Mathf.Abs(_critterRigidbody.linearVelocity.y) <= FrogGroundedVelocityEpsilon;
	}

	/// <summary>Per-kind attack. Returns true when an attack actually fired.</summary>
	private bool TryAttack(Vector3 forward)
	{
		switch (_kind)
		{
			case CritterKind.Frog:
				return TryFrogTongue();
			case CritterKind.Beetle:
				return TryBeetleBonk(forward);
			case CritterKind.Scorpion:
				return TryScorpionSting(forward);
			case CritterKind.Coconut:
				return TryCoconutSlam();
			case CritterKind.Bomb:
				return TryIgniteBomb();
			case CritterKind.Cactus:
				return TryCactusLaunch();
			default:
				return false;
		}
	}

	private static bool PhotonViewHasRpcMethod(PhotonView view, string methodName)
	{
		if (view == null) return false;
		try
		{
			MonoBehaviour[] behaviours = view.GetComponents<MonoBehaviour>();
			if (behaviours == null) return false;
			for (int i = 0; i < behaviours.Length; i++)
			{
				MonoBehaviour behaviour = behaviours[i];
				if (behaviour == null) continue;
				Type type = behaviour.GetType();
				if (!HasSetKinematicRpcByComponentType.TryGetValue(type, out bool hasMethod))
				{
					hasMethod = AccessTools.Method(type, methodName) != null;
					HasSetKinematicRpcByComponentType[type] = hasMethod;
				}
				if (hasMethod) return true;
			}
		}
		catch
		{
			return false;
		}
		return false;
	}

	private void UpdatePickupOrBreakRestoreRecovery()
	{
		bool canRecoverFromPickupHijack = ShouldRestoreOnPickupHijack();
		if (!IsBreakRestoreItemForm(_kind) && !canRecoverFromPickupHijack) return;
		if (_critterRoot != null)
		{
			_lastKnownCritterPosition = _critterRoot.transform.position;
		}
		if (_itemRestoreAt > 0f && Time.unscaledTime >= _itemRestoreAt)
		{
			RestorePlayerAfterPickupOrBreak();
			return;
		}
		bool missingOrDestroyed = _critterRoot == null || _critterRigidbody == null || _critterView == null
		    || (!IsBreakRestoreItemForm(_kind) && _mob == null)
		    || (IsBreakRestoreItemForm(_kind) && IsBreakableAlreadyBroke());
		if (missingOrDestroyed)
		{
			SchedulePickupOrBreakRestore(IsTransformEntryRestoreForm(_kind));
		}
	}

	private void SchedulePickupOrBreakRestore(bool restoreAtTransformEntry)
	{
		if (!Active) return;
		bool canRecoverFromPickupHijack = ShouldRestoreOnPickupHijack();
		if (!IsBreakRestoreItemForm(_kind) && !canRecoverFromPickupHijack) return;
		if (_critterRoot != null)
		{
			_lastKnownCritterPosition = _critterRoot.transform.position;
		}
		if (restoreAtTransformEntry && IsTransformEntryRestoreForm(_kind))
		{
			_restoreAtTransformEntryOnExit = true;
		}
		if (_itemRestoreAt > 0f) return;
		_itemRestoreAt = Time.unscaledTime + BreakRestoreItemRestoreDelaySeconds;
		_coconutChargeStartTime = -1f;
		_coconutSlamQueued = false;
		string reason = canRecoverFromPickupHijack && !CritterPlugin.MasterHasMod()
			? "was picked up while the master has no mod"
			: "broke, exploded, or was removed";
		LogInfo(_kind + " " + reason + "; restoring player in 1 second.");
	}

	private bool IsBreakableAlreadyBroke()
	{
		if (_breakable == null || BreakableAlreadyBrokeField == null) return false;
		try { return (bool)BreakableAlreadyBrokeField.GetValue(_breakable); }
		catch { return false; }
	}

	private void RestorePlayerAfterPickupOrBreak()
	{
		if (!Active) return;
		if (!IsBreakRestoreItemForm(_kind) && !ShouldRestoreOnPickupHijack()) return;
		try
		{
			string target = ShouldRestoreAtTransformEntryPosition() ? "transform entry position" : "pickup/destroyed position";
			LogInfo("Restoring player at " + _kind + " " + target + ".");
			ExitCritter();
		}
		catch (Exception ex)
		{
			LogError("RestorePlayerAfterPickupOrBreak", ex);
		}
	}

	/// <summary>Coconut: hold RMB to charge, then launch the coconut toward the crosshair point.</summary>
	private bool TryCoconutSlam()
	{
		if (_critterRigidbody == null || _critterRoot == null) return false;
		try
		{
			float held = _coconutChargeStartTime > 0f
				? Mathf.Clamp(Time.time - _coconutChargeStartTime, 0f, CoconutMaxChargeSeconds)
				: CoconutMaxChargeSeconds * 0.35f;
			float charge = Mathf.Clamp01(held / CoconutMaxChargeSeconds);
			Vector3 direction = GetCrosshairLaunchDirection(CoconutSlamRange);
			float speed = Mathf.Lerp(CoconutMinSlamSpeed, CoconutMaxSlamSpeed, charge);
			_critterRigidbody.linearVelocity = direction * speed;
			Vector3 flatDirection = Vector3.ProjectOnPlane(direction, Vector3.up);
			if (flatDirection.sqrMagnitude < 0.0001f) flatDirection = GetFlatLookDirection();
			_critterRigidbody.angularVelocity += Vector3.Cross(Vector3.up, flatDirection.normalized) * Mathf.Lerp(4f, 16f, charge);
			ForceCritterPhysicsSync(10);
			_coconutChargeStartTime = -1f;
			LogInfo("Coconut slam charged " + charge.ToString("0.00") + ".");
			return true;
		}
		catch (Exception ex)
		{
			LogError("TryCoconutSlam", ex);
			return false;
		}
	}

	/// <summary>Bomb: right-click lights the vanilla dynamite fuse. Its own Update/RPC then
	/// handles countdown, explosion effects, network destroy, and our recovery watcher restores
	/// the player one second after the original event removes the item.</summary>
	private bool TryIgniteBomb()
	{
		if (_kind != CritterKind.Bomb || _bombIgnited || _dynamite == null) return false;
		try
		{
			_bombIgnited = true;
			_dynamite.LightFlare();
			ForceCritterPhysicsSync(4);
			LogInfo("Bomb fuse lit through vanilla Dynamite.LightFlare.");
			return true;
		}
		catch (Exception ex)
		{
			_bombIgnited = false;
			LogError("TryIgniteBomb", ex);
			return false;
		}
	}

	/// <summary>Cactus: launch at the crosshair target. The vanilla CactusBall collision code
	/// handles sticking to the remote player's local body when it arrives.</summary>
	private bool TryCactusLaunch()
	{
		if (_kind != CritterKind.Cactus || _critterRigidbody == null || _critterRoot == null) return false;
		try
		{
			Character target = FindCrosshairTarget(CactusLaunchRange, 4f);
			Vector3 aimPoint = target != null ? target.Center : FindCrosshairPoint(CactusLaunchRange);
			Vector3 origin = _critterRigidbody.position;
			Vector3 direction = aimPoint - origin;
			if (direction.sqrMagnitude < 0.25f) direction = GetFlatLookDirection();
			direction = direction.normalized + Vector3.up * CactusLaunchUpBias;
			if (_item != null)
			{
				ItemLastThrownCharacterField?.SetValue(_item, _character);
				ItemLastThrownTimeField?.SetValue(_item, Time.time);
			}
			_critterRigidbody.linearVelocity = direction.normalized * CactusLaunchSpeed;
			_critterRigidbody.angularVelocity += Vector3.Cross(Vector3.up, direction).normalized * 18f;
			ForceCritterPhysicsSync(10);
			LogInfo("Cactus launched" + (target != null ? " at " + target.characterName : " at crosshair") + ".");
			return true;
		}
		catch (Exception ex)
		{
			LogError("TryCactusLaunch", ex);
			return false;
		}
	}

	private Vector3 GetCrosshairLaunchDirection(float range)
	{
		try
		{
			Camera camera = Camera.main;
			if (camera == null)
			{
				Vector3 fallback = GetFlatLookDirection() + Vector3.up * CoconutSlamUpBias;
				return fallback.normalized;
			}

			Ray ray = new Ray(camera.transform.position, camera.transform.forward);
			Vector3 direction = ray.direction;
			if (Physics.Raycast(ray, out RaycastHit hit, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
			    && _critterRigidbody != null)
			{
				Vector3 toHit = hit.point - _critterRigidbody.position;
				if (toHit.sqrMagnitude > 1f)
				{
					direction = Vector3.Slerp(direction, toHit.normalized, 0.25f);
				}
			}
			direction += Vector3.up * CoconutSlamUpBias;
			return direction.normalized;
		}
		catch
		{
			Vector3 fallback = GetFlatLookDirection() + Vector3.up * CoconutSlamUpBias;
			return fallback.normalized;
		}
	}

	private Vector3 FindCrosshairPoint(float range)
	{
		try
		{
			Camera camera = Camera.main;
			if (camera == null || _critterRoot == null)
			{
				return _critterRoot != null
					? _critterRoot.transform.position + GetFlatLookDirection() * range
					: transform.position + transform.forward * range;
			}
			Ray ray = new Ray(camera.transform.position, camera.transform.forward);
			if (Physics.Raycast(ray, out RaycastHit hit, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
			{
				return hit.point;
			}
			return ray.origin + ray.direction * range;
		}
		catch
		{
			return _critterRoot != null
				? _critterRoot.transform.position + GetFlatLookDirection() * range
				: transform.position + transform.forward * range;
		}
	}

	/// <summary>Frog: tongue-shot the crosshair target via the vanilla RPC-synced attack. With no
	/// target in range the frog still lashes its tongue: the visible tongue is the "Lick"
	/// animator trigger (verified in FrogTongue's own LickRoutine) — an "Attack" trigger does
	/// not exist on the frog animator, so the old empty-swing fallback played nothing.</summary>
	private bool TryFrogTongue()
	{
		if (_frog == null) return false;
		try
		{
			ResetFrogTongueIfIdle();
			Character target = FindCrosshairTarget(Mathf.Max(_frog.maxDistance, FrogTongueMinRange), FrogTongueAimRadius);
			if (target == null)
			{
				if (_mob != null && _mob.anim != null)
				{
					_mob.anim.SetTrigger("Lick");
				}
				TryPlayFrogLickSfx();
				_frogLocalLickBusyUntil = Time.time + FrogEmptyLickSeconds;
				LogInfo("Frog tongue lashed (no target).");
				return true;
			}
			_frog.photonView.RPC("RPCA_FrogAction", RpcTarget.All,
				target.photonView, FrogTongue.FrogActionType.Attack, Vector3.zero);
			LogInfo("Frog tongue fired.");
			return true;
		}
		catch (Exception ex)
		{
			LogError("TryFrogTongue", ex);
			return false;
		}
	}

	private bool IsFrogTongueReady()
	{
		if (_frog == null) return true;
		if (Time.time < _frogLocalLickBusyUntil) return false;

		try
		{
			Character target = FrogTargetCharacterField?.GetValue(_frog) as Character;
			bool pulling = FrogIsPullingField != null && (bool)FrogIsPullingField.GetValue(_frog);
			float length = FrogTongueLengthField != null ? (float)FrogTongueLengthField.GetValue(_frog) : 0f;
			return target == null && !pulling && length <= FrogTongueReadyLength;
		}
		catch
		{
			// If reflection breaks in a future game update, fall back to the short controller-side
			// timer instead of permanently locking the tongue.
			return Time.time >= _nextAttackAllowedTime;
		}
	}

	/// <summary>FrogTongue.lickSFX is a public SFX_Instance[] — play one shot at the frog so the
	/// empty swing is audible too. Best-effort: any failure is silently ignored.</summary>
	private void TryPlayFrogLickSfx()
	{
		if (_frog == null || _frog.lickSFX == null || _frog.lickSFX.Length == 0 || _critterRoot == null) return;
		SFX_Instance lick = _frog.lickSFX[0];
		if (lick != null)
		{
			lick.Play(_critterRoot.transform.position);
		}
	}

	/// <summary>Beetle: bonk the crosshair target — vanilla knockdown + physics impulse. With no
	/// target the beetle still lunges forward with its attack animation (ramming the air).</summary>
	private bool TryBeetleBonk(Vector3 forward)
	{
		try
		{
			Character target = FindCrosshairTarget(AttackRange);
			if (target == null)
			{
				PlayAttackAnimationNetworked();
				// Air-ram: a small forward impulse so the bonk reads as a charge even with nobody
				// in front of the beetle.
				_critterRigidbody?.AddForce(forward * 3f + Vector3.up * 1.5f, ForceMode.VelocityChange);
				return true;
			}
			PlayAttackAnimationNetworked();
			float ragdollTime = _beetle != null ? _beetle.ragdollTime : 2f;
			float bonkForce = _beetle != null ? _beetle.bonkForce : 8f;
			// 顶飞高度 ×1.6（默认更夸张）。
			float bonkForceUp = (_beetle != null ? _beetle.bonkForceUp : 6f) * 1.6f;
			Vector3 knock = forward * bonkForce + Vector3.up * bonkForceUp;
			ApplyKnockback(target, knock, ragdollTime);
			LogInfo("Beetle bonk!");
			return true;
		}
		catch (Exception ex)
		{
			LogError("TryBeetleBonk", ex);
			return false;
		}
	}

	/// <summary>Scorpion: sting the crosshair target — knockdown plus the vanilla poison. With no
	/// target the scorpion still plays its sting animation.</summary>
	private bool TryScorpionSting(Vector3 forward)
	{
		try
		{
			Character target = FindCrosshairTarget(AttackRange);
			if (target == null)
			{
				PlayAttackAnimationNetworked();
				return true;
			}
			PlayAttackAnimationNetworked();
			ApplyKnockback(target, forward * 3f + Vector3.up * 2f, 1.5f);
			ApplyPoison(target);
			LogInfo("Scorpion sting!");
			return true;
		}
		catch (Exception ex)
		{
			LogError("TryScorpionSting", ex);
			return false;
		}
	}

	/// <summary>Local-only attack swing (no target): plays the mob animator's Attack trigger.
	/// Harmless when the prefab's animator has no such parameter.</summary>
	private void PlayAttackAnimation()
	{
		if (_mob != null && _mob.anim != null)
		{
			_mob.anim.SetTrigger("Attack");
		}
	}

	private void PlayAttackAnimationNetworked()
	{
		PlayAttackAnimation();
		try
		{
			if (PhotonNetwork.InRoom && _critterView != null && _critterView.IsMine)
			{
				_critterView.RPC("RPC_StartAttack", RpcTarget.Others);
			}
		}
		catch (Exception ex)
		{
			LogInfo("Attack animation RPC failed: " + ex.Message);
		}
	}

	/// <summary>
	/// Knockdown + impulse. RPCA_Fall is broadcast so every client sees the target crumple;
	/// the fling impulse goes through the game's networked force channel
	/// (RPCA_AddForceToBodyPart — the same one the tornado/ghost use, no master gate) so every
	/// client applies it to its local copy of the victim's ragdoll: the victim actually gets
	/// knocked back on their own screen and on every other client's. The old local-only
	/// AddForce made the knockback visible ONLY on the attacker's screen (the synced character
	/// never carried the momentum back to the victim's own client).
	/// </summary>
	private static void ApplyKnockback(Character target, Vector3 force, float ragdollTime)
	{
		if (target == null) return;
		try
		{
			target.photonView?.RPC("RPCA_Fall", RpcTarget.All, ragdollTime, 0f);
		}
		catch (Exception ex)
		{
			CritterPlugin.Log?.LogWarning("[Critter] RPCA_Fall failed: " + ex.Message);
		}
		try
		{
			if (target.photonView != null)
			{
				target.photonView.RPC("RPCA_AddForceToBodyPart", RpcTarget.All,
					BodypartType.Torso, Vector3.zero, force);
			}
		}
		catch (Exception ex)
		{
			CritterPlugin.Log?.LogWarning("[Critter] Knockback broadcast failed: " + ex.Message);
		}
	}

	/// <summary>
	/// Scorpion poison, mirroring Scorpion.InflictAttack's affliction calls. NOTE: the game's
	/// status broadcast (RPC_ApplyStatusesFromFloatArray) only honors the MASTER client
	/// (Sender.IsMasterClient gate), so when the transformed player is not the room's master
	/// the affliction applies only locally — the knockdown above is what replicates
	/// everywhere. Numbers are a touch stronger than vanilla so the sting reads as an attack.
	/// </summary>
	private static void ApplyPoison(Character target)
	{
		try
		{
			CharacterAfflictions afflictions = target.refs != null ? target.refs.afflictions : null;
			if (afflictions == null) return;
			AccessTools.Method(typeof(CharacterAfflictions), "AddStatus")?.Invoke(
				afflictions,
				new object[] { 3, 0.15f, false, true, true, false });
			Type poisonType = AccessTools.TypeByName("Peak.Afflictions.Affliction_PoisonOverTime");
			object affliction = poisonType != null
				? AccessTools.Constructor(poisonType, new[] { typeof(float), typeof(float), typeof(float) })
					?.Invoke(new object[] { 10f, 0f, 0.1f })
				: null;
			if (affliction != null)
			{
				AccessTools.Method(typeof(CharacterAfflictions), "AddAffliction")?.Invoke(
					afflictions, new object[] { affliction, false });
			}
		}
		catch (Exception ex)
		{
			CritterPlugin.Log?.LogWarning("[Critter] Poison affliction failed: " + ex.Message);
		}
	}

	/// <summary>Crosshair target search: nearby characters within range, closest to the aim ray.</summary>
	private Character FindCrosshairTarget(float range)
	{
		return FindCrosshairTarget(range, 2.5f);
	}

	private Character FindCrosshairTarget(float range, float aimRadius)
	{
		try
		{
			Camera camera = Camera.main;
			if (camera == null) return null;
			Ray ray = new Ray(camera.transform.position, camera.transform.forward);
			Character best = null;
			float bestScore = float.MaxValue;
			List<Character> all = Character.AllCharacters;
			for (int i = 0; i < all.Count; i++)
			{
				Character other = all[i];
				if (other == null || other == _character || other.data == null || other.data.dead) continue;
				Vector3 center = other.Center;
				Vector3 toTarget = center - _critterRoot.transform.position;
				if (toTarget.magnitude > range) continue;
				Vector3 rayDir = center - ray.origin;
				float along = Vector3.Dot(rayDir, ray.direction);
				if (along <= 0f) continue;
				Vector3 closest = ray.origin + ray.direction * along;
				float lateral = Vector3.Distance(closest, center);
				float characterRadius = Mathf.Max(0.65f, GetCharacterAimRadius(other));
				float effectiveRadius = Mathf.Max(aimRadius, characterRadius);
				if (lateral > effectiveRadius) continue;
				float score = Mathf.Max(0f, lateral - characterRadius) + toTarget.magnitude * 0.08f;
				if (score < bestScore)
				{
					bestScore = score;
					best = other;
				}
			}
			return best;
		}
		catch (Exception ex)
		{
			LogError("FindCrosshairTarget", ex);
			return null;
		}
	}

	private static float GetCharacterAimRadius(Character character)
	{
		try
		{
			Collider[] colliders = character.GetComponentsInChildren<Collider>(true);
			float radius = 0.65f;
			for (int i = 0; i < colliders.Length; i++)
			{
				Collider collider = colliders[i];
				if (collider == null || collider.isTrigger) continue;
				Vector3 extents = collider.bounds.extents;
				radius = Mathf.Max(radius, Mathf.Min(1.8f, Mathf.Max(extents.x, extents.z)));
			}
			return radius;
		}
		catch
		{
			return 0.65f;
		}
	}

	private void ResetFrogTongueIfIdle()
	{
		if (_frog == null) return;
		try
		{
			Character target = FrogTargetCharacterField?.GetValue(_frog) as Character;
			bool pulling = FrogIsPullingField != null && (bool)FrogIsPullingField.GetValue(_frog);
			float length = FrogTongueLengthField != null ? (float)FrogTongueLengthField.GetValue(_frog) : 0f;
			if (target == null && !pulling && length <= FrogTongueReadyLength * 2f)
			{
				if (FrogTongueLengthField != null) FrogTongueLengthField.SetValue(_frog, 0f);
			}
		}
		catch (Exception ex)
		{
			LogError(nameof(ResetFrogTongueIfIdle), ex);
		}
	}

	private bool IsCritterGrounded()
	{
		// 1) Vanilla character ground state — the game's own physics maintains this reliably
		//    and (in-place-driving) the original body follows the critter every frame, so it
		//    reflects the ground under the critter.
		if (_character != null && _character.data != null && _character.data.isGrounded)
		{
			return true;
		}
		if (_critterRoot == null) return false;
		// 2) Physics fallback: overlap check at the critter's feet. An overlap is the most
		//    robust "am I on the ground" test — it needs no direction/distance and reports a
		//    hit the exact frame the body touches down (ray/sphere CASTS proved unreliable for
		//    the frog: landing bounce, physics-syncer interference).
		float radius = GetCritterRadius();
		Vector3 feet = _critterRoot.transform.position - Vector3.up * (radius * 0.8f);
		return Physics.CheckSphere(
			feet + Vector3.down * 0.25f,
			Mathf.Max(0.08f, radius * 0.5f),
			Physics.DefaultRaycastLayers,
			QueryTriggerInteraction.Ignore);
	}

	private float GetCritterRadius()
	{
		try
		{
			Collider collider = _critterRoot.GetComponentInChildren<Collider>();
			if (collider != null)
			{
				return Mathf.Max(0.1f, collider.bounds.extents.y);
			}
		}
		catch
		{
		}
		return 0.3f;
	}

	// ------------------------------------------------------------------
	// Stamina (zombie recipe: sprint drains, idle regenerates)
	// ------------------------------------------------------------------

	private void HandleStamina()
	{
		if (_character == null || _character.data == null) return;
		CharacterData data = _character.data;
		if (!IsSprintHeld() || GetMovementInput().sqrMagnitude < 0.0001f)
		{
			data.currentStamina = Mathf.Min(1f, data.currentStamina + GetStaminaRegen() * Time.deltaTime);
		}
		// The drain itself is applied in DriveCritterPhysics together with the sprint force.
	}

	private static bool IsSprintHeld()
	{
		if (global::TransformState.MenuOpen) return false;
		return Transform.Core.GameInput.SprintHeld(KeyCode.LeftShift);
	}

	private bool CanSprint()
	{
		return _character != null && _character.data != null && _character.data.currentStamina > MinSprintStamina;
	}

	private bool UseStamina(float cost)
	{
		if (_character == null || _character.data == null) return true;
		CharacterData data = _character.data;
		if (data.currentStamina < cost) return false;
		data.currentStamina = Mathf.Max(0f, data.currentStamina - cost);
		return true;
	}

	private static float GetStaminaRegen()
	{
		return Mathf.Clamp(0.18f, 0.01f, 1f);
	}

	// ------------------------------------------------------------------
	// Input / survival / data sync (tumbleweed recipe)
	// ------------------------------------------------------------------

	private void ClearNonMovementInput()
	{
		if (_character == null || _character.input == null) return;
		_character.input.jumpWasPressed = false;
		_character.input.jumpIsPressed = false;
		_character.input.sprintIsPressed = false;
		_character.input.sprintWasPressed = false;
		_character.input.sprintToggleWasPressed = false;
		_character.input.usePrimaryWasPressed = false;
		_character.input.usePrimaryIsPressed = false;
		_character.input.useSecondaryWasPressed = false;
		_character.input.useSecondaryIsPressed = false;
		_character.input.crouchWasPressed = false;
		_character.input.crouchIsPressed = false;
		_character.input.crouchToggleWasPressed = false;
		_character.input.dropWasPressed = false;
		_character.input.dropIsPressed = false;
		_character.input.interactWasPressed = false;
		_character.input.interactIsPressed = false;
	}

	private void BufferFrogJumpInput()
	{
		if (_kind != CritterKind.Frog && !IsBreakRestoreItemForm(_kind)) return;
		if (global::TransformState.MenuOpen) return;
		if (Transform.Core.GameInput.JumpPressed(CritterPlugin.JumpKey.Value))
		{
			_frogJumpPressedTime = Time.time;
		}
	}

	private bool HasBufferedFrogJump()
	{
		return Time.time - _frogJumpPressedTime <= FrogJumpInputBufferSeconds;
	}

	private void ConsumeFrogJumpInput()
	{
		_frogJumpPressedTime = -10f;
	}

	private static bool AttackPressed()
	{
		return Transform.Core.GameInput.UseSecondaryPressed(CritterPlugin.AttackKey.Value);
	}

	private static bool AttackReleased()
	{
		return Transform.Core.GameInput.UseSecondaryReleased(CritterPlugin.AttackKey.Value);
	}

	private void BufferAttackInput()
	{
		if (global::TransformState.MenuOpen) return;
		if (_kind == CritterKind.Bomb && _bombIgnited) return;
		if (_kind == CritterKind.Coconut)
		{
			if (AttackPressed())
			{
				_coconutChargeStartTime = Time.time;
				_coconutSlamQueued = false;
			}
			if (_coconutChargeStartTime > 0f
			    && (AttackReleased() || Time.time - _coconutChargeStartTime >= CoconutMaxChargeSeconds))
			{
				_coconutSlamQueued = true;
			}
			return;
		}
		if (AttackPressed())
		{
			_attackPressedTime = Time.time;
		}
	}

	private bool HasBufferedAttack()
	{
		return Time.time - _attackPressedTime <= AttackInputBufferSeconds;
	}

	private void ConsumeAttackInput()
	{
		_attackPressedTime = -10f;
	}

	private void KeepPlayerAlive()
	{
		if (_character == null || _character.data == null) return;
		_character.data.dead = false;
		_character.data.zombified = false;
		_character.data.passedOut = false;
		_character.data.fullyPassedOut = false;
		_character.data.fallSeconds = 0f;
		_character.data.isSprinting = false;
	}

	private Vector2 GetMovementInput()
	{
		if (global::TransformState.MenuOpen) return Vector2.zero;

		Vector2 raw = Transform.Core.GameInput.Move();

		if (raw.sqrMagnitude <= MovementInputDeadzone * MovementInputDeadzone)
		{
			float x = 0f;
			float y = 0f;
			if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
			if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
			if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) y += 1f;
			if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) y -= 1f;
			raw = new Vector2(x, y);
		}

		raw.x = Mathf.Clamp(raw.x, -1f, 1f);
		raw.y = Mathf.Clamp(raw.y, -1f, 1f);
		if (raw.sqrMagnitude > 1f) raw.Normalize();
		return raw;
	}

	private Vector3 GetFlatLookDirection()
	{
		Vector3 forward = _character.data != null ? _character.data.lookDirection_Flat : Vector3.zero;
		forward = Vector3.ProjectOnPlane(forward, Vector3.up);
		if (forward.sqrMagnitude < 0.0001f && Camera.main != null)
		{
			forward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
		}
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
		}
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}
		return forward.normalized;
	}

	private void SyncCharacterData()
	{
		if (_character == null || _character.data == null) return;
		Vector3 center = _character.Center;
		Vector3 velocity = _prevCenter.sqrMagnitude > 0f ? (center - _prevCenter) / Mathf.Max(Time.deltaTime, 0.0001f) : Vector3.zero;
		_prevCenter = center;
		_character.data.avarageLastFrameVelocity = _character.data.avarageVelocity;
		_character.data.avarageVelocity = velocity;
		_character.data.worldMovementInput = _character.data.worldMovementInput_Grounded = Vector3.zero;
		_character.data.sinceGrounded = Mathf.Min(_character.data.sinceGrounded, 0.1f);
	}

	private void FollowCritterWithCharacterRoot()
	{
		if (_critterRoot == null || _character == null) return;
		Vector3 critterCenter = _critterRoot.transform.position;
		Vector3 currentCenter = _character.Center;
		transform.position += critterCenter - currentCenter;
	}

	// ------------------------------------------------------------------
	// Exit positioning (tumbleweed recipe)
	// ------------------------------------------------------------------

	private void PositionCharacterForExit()
	{
		if (_character == null) return;
		try
		{
			bool lockToTransformEntry = ShouldRestoreAtTransformEntryPosition();
			Vector3 start = lockToTransformEntry
				? _transformEntryRestorePosition
				: ShouldUseLastKnownRestorePosition()
				? _lastKnownCritterPosition
				: (_critterRoot != null ? _critterRoot.transform.position : transform.position);
			Vector3 target = FindSafeExitPosition(start, lockToTransformEntry);
			SetCharacterPositionImmediate(_character, target, transform.rotation);
			ResetExitFallState(target);
			LogInfo("Repositioned player to safe exit spot: " + target);
		}
		catch (Exception ex)
		{
			LogError("PositionCharacterForExit", ex);
		}
	}

	private void UpdateSafeExitAnchor()
	{
		if (_character == null) return;
		Vector3 source = _critterRoot != null ? _critterRoot.transform.position : _character.Center;
		if (!IsFiniteVector(source)) return;
		if (TryFindLocalExitPositionAround(source, ExitStandHeight(), out Vector3 safe, out _))
		{
			_lastSafeExitAnchor = safe;
		}
	}

	private Vector3 FindSafeExitPosition(Vector3 start, bool lockToStart)
	{
		float standHeight = ExitStandHeight();
		Vector3 bestFallback = Vector3.zero;
		bool hasBestFallback = false;
		Vector3[] anchors = lockToStart
			? new[] { start }
			: new[]
			{
				start,
				_lastSafeExitAnchor,
				_prevCenter,
				_character != null ? _character.Center : Vector3.zero,
				transform.position,
				_character != null && _character.data != null ? _character.data.groundPos : Vector3.zero
			};

		for (int i = 0; i < anchors.Length; i++)
		{
			Vector3 anchor = anchors[i];
			if (!IsFiniteVector(anchor) || anchor.sqrMagnitude <= 0.0001f) continue;
			if (TryFindLocalExitPositionAround(anchor, standHeight, out Vector3 safe, out Vector3 fallback))
			{
				return safe;
			}
			if (!hasBestFallback && IsFiniteVector(fallback) && fallback.sqrMagnitude > 0.0001f)
			{
				bestFallback = fallback;
				hasBestFallback = true;
			}
		}

		for (int i = 0; i < anchors.Length; i++)
		{
			Vector3 anchor = anchors[i];
			if (!IsFiniteVector(anchor) || anchor.sqrMagnitude <= 0.0001f) continue;
			if (TryFindBroadExitPositionAround(anchor, standHeight, out Vector3 safe, out Vector3 fallback))
			{
				return safe;
			}
			if (!hasBestFallback && IsFiniteVector(fallback) && fallback.sqrMagnitude > 0.0001f)
			{
				bestFallback = fallback;
				hasBestFallback = true;
			}
		}

		if (hasBestFallback)
		{
			return bestFallback + Vector3.up * 0.25f;
		}
		if (!lockToStart && IsFiniteVector(_lastSafeExitAnchor) && _lastSafeExitAnchor.sqrMagnitude > 0.0001f)
		{
			return _lastSafeExitAnchor + Vector3.up * 0.25f;
		}
		return (IsFiniteVector(start) ? start : transform.position) + Vector3.up * (standHeight + 0.5f);
	}

	private static float ExitStandHeight()
	{
		return 1.05f;
	}

	private bool TryFindLocalExitPositionAround(Vector3 anchor, float standHeight, out Vector3 safe, out Vector3 fallback)
	{
		return TryFindExitPositionAround(anchor, standHeight, ExitLocalProbeUp, ExitLocalProbeDown, true, out safe, out fallback);
	}

	private bool TryFindBroadExitPositionAround(Vector3 anchor, float standHeight, out Vector3 safe, out Vector3 fallback)
	{
		return TryFindExitPositionAround(anchor, standHeight, ExitBroadProbeHeight, ExitBroadProbeDepth, true, out safe, out fallback);
	}

	private bool TryFindExitPositionAround(Vector3 anchor, float standHeight, float probeUp, float probeDown, bool limitSurfaceRise, out Vector3 safe, out Vector3 fallback)
	{
		safe = Vector3.zero;
		fallback = Vector3.zero;
		bool hasFallback = false;
		for (int offsetIndex = 0; offsetIndex < ExitSearchOffsets.Length; offsetIndex++)
		{
			Vector3 probe = anchor + ExitSearchOffsets[offsetIndex] + Vector3.up * probeUp;
			RaycastHit[] hits = Physics.RaycastAll(
				probe, Vector3.down, probeUp + probeDown,
				Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
			System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

			for (int i = 0; i < hits.Length; i++)
			{
				if (!IsUsableExitGround(hits[i])) continue;
				if (limitSurfaceRise && hits[i].point.y > anchor.y + ExitMaxSurfaceRise) continue;
				Vector3 candidate = hits[i].point + Vector3.up * standHeight;
				if (!hasFallback)
				{
					fallback = candidate;
					hasFallback = true;
				}
				if (IsExitSpaceClear(candidate))
				{
					safe = candidate;
					return true;
				}
			}
		}
		return false;
	}

	private bool IsUsableExitGround(RaycastHit hit)
	{
		if (hit.collider == null) return false;
		if (hit.normal.y < ExitGroundMinNormalY) return false;
		if (_critterRoot != null && hit.collider.transform.IsChildOf(_critterRoot.transform)) return false;
		if (hit.collider.transform.IsChildOf(transform)) return false;
		return true;
	}

	private bool IsExitSpaceClear(Vector3 rootPosition)
	{
		Vector3 bottom = rootPosition + Vector3.up * ExitCapsuleBottom;
		Vector3 top = rootPosition + Vector3.up * ExitCapsuleTop;
		Collider[] overlaps = Physics.OverlapCapsule(
			bottom, top, ExitCapsuleRadius,
			Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

		for (int i = 0; i < overlaps.Length; i++)
		{
			Collider col = overlaps[i];
			if (col == null) continue;
			if (_critterRoot != null && col.transform.IsChildOf(_critterRoot.transform)) continue;
			if (col.transform.IsChildOf(transform)) continue;
			return false;
		}
		return true;
	}

	private void ResetExitFallState(Vector3 groundPosition)
	{
		if (_character == null || _character.data == null) return;
		CharacterData data = _character.data;
		data.dead = false;
		data.zombified = false;
		data.passedOut = false;
		data.fullyPassedOut = false;
		data.fallSeconds = 0f;
		data.deathTimer = 0f;
		data.currentRagdollControll = 1f;
		data.isGrounded = true;
		data.isJumping = false;
		data.sinceGrounded = 0f;
		data.groundPos = groundPosition;
		data.sinceJump = 0f;
		data.avarageVelocity = Vector3.zero;
		data.avarageLastFrameVelocity = Vector3.zero;
	}

	private static void SetCharacterPositionImmediate(Character character, Vector3 position, Quaternion rotation)
	{
		if (character == null || !IsFiniteVector(position)) return;
		UnityEngine.Transform t = character.transform;
		Quaternion oldRotation = t.rotation;
		if (!IsFiniteQuaternion(rotation)) rotation = oldRotation;
		Vector3 oldCenter = character.Center;
		if (!IsFiniteVector(oldCenter)) oldCenter = t.position;
		Quaternion rotationDelta = rotation * Quaternion.Inverse(oldRotation);
		Vector3 delta = position - oldCenter;
		t.SetPositionAndRotation(t.position + delta, rotation);
		if (character.refs?.ragdoll?.partList == null) return;
		foreach (Bodypart part in character.refs.ragdoll.partList)
		{
			if (part == null) continue;
			if (part.Rig != null)
			{
				Vector3 oldPartPosition = part.Rig.position;
				if (!IsFiniteVector(oldPartPosition)) oldPartPosition = oldCenter;
				part.Rig.position = position + rotationDelta * (oldPartPosition - oldCenter);
				part.Rig.rotation = rotationDelta * part.Rig.rotation;
				part.Rig.linearVelocity = Vector3.zero;
				part.Rig.angularVelocity = Vector3.zero;
			}
			else if (part.transform != null)
			{
				Vector3 oldPartPosition = part.transform.position;
				if (!IsFiniteVector(oldPartPosition)) oldPartPosition = oldCenter;
				part.transform.position = position + rotationDelta * (oldPartPosition - oldCenter);
				part.transform.rotation = rotationDelta * part.transform.rotation;
			}
		}
	}

	// ------------------------------------------------------------------
	// Ragdoll no-clip (ghost/tumbleweed recipe)
	// ------------------------------------------------------------------

	private void SetCritterNoClip(bool enable)
	{
		if (_character == null || _character.refs == null || _character.refs.ragdoll == null) return;
		CharacterRagdoll ragdoll = _character.refs.ragdoll;
		try
		{
			if (enable && _character.data != null && _character.data.IsCarryingCharacter)
			{
				AccessTools.Method(typeof(Character), "DropCarriedCharacter")?.Invoke(_character, new object[] { true });
			}
			ragdoll.ToggleKinematic(enable);
			ragdoll.ToggleCollision(!enable);
			if (ragdoll.partList != null)
			{
				foreach (Bodypart part in ragdoll.partList)
				{
					Rigidbody rig = part != null ? part.Rig : null;
					if (rig == null) continue;
					rig.useGravity = !enable;
				}
			}
			SetRagdollInterpolation(enable);
			if (!enable)
			{
				ragdoll.HaltBodyVelocity(false);
			}
		}
		catch (Exception ex)
		{
			LogError("SetCritterNoClip(" + enable + ")", ex);
		}
	}

	private void SetRagdollInterpolation(bool critterEnabled)
	{
		CharacterRagdoll ragdoll = _character != null && _character.refs != null ? _character.refs.ragdoll : null;
		if (ragdoll == null || ragdoll.partList == null) return;

		if (!critterEnabled)
		{
			for (int i = 0; i < _savedInterpolations.Count; i++)
			{
				Rigidbody rig = _savedInterpolations[i].Key;
				if (rig != null)
				{
					try { rig.interpolation = _savedInterpolations[i].Value; } catch { }
				}
			}
			_savedInterpolations.Clear();
			return;
		}

		_savedInterpolations.Clear();
		foreach (Bodypart part in ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null) continue;
			try
			{
				if (rig.interpolation != RigidbodyInterpolation.None)
				{
					_savedInterpolations.Add(new KeyValuePair<Rigidbody, RigidbodyInterpolation>(rig, rig.interpolation));
					rig.interpolation = RigidbodyInterpolation.None;
				}
			}
			catch { }
		}
	}

	// ------------------------------------------------------------------
	// Local visuals (tumbleweed recipe: hide the rider, keep the critter visible)
	// ------------------------------------------------------------------

	private void HideLocalRenderers()
	{
		if (_character == null) return;
		try
		{
			_localRenderers = ((Component)_character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in _localRenderers)
			{
				if (renderer != null) renderer.enabled = false;
			}
		}
		catch (Exception ex)
		{
			LogError("HideLocalRenderers", ex);
		}
	}

	private void KeepLocalRenderersHidden()
	{
		if (_localRenderers == null) return;
		try
		{
			foreach (Renderer renderer in _localRenderers)
			{
				if (renderer != null && renderer.enabled) renderer.enabled = false;
			}
		}
		catch (Exception ex)
		{
			LogError("KeepLocalRenderersHidden", ex);
		}
	}

	private void ForceShowLocalRenderers()
	{
		if (_character == null) return;
		try
		{
			Renderer[] renderers = ((Component)_character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in renderers)
			{
				if (renderer != null) renderer.enabled = true;
			}
		}
		catch (Exception ex)
		{
			LogError("ForceShowLocalRenderers", ex);
		}
	}

	// ------------------------------------------------------------------
	// HUD (shared Transform filter). Critters use their own internal stamina
	// (UseStamina), not the player's — hide the status bar too.
	// ------------------------------------------------------------------

	private void HideHud()
	{
		global::Transform.Core.TransformHud.TickHide(false);
	}

	private void RestoreHud()
	{
		global::Transform.Core.TransformHud.Restore();
	}

	// ------------------------------------------------------------------
	// Camera (tumbleweed recipe, per-kind config)
	// ------------------------------------------------------------------

	private void RefreshCamera()
	{
		try
		{
			Camera camera = Camera.main;
			if (camera == null) return;

			Vector3 critterCenter = _critterRoot != null ? _critterRoot.transform.position : _character.Center;
			Vector3 forward = GetFlatLookDirection();
			Vector3 lookDirection = _character.data != null ? _character.data.lookDirection.normalized : forward;
			float verticalLook = Mathf.Clamp(lookDirection.y, -0.35f, 0.65f);

			Vector3 lookTarget = critterCenter
			                     + Vector3.up * (GetCameraHeight() * 0.5f + verticalLook * 2f)
			                     + forward * (_kind == CritterKind.Coconut ? 4.5f : CameraLookAhead);
			Vector3 desiredPosition = critterCenter
			                          + Vector3.up * (GetCameraHeight() + 0.5f)
			                          - forward * GetCameraDistance();

			if (!_cameraHasSmoothedPosition)
			{
				_cameraSmoothedPosition = desiredPosition;
				_cameraSmoothedRotation = GetCameraRotation(_cameraSmoothedPosition, lookTarget);
				_cameraHasSmoothedPosition = true;
			}
			else
			{
				_cameraSmoothedPosition = Vector3.SmoothDamp(
					_cameraSmoothedPosition, desiredPosition, ref _cameraVelocity, CameraSmoothTime);
				Quaternion desiredRotation = GetCameraRotation(_cameraSmoothedPosition, lookTarget);
				float rotationT = 1f - Mathf.Exp(-CameraRotationSharpness * Time.deltaTime);
				_cameraSmoothedRotation = Quaternion.Slerp(_cameraSmoothedRotation, desiredRotation, rotationT);
			}

			camera.transform.SetPositionAndRotation(_cameraSmoothedPosition, _cameraSmoothedRotation);
			camera.fieldOfView = Mathf.Lerp(camera.fieldOfView, GetCameraFov(), Time.deltaTime * 4f);
		}
		catch (Exception ex)
		{
			LogError("RefreshCamera", ex);
		}
	}

	private static Quaternion GetCameraRotation(Vector3 position, Vector3 lookTarget)
	{
		Vector3 look = lookTarget - position;
		return Quaternion.LookRotation(look.sqrMagnitude > 0.0001f ? look : Vector3.forward, Vector3.up);
	}

	private float GetCameraDistance()
	{
		float value = CritterPlugin.CameraDistance(_kind) != null ? CritterPlugin.CameraDistance(_kind).Value : DefaultCameraDistance;
		return _kind == CritterKind.Coconut ? Mathf.Clamp(value, 5f, 20f) : Mathf.Clamp(value, 2f, 20f);
	}

	private float GetCameraHeight()
	{
		float value = CritterPlugin.CameraHeight(_kind) != null ? CritterPlugin.CameraHeight(_kind).Value : DefaultCameraHeight;
		return _kind == CritterKind.Coconut ? Mathf.Clamp(value, 2.2f, 10f) : Mathf.Clamp(value, 0.3f, 10f);
	}

	private float GetCameraFov()
	{
		float value = CritterPlugin.CameraFov(_kind) != null ? CritterPlugin.CameraFov(_kind).Value : DefaultCameraFov;
		return Mathf.Clamp(value, 60f, 110f);
	}
}

