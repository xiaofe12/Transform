using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using Peak; // game namespace hosting PetrifiedScout

namespace Transform.Statue;

/// <summary>
/// Runtime controller added to the local player's Character while they are a petrified statue.
///
/// The statue is the game's own "PetrifiedScout" prefab (what Character.SpawnStatue creates when
/// a scout petrifies). We spawn it over Photon exactly like the game does and call the prefab's
/// own RPC_SpawnPetrifiedScout so it warps to the player's pose and copies the player's
/// cosmetics on every client, including unmodded ones. The prefab's PhysicsSyncer then
/// replicates the owner's physics to everyone.
///
/// The player's ragdoll is switched to kinematic no-clip (same recipe as the Tumbleweed/Ghost
/// mods) and the character root follows the statue's hip every frame; the local player model is
/// HIDDEN while transformed. The network broadcast of the rider's position is redirected 30 m
/// underground (see StatueHarmonyPatches), which is the best-performing "hide the player"
/// approach across the integrated mods.
///
/// Because Character.localCharacter is never swapped, the vanilla stamina bar keeps working:
/// Shift sprint drains it (and stops when empty), Space hops cost stamina, and standing still
/// regenerates it — mirroring the zombie form's shared-stamina behaviour.
///
/// Controls: WASD pushes the statue (camera-relative), Shift multiplies the force while
/// draining stamina, Space hops when grounded.
/// </summary>
[DefaultExecutionOrder(600)]
public sealed class StatueController : MonoBehaviour
{
	private const float CameraLookAhead = 2f;
	private const float CameraSmoothTime = 0.08f;
	private const float CameraRotationSharpness = 14f;
	private const float DefaultCameraDistance = 6f;
	private const float DefaultCameraHeight = 2.5f;
	private const float DefaultCameraFov = 78f;
	private const float JumpCooldown = 0.35f;
	private const float GroundCheckExtra = 0.45f;
	private const float VoidExitHeight = -80f;
	private const float JumpStaminaCost = 0.1f;
	private const float MinSprintStamina = 0.02f;
	private const float DestroyRestoreDelaySeconds = 1f;
	private const float SpawnStabilizeSeconds = 0.75f;
	private const float DestroyedStateConfirmSeconds = 0.2f;
	private const float RestoreHeightOffset = 0.6f;
	private const float MaxStatueLinearSpeed = 8.5f;
	private const float MaxStatueAngularSpeed = 18f;
	private const float RendererRestorePulseSeconds = 1.25f;

	private Character _character;
	private Vector3 _cameraVelocity;
	private Vector3 _cameraSmoothedPosition;
	private Quaternion _cameraSmoothedRotation;
	private bool _cameraHasSmoothedPosition;

	private GameObject _statueRoot;
	private Rigidbody[] _statueBodies;
	private PetrifiedScout _statueComponent;
	private PhotonView _statueView;
	private float _nextJumpAllowedTime;
	private float _destroyRestoreAt = -1f;
	private float _statueStateStableAt;
	private float _statueMissingSince = -1f;
	private float _statueShatteredSince = -1f;
	private Vector3 _transformEntryRestorePosition;
	private Vector3 _destroyRestoreHoldPosition;
	private Vector3 _lastKnownStatueAnchor;
	private bool _restoreAtTransformEntryOnExit;
	private Renderer[] _localRenderers;
	private Coroutine _rendererRestorePulse;
	private readonly Dictionary<Renderer, bool> _localRendererStates = new Dictionary<Renderer, bool>();
	private readonly List<KeyValuePair<Rigidbody, RigidbodyInterpolation>> _savedInterpolations =
		new List<KeyValuePair<Rigidbody, RigidbodyInterpolation>>();

	/// <summary>True while the form is active.</summary>
	public bool Active { get; private set; }

	/// <summary>The local character currently in statue form, or null.</summary>
	public static Character ActiveStatueCharacter { get; private set; }

	/// <summary>
	/// True once the camera Harmony fallback (a postfix on MainCameraMovement.LateUpdate) has
	/// been applied; while true the camera is driven from that postfix instead of this
	/// controller's own LateUpdate.
	/// </summary>
	internal static bool CameraOverridePatchActive { get; set; }

	/// <summary>Statue riders registered by view id: used by the syncer patches to bury the
	/// rider's broadcast and hide the remote copies while a statue exists.</summary>
	internal static readonly HashSet<int> StatueRiderViewIds = new HashSet<int>();

	internal static bool IsLocalStatueCharacter(Character character)
	{
		return character != null && ActiveStatueCharacter == character;
	}

	private static void LogInfo(string message) => StatuePlugin.Log?.LogInfo("[Statue] " + message);
	private static void LogError(string context, Exception ex) => StatuePlugin.Log?.LogError("[Statue] " + context + ": " + ex);

	/// <summary>Validity gate checked by the module every frame.</summary>
	public bool IsValid()
	{
		if (!Active || _character == null) return false;
		UpdateDestroyedRestoreFromStatueState();
		return true;
	}

	/// <summary>
	/// Scene-switch / end-game safety net: force-exit any active statue form. Runs when the new
	/// scene is already active, so ExitStatue skips the position restore (the old statue is
	/// destroyed) and simply clears state.
	/// </summary>
	internal static void ForceExitForEndGame()
	{
		try
		{
			if (ActiveStatueCharacter != null)
			{
				StatueController ctrl = ((Component)ActiveStatueCharacter).GetComponent<StatueController>();
				if (ctrl != null && ctrl.Active) { ctrl.ExitStatue(); return; }
			}
			foreach (StatueController ctrl in UnityObject.FindObjectsByType<StatueController>(FindObjectsSortMode.None))
			{
				if (ctrl != null && ctrl.Active) { ctrl.ExitStatue(); return; }
			}
		}
		catch (Exception ex)
		{
			LogError("ForceExitForEndGame", ex);
		}
	}

	// ------------------------------------------------------------------
	// Enter / exit
	// ------------------------------------------------------------------

	public void EnterStatue(Character character)
	{
		_character = character;
		_cameraVelocity = Vector3.zero;
		_cameraHasSmoothedPosition = false;
		_nextJumpAllowedTime = 0f;
		StopRendererRestorePulse();
		_destroyRestoreAt = -1f;
		_statueStateStableAt = Time.unscaledTime + SpawnStabilizeSeconds;
		_statueMissingSince = -1f;
		_statueShatteredSince = -1f;
		_restoreAtTransformEntryOnExit = false;
		_transformEntryRestorePosition = character != null ? character.Center : transform.position;
		_destroyRestoreHoldPosition = _transformEntryRestorePosition;
		_lastKnownStatueAnchor = _transformEntryRestorePosition;

		GameObject statue = SpawnStatuePrefab(character);
		if (statue == null)
		{
			LogError("EnterStatue", new InvalidOperationException("PetrifiedScout prefab could not be instantiated."));
			return;
		}

		_statueRoot = statue;
		_statueView = statue.GetComponent<PhotonView>();
		_statueComponent = statue.GetComponent<PetrifiedScout>();
		_statueBodies = statue.GetComponentsInChildren<Rigidbody>(true);

		Active = true;
		ActiveStatueCharacter = character;
		enabled = true;

		if (_character.photonView != null)
		{
			StatueRiderViewIds.Add(_character.photonView.ViewID);
		}

		SetPlayerNoClip(true);
		HideLocalRenderers();
		HideHud();
		LogInfo("Entered statue form.");
	}

	public void ExitStatue()
	{
		if (!Active && _statueRoot == null)
		{
			return;
		}
		Active = false;
		ActiveStatueCharacter = null;
		enabled = false;

		PositionCharacterForExit();
		SetPlayerNoClip(false);
		DestroyStatue();
		RestoreLocalRendererStates(clearState: false);
		StartRendererRestorePulse();
		RestoreHud();
		global::Transform.TransformPlugin.Instance?.BeginPostRestoreRecovery("statue exit");

		if (_character != null && _character.photonView != null)
		{
			StatueRiderViewIds.Remove(_character.photonView.ViewID);
		}
		LogInfo("Exited statue form.");
	}

	private void OnDestroy()
	{
		if (!Active) return;
		try { ExitStatue(); }
		catch (Exception ex) { LogError("OnDestroy", ex); }
	}

	/// <summary>
	/// Spawns the networked PetrifiedScout prefab exactly like the game's Character.SpawnStatue:
	/// PhotonNetwork.Instantiate at the player's ragdoll spawn point, then the prefab's own
	/// RPC_SpawnPetrifiedScout warps it onto the player's bodypart poses and copies the player's
	/// cosmetics on every client (RpcTarget.All). The rider is hidden only AFTER the RPC is out,
	/// so remote clients warp the statue to the above-ground pose before the buried coordinates
	/// start flowing (Photon reliable ordering guarantees this).
	/// Offline / single-player: falls back to a local instantiate (no network pose sync — the
	/// statue spawns at the ragdoll spawn point with the prefab's default pose).
	/// </summary>
	private GameObject SpawnStatuePrefab(Character character)
	{
		try
		{
			UnityEngine.Transform spawnPoint = character.refs != null && character.refs.ragdoll != null
				? character.refs.ragdoll.bodySpawnPoint
				: ((Component)character).transform;
			Vector3 position = spawnPoint != null ? spawnPoint.position : character.Center;
			Quaternion rotation = spawnPoint != null ? spawnPoint.rotation : ((Component)character).transform.rotation;

			if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom)
			{
				// Offline / single-player: local-only statue (mirrors the tumbleweed/ghost/
				// tornado local fallback). No RPC_SpawnPetrifiedScout — no network.
				GameObject prefab = Resources.Load<GameObject>("PetrifiedScout");
				if (prefab == null) prefab = Resources.Load<GameObject>("PhotonPrefabs/PetrifiedScout");
				if (prefab == null)
				{
					LogInfo("Resources.Load(PetrifiedScout) returned null; statue form unavailable offline.");
					return null;
				}
				GameObject local = UnityObject.Instantiate(prefab, position, rotation);
				local.name = "StatueLocal";
				LogInfo("Spawned local PetrifiedScout statue (offline / single-player).");
				return local;
			}

			GameObject statue = PhotonNetwork.Instantiate("PetrifiedScout", position, rotation);
			if (statue == null)
			{
				LogInfo("PhotonNetwork.Instantiate(PetrifiedScout) returned null.");
				return null;
			}

			PhotonView view = statue.GetComponent<PhotonView>();
			if (view != null && character.photonView != null)
			{
				view.RPC("RPC_SpawnPetrifiedScout", RpcTarget.All, character.photonView.ViewID, false);
			}
			return statue;
		}
		catch (Exception ex)
		{
			LogError("SpawnStatuePrefab", ex);
			return null;
		}
	}

	private void DestroyStatue()
	{
		if (_statueRoot == null) return;
		try
		{
			// Offline statue: no networked view — always destroy locally.
			if (PhotonNetwork.InRoom && _statueView != null && _statueView.IsMine)
			{
				PhotonNetwork.Destroy(_statueRoot);
			}
			else
			{
				UnityObject.Destroy(_statueRoot);
			}
		}
		catch (Exception ex)
		{
			LogError("DestroyStatue", ex);
			try { UnityObject.Destroy(_statueRoot); } catch { }
		}
		_statueRoot = null;
		_statueView = null;
		_statueComponent = null;
		_statueBodies = null;
	}

	// ------------------------------------------------------------------
	// Per-frame maintenance
	// ------------------------------------------------------------------

	private void Update()
	{
		if (!Active || _character == null) return;

		try
		{
			ClearNonMovementInput();
			KeepPlayerAlive();
			KeepLocalRenderersHidden();
			HandleStamina();
			HideHud();
			UpdateLastKnownStatueAnchor();
			UpdateDestroyedRestoreFromStatueState();

			if (IsDestroyedRestorePending())
			{
				KeepPlayerAtDestroyRestoreHoldAnchor();
				if (Time.unscaledTime >= _destroyRestoreAt)
				{
					ExitStatue();
				}
				return;
			}

			FollowStatueWithCharacterRoot();
		}
		catch (Exception ex)
		{
			LogError("StatueController.Update", ex);
		}
	}

	private void FixedUpdate()
	{
		if (!Active || _character == null || _statueBodies == null || IsDestroyedRestorePending()) return;
		try
		{
			if (!global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
			{
				DriveStatuePhysics();
			}
			else
			{
				ClampStatueVelocities();
			}
		}
		catch (Exception ex)
		{
			LogError("StatueController.FixedUpdate", ex);
		}
	}

	private bool IsDestroyedRestorePending()
	{
		return _destroyRestoreAt > 0f;
	}

	private void KeepPlayerAtDestroyRestoreHoldAnchor()
	{
		if (_character == null) return;
		Vector3 target = GetDestroyRestoreHoldPosition();
		Vector3 currentCenter = _character.Center;
		if (!IsFiniteVector(currentCenter)) return;
		Vector3 delta = target - currentCenter;
		if (delta.sqrMagnitude <= 0.0001f) return;
		transform.position += delta;
		if (_character.refs?.ragdoll?.partList == null) return;
		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			if (part == null || part.Rig == null) continue;
			part.Rig.position += delta;
			// kinematic 刚体设速度是 no-op 且每帧刷警告，跳过。
			if (!part.Rig.isKinematic)
			{
				part.Rig.linearVelocity = Vector3.zero;
				part.Rig.angularVelocity = Vector3.zero;
			}
		}
	}

	private Vector3 GetRestoreCameraAnchor()
	{
		if (IsDestroyedRestorePending())
		{
			return GetDestroyRestoreHoldPosition() + Vector3.up * 0.9f;
		}
		return transform.position + Vector3.up * 0.9f;
	}

	private Vector3 GetDestroyRestoreHoldPosition()
	{
		if (IsUsableWorldAnchor(_destroyRestoreHoldPosition))
		{
			return _destroyRestoreHoldPosition;
		}
		if (IsUsableWorldAnchor(_lastKnownStatueAnchor))
		{
			return _lastKnownStatueAnchor;
		}
		return IsUsableWorldAnchor(_transformEntryRestorePosition) ? _transformEntryRestorePosition : transform.position;
	}

	private void UpdateDestroyedRestoreFromStatueState()
	{
		if (_statueRoot != null && _statueRoot.transform.position.y < VoidExitHeight)
		{
			ScheduleDestroyedRestore("statue fell out of the world");
			return;
		}

		if (ShouldConfirmDestroyedState(_statueRoot == null, ref _statueMissingSince))
		{
			ScheduleDestroyedRestore("statue object disappeared");
			return;
		}

		bool shattered = _statueComponent != null
		                 && _statueComponent.normalBody != null
		                 && !_statueComponent.normalBody.activeSelf;
		if (ShouldConfirmDestroyedState(shattered, ref _statueShatteredSince))
		{
			ScheduleDestroyedRestore("statue shattered");
		}
	}

	private bool ShouldConfirmDestroyedState(bool detected, ref float detectedSince)
	{
		if (!detected)
		{
			detectedSince = -1f;
			return false;
		}

		float now = Time.unscaledTime;
		if (now < _statueStateStableAt)
		{
			detectedSince = -1f;
			return false;
		}
		if (detectedSince < 0f)
		{
			detectedSince = now;
			return false;
		}
		return now - detectedSince >= DestroyedStateConfirmSeconds;
	}

	/// <summary>
	/// Keeps the player's torso glued to the statue's hip every frame by repositioning the
	/// ragdoll root with the delta. Remotes never see this local transform: the network
	/// broadcast of the rider's position is redirected to a buried spot (see
	/// StatueHarmonyPatches.CharacterSyncerGetDataToWritePostfix).
	/// </summary>
	private void FollowStatueWithCharacterRoot()
	{
		if (_statueRoot == null || _character == null) return;

		UpdateLastKnownStatueAnchor();
		Vector3 anchor = GetStatueAnchor();
		Vector3 currentCenter = _character.Center;
		transform.position += anchor - currentCenter;
	}

	private void UpdateLastKnownStatueAnchor()
	{
		if (_statueRoot == null) return;
		Vector3 anchor = GetStatueAnchor();
		if (IsUsableWorldAnchor(anchor))
		{
			_lastKnownStatueAnchor = anchor;
		}
	}

	private Vector3 GetStatueAnchor()
	{
		if (_statueComponent != null && _statueComponent.hip != null)
		{
			return _statueComponent.hip.position + Vector3.up * 0.9f;
		}
		return _statueRoot != null ? _statueRoot.transform.position : transform.position;
	}

	// ------------------------------------------------------------------
	// HUD: the shared Transform filter hides every HUD element INCLUDING the
	// status bar while transformed (the statue doesn't run on the player's
	// real stamina) and restores them on exit — see Core/TransformHud.cs.
	// It re-checks late-spawning canvases itself.
	// ------------------------------------------------------------------

	private void HideHud()
	{
		Core.TransformHud.TickHide(false);
	}

	private void RestoreHud()
	{
		Core.TransformHud.Restore();
	}

	// ------------------------------------------------------------------
	// Input / survival
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

	// ------------------------------------------------------------------
	// Shared stamina bar: the player Character stays as localCharacter, so the
	// vanilla stamina UI keeps showing its value. Sprint drains it exactly like
	// the game's sprint (stops when empty), hops cost a vanilla-ish jump chunk,
	// and it regenerates while not sprinting (the hidden player's own regen is
	// frozen with the ragdoll, so the controller owns it while transformed).
	// ------------------------------------------------------------------

	private void HandleStamina()
	{
		if (_character == null || _character.data == null) return;
		CharacterData data = _character.data;
		if (!IsSprintHeld() || GetMovementInput().sqrMagnitude < 0.0001f)
		{
			data.currentStamina = Mathf.Min(1f, data.currentStamina + GetStaminaRegen() * Time.deltaTime);
		}
		// The drain itself is applied in DriveStatuePhysics together with the sprint force.
	}

	private static bool IsSprintHeld()
	{
		// Unified menu open: never report sprint so menu clicks don't drain stamina
		// via the raw Shift reads below.
		if (global::TransformState.MenuOpen) return false;
		return Transform.Core.GameInput.SprintHeld(StatuePlugin.SprintKey.Value);
	}

	private static float GetStaminaRegen()
	{
		float value = StatuePlugin.StaminaRegenPerSecond != null ? StatuePlugin.StaminaRegenPerSecond.Value : 0.18f;
		return Mathf.Clamp(value, 0.01f, 1f);
	}

	private static float GetStaminaDrain()
	{
		float value = StatuePlugin.StaminaDrainPerSecond != null ? StatuePlugin.StaminaDrainPerSecond.Value : 0.12f;
		return Mathf.Clamp(value, 0.01f, 1f);
	}

	// ------------------------------------------------------------------
	// Physics driving
	// ------------------------------------------------------------------

	private void DriveStatuePhysics()
	{
		if (_statueBodies == null || _statueBodies.Length == 0) return;

		Vector3 forward = GetFlatLookDirection();
		Vector2 input = GetMovementInput();
		Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
		Vector3 move = forward * input.y + right * input.x;
		if (move.sqrMagnitude > 1f) move.Normalize();

		bool wantsSprint = IsSprintHeld() && move.sqrMagnitude > 0.0001f;
		bool canSprint = wantsSprint
			&& _character != null
			&& _character.data != null
			&& _character.data.currentStamina > MinSprintStamina;

		float multiplier = canSprint
			? Mathf.Max(1f, StatuePlugin.SprintMultiplier != null ? StatuePlugin.SprintMultiplier.Value : 1.8f)
			: 1f;
		float force = (StatuePlugin.MovementForce != null ? StatuePlugin.MovementForce.Value : 26f) * multiplier;

		if (move.sqrMagnitude > 0.0001f)
		{
			// Uniform acceleration on every rigidbody: the ragdoll moves as one piece
			// without stretching its joints (mass-independent, like the game's own
			// movement force application).
			Vector3 accel = move * force;
			for (int i = 0; i < _statueBodies.Length; i++)
			{
				Rigidbody rig = _statueBodies[i];
				if (rig != null && !rig.isKinematic)
				{
					rig.AddForce(accel, ForceMode.Acceleration);
				}
			}
		}

		if (canSprint && _character.data != null)
		{
			_character.data.currentStamina = Mathf.Max(0f, _character.data.currentStamina - GetStaminaDrain() * Time.fixedDeltaTime);
		}

		// Unified menu open: freeze the raw Space hop below so menu clicks never
		// jump the statue. WASD movement is already zeroed natively — the menu sets
		// GUIManager.windowBlockingInput, which makes Character.CanDoInput() false and
		// CharacterInput.Sample() reset movementInput.
		if (global::TransformState.MenuOpen || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
		{
			ClampStatueVelocities();
			return;
		}

		bool jumpPressed = Transform.Core.GameInput.JumpPressed(StatuePlugin.JumpKey.Value);
		if (jumpPressed
			&& Time.time >= _nextJumpAllowedTime
			&& IsStatueGrounded()
			&& _character.data != null
			&& _character.data.currentStamina >= JumpStaminaCost)
		{
			float jumpSpeed = StatuePlugin.JumpSpeed != null ? StatuePlugin.JumpSpeed.Value : 11f;
			for (int i = 0; i < _statueBodies.Length; i++)
			{
				Rigidbody rig = _statueBodies[i];
				if (rig == null || rig.isKinematic) continue;
				Vector3 velocity = rig.linearVelocity;
				rig.linearVelocity = new Vector3(velocity.x, Mathf.Max(velocity.y, jumpSpeed), velocity.z);
			}
			_character.data.currentStamina = Mathf.Max(0f, _character.data.currentStamina - JumpStaminaCost);
			_nextJumpAllowedTime = Time.time + JumpCooldown;
		}

		ClampStatueVelocities();
	}

	private void ClampStatueVelocities()
	{
		if (_statueBodies == null) return;
		for (int i = 0; i < _statueBodies.Length; i++)
		{
			Rigidbody rig = _statueBodies[i];
			if (rig == null || rig.isKinematic) continue;
			Vector3 velocity = rig.linearVelocity;
			Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
			if (flatVelocity.magnitude > MaxStatueLinearSpeed)
			{
				flatVelocity = flatVelocity.normalized * MaxStatueLinearSpeed;
				rig.linearVelocity = flatVelocity + Vector3.up * velocity.y;
			}
			rig.angularVelocity = Vector3.ClampMagnitude(rig.angularVelocity, MaxStatueAngularSpeed);
		}
	}

	private bool IsStatueGrounded()
	{
		if (_statueRoot == null) return false;
		float radius = GetStatueBoundsRadius();
		Vector3 origin = GetStatueAnchor();
		return Physics.Raycast(
			origin,
			Vector3.down,
			radius + GroundCheckExtra,
			Physics.DefaultRaycastLayers,
			QueryTriggerInteraction.Ignore);
	}

	private float GetStatueBoundsRadius()
	{
		try
		{
			Bounds bounds = default;
			bool hasBounds = false;
			foreach (Collider collider in _statueRoot.GetComponentsInChildren<Collider>())
			{
				if (collider == null || collider.isTrigger) continue;
				if (!hasBounds) { bounds = collider.bounds; hasBounds = true; }
				else bounds.Encapsulate(collider.bounds);
			}
			if (hasBounds) return Mathf.Max(0.3f, bounds.extents.magnitude * 0.6f);
		}
		catch { }
		return 0.6f;
	}

	private Vector2 GetMovementInput()
	{
		if (_character == null || _character.input == null) return Vector2.zero;
		Vector2 raw = _character.input.movementInput;
		if (raw.sqrMagnitude <= 0.0001f) raw = Transform.Core.GameInput.Move();
		raw.x = Mathf.Clamp(raw.x, -1f, 1f);
		raw.y = Mathf.Clamp(raw.y, -1f, 1f);
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

	// ------------------------------------------------------------------
	// Exit positioning: put the player back on solid ground before the
	// ragdoll physics and colliders are re-enabled.
	// ------------------------------------------------------------------

	private void PositionCharacterForExit()
	{
		if (_character == null) return;
		try
		{
			bool lockToTransformEntry = ShouldRestoreAtTransformEntryPosition();
			Vector3 start = lockToTransformEntry
				? _transformEntryRestorePosition
				: (_statueRoot != null ? GetStatueAnchor() : transform.position);
			Vector3 target = ResolveSimpleRestorePosition(start);
			MoveCharacterToExit(target);
			ResetExitFallState(target);
			LogInfo("Repositioned player to restore spot: " + target);
		}
		catch (Exception ex)
		{
			LogError("PositionCharacterForExit", ex);
		}
	}

	private Vector3 ResolveSimpleRestorePosition(Vector3 start)
	{
		if (!IsFiniteVector(start) || start.sqrMagnitude <= 0.0001f)
		{
			start = transform.position;
		}
		return start + Vector3.up * RestoreHeightOffset;
	}

	private void ScheduleDestroyedRestore(string reason)
	{
		if (!Active) return;
		_restoreAtTransformEntryOnExit = true;
		if (_destroyRestoreAt > 0f) return;
		_destroyRestoreHoldPosition = GetCurrentStatueOrCharacterAnchor();
		_cameraVelocity = Vector3.zero;
		_cameraHasSmoothedPosition = false;
		_destroyRestoreAt = Time.unscaledTime + DestroyRestoreDelaySeconds;
		LogInfo(reason + "; holding at " + _destroyRestoreHoldPosition + " for 1 second, then restoring player at transform position.");
	}

	private Vector3 GetCurrentStatueOrCharacterAnchor()
	{
		if (_statueRoot != null)
		{
			Vector3 anchor = GetStatueAnchor();
			if (IsUsableWorldAnchor(anchor)) return anchor;
		}
		if (IsUsableWorldAnchor(_lastKnownStatueAnchor)) return _lastKnownStatueAnchor;
		if (_character != null && IsUsableWorldAnchor(_character.Center)) return _character.Center;
		return IsUsableWorldAnchor(_transformEntryRestorePosition) ? _transformEntryRestorePosition : transform.position;
	}

	private bool ShouldRestoreAtTransformEntryPosition()
	{
		return _restoreAtTransformEntryOnExit
		       && IsFiniteVector(_transformEntryRestorePosition)
		       && _transformEntryRestorePosition.sqrMagnitude > 0.0001f;
	}

	private static bool IsFiniteVector(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
		       && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
		       && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}

	private static bool IsUsableWorldAnchor(Vector3 value)
	{
		return IsFiniteVector(value) && value.sqrMagnitude > 0.0001f && value.y > VoidExitHeight + 2f;
	}

	private void MoveCharacterToExit(Vector3 target)
	{
		if (_character == null) return;
		Vector3 currentCenter = _character.Center;
		Vector3 delta = target - currentCenter;
		transform.position += delta;
		if (_character.refs?.ragdoll?.partList == null) return;
		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			if (part == null) continue;
			if (part.Rig != null)
			{
				part.Rig.position += delta;
				// 定位前刚体仍为 kinematic，设速度只刷警告。
				if (!part.Rig.isKinematic)
				{
					part.Rig.linearVelocity = Vector3.zero;
					part.Rig.angularVelocity = Vector3.zero;
				}
			}
			else if (part.transform != null)
			{
				part.transform.position += delta;
			}
		}
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
		data.currentRagdollControll = 1f;
		data.isGrounded = true;
		data.isJumping = false;
		data.sinceGrounded = 0f;
		data.groundPos = groundPosition;
		data.sinceJump = 0f;
		data.avarageVelocity = Vector3.zero;
		data.avarageLastFrameVelocity = Vector3.zero;
	}

	// ------------------------------------------------------------------
	// Ragdoll no-clip: kinematic parts, colliders off, gravity off,
	// interpolation off (same recipe as the Tumbleweed/Ghost mods).
	// ------------------------------------------------------------------

	private void SetPlayerNoClip(bool enable)
	{
		if (_character == null || _character.refs == null || _character.refs.ragdoll == null) return;

		CharacterRagdoll ragdoll = _character.refs.ragdoll;
		try
		{
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
			LogError("SetPlayerNoClip(" + enable + ")", ex);
		}
	}

	private void SetRagdollInterpolation(bool noClipEnabled)
	{
		CharacterRagdoll ragdoll = _character != null && _character.refs != null ? _character.refs.ragdoll : null;
		if (ragdoll == null || ragdoll.partList == null) return;

		if (!noClipEnabled)
		{
			for (int i = 0; i < _savedInterpolations.Count; i++)
			{
				Rigidbody rig = _savedInterpolations[i].Key;
				if (rig != null)
				{
					try { rig.interpolation = _savedInterpolations[i].Value; }
					catch { }
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
	// Local visuals: hidden while transformed so the local view matches
	// remote clients; original renderer states are restored on exit.
	// ------------------------------------------------------------------

	private void HideLocalRenderers()
	{
		if (_character == null) return;
		try
		{
			_localRendererStates.Clear();
			_localRenderers = ((Component)_character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in _localRenderers)
			{
				if (renderer == null) continue;
				_localRendererStates[renderer] = renderer.enabled;
				renderer.enabled = false;
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

	private void RestoreLocalRendererStates(bool clearState)
	{
		if (_character == null) return;
		try
		{
			Renderer[] renderers = _localRenderers ?? ((Component)_character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in renderers)
			{
				if (renderer == null) continue;
				if (_localRendererStates.TryGetValue(renderer, out bool wasEnabled))
				{
					renderer.enabled = wasEnabled;
				}
			}
			if (clearState)
			{
				_localRendererStates.Clear();
				_localRenderers = null;
			}
		}
		catch (Exception ex)
		{
			LogError("RestoreLocalRendererStates", ex);
		}
	}

	private void StartRendererRestorePulse()
	{
		StopRendererRestorePulse();
		if (gameObject != null && gameObject.activeInHierarchy)
		{
			_rendererRestorePulse = StartCoroutine(RendererRestorePulseRoutine());
			return;
		}
		RestoreLocalRendererStates(clearState: true);
	}

	private void StopRendererRestorePulse()
	{
		if (_rendererRestorePulse == null) return;
		try { StopCoroutine(_rendererRestorePulse); } catch { }
		_rendererRestorePulse = null;
	}

	private IEnumerator RendererRestorePulseRoutine()
	{
		float until = Time.unscaledTime + RendererRestorePulseSeconds;
		while (_character != null && Time.unscaledTime <= until)
		{
			RestoreLocalRendererStates(clearState: false);
			yield return null;
		}
		RestoreLocalRendererStates(clearState: true);
		_rendererRestorePulse = null;
	}

	// ------------------------------------------------------------------
	// Camera: driven either from the Harmony postfix on
	// MainCameraMovement.LateUpdate or from this controller's LateUpdate.
	// ------------------------------------------------------------------

	internal static void ApplyCameraOverrideForLocalStatue()
	{
		Character character = ActiveStatueCharacter;
		if (character == null) return;
		StatueController controller = ((Component)character).GetComponent<StatueController>();
		if (controller != null && controller.IsValid())
		{
			controller.RefreshCamera();
		}
	}

	private void RefreshCamera()
	{
		// 外部自由相机（PeakSpectatorMode / PeakCinema）激活期间让路，避免双方逐帧互相覆盖相机。
		if (global::Transform.Core.ThirdPartyCameras.ExternalCameraActive) return;

		try
		{
			Camera camera = Camera.main;
			if (camera == null) return;

			Vector3 statueCenter = IsDestroyedRestorePending() ? GetRestoreCameraAnchor() : GetStatueAnchor();
			Vector3 forward = GetFlatLookDirection();
			Vector3 lookDirection = _character.data != null ? _character.data.lookDirection.normalized : forward;
			float verticalLook = Mathf.Clamp(lookDirection.y, -0.35f, 0.65f);

			Vector3 lookTarget = statueCenter
			                     + Vector3.up * (GetCameraHeight() * 0.5f + verticalLook * 1.5f)
			                     + forward * CameraLookAhead;
			Vector3 desiredPosition = statueCenter
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
					_cameraSmoothedPosition,
					desiredPosition,
					ref _cameraVelocity,
					CameraSmoothTime);
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

	private void LateUpdate()
	{
		if (!CameraOverridePatchActive && Active && IsValid())
		{
			RefreshCamera();
		}
	}

	private static float GetCameraDistance()
	{
		float value = StatuePlugin.CameraDistance != null ? StatuePlugin.CameraDistance.Value : DefaultCameraDistance;
		return Mathf.Clamp(value, 3f, 15f);
	}

	private static float GetCameraHeight()
	{
		float value = StatuePlugin.CameraHeight != null ? StatuePlugin.CameraHeight.Value : DefaultCameraHeight;
		return Mathf.Clamp(value, 1f, 8f);
	}

	private static float GetCameraFov()
	{
		float value = StatuePlugin.CameraFov != null ? StatuePlugin.CameraFov.Value : DefaultCameraFov;
		return Mathf.Clamp(value, 60f, 110f);
	}

	private static Quaternion GetCameraRotation(Vector3 cameraPosition, Vector3 lookTarget)
	{
		Vector3 lookDirection = lookTarget - cameraPosition;
		if (lookDirection.sqrMagnitude < 0.0001f) lookDirection = Vector3.forward;
		return Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
	}
}
