using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ImTumbleweed;

[DefaultExecutionOrder(600)]
public sealed class TumbleweedController : MonoBehaviour
{
	private const float CameraLookAhead = 3f;
	private const float CameraSmoothTime = 0.08f;
	private const float CameraRotationSharpness = 12f;
	private const float DefaultCameraDistance = 12f;
	private const float DefaultCameraHeight = 4f;
	private const float DefaultCameraFov = 80f;
	private const float JumpCooldown = 0.25f;
	private const float GroundCheckExtra = 0.35f;
	private const float VoidExitHeight = -80f;
	private const byte NetworkSyncEventCode = 198;
	private const string NetworkSyncMagic = "ImTumbleweed.Sync";
	private const float NetworkSyncInterval = 0.05f;
	private const float RestoreHeightOffset = 0.6f;
	/// <summary>Mouse-down input is sampled in Update and consumed in FixedUpdate. Reading
	/// GetMouseButtonDown directly in FixedUpdate can miss short clicks when render and
	/// physics frames do not line up.</summary>
	private const float DashInputBufferSeconds = 0.25f;

	private Character _character;
	private Vector3 _prevCenter;
	private Vector3 _cameraVelocity;
	private Vector3 _cameraSmoothedPosition;
	private Quaternion _cameraSmoothedRotation;
	private bool _cameraHasSmoothedPosition;

	private GameObject _weedRoot;
	private Rigidbody _weedRigidbody;
	private SphereCollider _weedCollider;
	private bool _weedIsNetworked;
	private float _nextJumpAllowedTime;
	private float _nextDashAllowedTime;
	private float _dashPressedTime = -10f;
	private float _activeSeconds;
	private float _nextNetworkSyncTime;

	internal const string NetworkVisualMarker = "ImTumbleweed.Visual";

	/// <summary>The local character currently in tumbleweed form, or null.</summary>
	public static Character ActiveWeedCharacter { get; private set; }

	/// <summary>
	/// True once the camera Harmony fallback (a postfix on MainCameraMovement.LateUpdate)
	/// has been applied; while true the camera is driven from that postfix instead of this
	/// controller's own LateUpdate.
	/// </summary>
	internal static bool CameraOverridePatchActive { get; set; }

	// ------------------------------------------------------------------
	// Weed-rider registry: maps a weed rider's Character PhotonView id to the
	// live weed GameObject. Used so remote (modded) clients can pin the rider to
	// the weed centre, and so the syncer's normal interpolation is skipped for
	// that character (the pin owns its position instead). The local rider's own
	// transform stays glued to the weed centre for camera/visibility; only the
	// network broadcast is redirected to a buried position (see the Harmony
	// GetDataToWrite postfix) so unmodded clients never receive a spot inside
	// the live weed ball - which their physics would otherwise push back out.
	// ------------------------------------------------------------------

	internal static readonly Dictionary<int, GameObject> WeedRootByCharacter = new Dictionary<int, GameObject>();

	internal static void RegisterWeedCharacter(int characterViewId, GameObject weedRoot)
	{
		if (characterViewId <= 0 || weedRoot == null)
		{
			return;
		}
		WeedRootByCharacter[characterViewId] = weedRoot;
	}

	internal static bool TryGetWeedForCharacter(int characterViewId, out GameObject weedRoot)
	{
		if (WeedRootByCharacter.TryGetValue(characterViewId, out weedRoot) && weedRoot != null && weedRoot.activeInHierarchy)
		{
			return true;
		}
		WeedRootByCharacter.Remove(characterViewId);
		weedRoot = null;
		return false;
	}

	internal static void UnregisterWeedCharacter(int characterViewId)
	{
		WeedRootByCharacter.Remove(characterViewId);
	}

	/// <summary>
	/// On a remote (modded) client, hide the weed rider's own character so the remote player
	/// only sees the tumbleweed rolling - matching the local player, which is also hidden while
	/// transformed. Visibility is restored via ShowRemoteRider when the owner exits and the
	/// weed mapping self-cleans (see TryGetWeedForCharacter).
	/// </summary>
	internal static void HideRemoteRider(Character character)
	{
		SetCharacterRenderersEnabled(character, false);
	}

	internal static void ShowRemoteRider(Character character)
	{
		SetCharacterRenderersEnabled(character, true);
	}

	private static void SetCharacterRenderersEnabled(Character character, bool enabled)
	{
		if (character == null)
		{
			return;
		}
		try
		{
			Renderer[] renderers = ((Component)character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in renderers)
			{
				if (renderer != null)
				{
					renderer.enabled = enabled;
				}
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.SetCharacterRenderersEnabled", ex);
		}
	}

	// Ragdoll rigidbodies whose interpolation we disabled while transformed, mapped to
	// their original setting so SetWeedNoClip(false) can restore them exactly.
	private readonly List<KeyValuePair<Rigidbody, RigidbodyInterpolation>> _savedInterpolations =
		new List<KeyValuePair<Rigidbody, RigidbodyInterpolation>>();

	public bool Active { get; private set; }

	public bool IsValid()
	{
		if (!Active || _character == null || _character.data == null)
		{
			return false;
		}
		// Mid-renewal the weed root is briefly null (destroyed this frame, respawned on
		// the next), and the player is still in control - don't let the plugin's validity
		// check force-exit the form during the swap.
		if (_pendingRenewal)
		{
			return true;
		}
		if (_weedRoot == null || _weedRigidbody == null)
		{
			return false;
		}
		return !_character.data.dead && !_character.data.fullyPassedOut;
	}

	private static void LogInfo(string message) => TumbleweedPlugin.Log?.LogInfo("[I'm a Tumbleweed] " + message);
	private static void LogError(string entryPoint, Exception ex) => TumbleweedPlugin.Log?.LogError("[I'm a Tumbleweed] " + entryPoint + ": " + ex);

	/// <summary>True while the local character is a tumbleweed driven by this controller.</summary>
	public static bool IsLocalWeedCharacter(Character character)
	{
		if (character == null || !character.IsLocal)
		{
			return false;
		}
		TumbleweedController controller = ((Component)character).GetComponent<TumbleweedController>();
		return controller != null && controller.Active;
	}

	/// <summary>
	/// Reverts the tumbleweed form immediately. Called from the vanilla Character.RPCEndGame /
	/// GameOverHandler.BeginAirportLoadRPC / EndScreen.ReturnToAirport prefixes BEFORE the
	/// end-game iterates characters or the Airport scene loads. If the local player is
	/// transformed we revert to normal first so:
	///  - the win/lose stats and badge unlocking apply to the real player (which keeps its valid
	///    badgeStatus/timelineInfo while in weed form - we never swap in a fake character);
	///  - the player is not left stuck in tumbleweed form when the Airport scene initializes
	///    (Character.localCharacter would otherwise point at a form that can no longer move,
	///    breaking plane boarding).
	/// ExitWeed also destroys the networked weed so it disappears on every client.
	///
	/// Unlike the Zombie mod there is no swap of Character.localCharacter: the controller lives on
	/// the player's own GameObject and ActiveWeedCharacter IS that player, so look the controller up
	/// there. The fallback scans the scene for any active controller (covers a mangled reference).
	/// </summary>
	internal static void ForceExitForEndGame()
	{
		try
		{
			if (ActiveWeedCharacter != null)
			{
				TumbleweedController ctrl = ((Component)ActiveWeedCharacter).GetComponent<TumbleweedController>();
				if (ctrl != null && ctrl.Active) { ctrl.ExitWeed(); return; }
			}
			foreach (TumbleweedController ctrl in UnityEngine.Object.FindObjectsByType<TumbleweedController>(FindObjectsSortMode.None))
			{
				if (ctrl != null && ctrl.Active) { ctrl.ExitWeed(); return; }
			}
		}
		catch (Exception ex) { LogError("ForceExitForEndGame", ex); }
	}

	public void EnterWeed(Character character)
	{
		_character = character;
		_prevCenter = character.Center;
		_cameraVelocity = Vector3.zero;
		_cameraHasSmoothedPosition = false;
		_nextJumpAllowedTime = 0f;
		_nextDashAllowedTime = 0f;
		_dashPressedTime = -10f;
		_activeSeconds = 0f;
		_pendingRenewal = false;
		float renewInterval = GetRenewInterval();
		_nextRenewTime = renewInterval > 0f ? Time.time + renewInterval : float.MaxValue;
		Active = true;
		ActiveWeedCharacter = character;
		enabled = true;
		SetWeedNoClip(true);
		BuildWeedVisual();
		// Register the rider -> weed mapping so remote (modded) clients can pin the rider
		// to the weed centre and skip the syncer's normal interpolation for it.
		RegisterWeedCharacter(_character.photonView.ViewID, _weedRoot);
		// The local player model is hidden while transformed so the local view matches
		// remote clients (which receive a buried coordinate and never see the rider on the
		// ball): the camera follows the weed and the user just sees the tumbleweed rolling.
		// ForceShowLocalRenderers() restores the player renderers to their original states.
		HideLocalRenderers();
		HideHud();
		LogInfo("Entered tumbleweed form.");
	}

	public void ExitWeed()
	{
		if (!Active && _weedRoot == null)
		{
			return;
		}
		Active = false;
		ActiveWeedCharacter = null;
		enabled = false;
		// Put the player back on the ground before physics/colliders come back, so a weed
		// that rolled into a wall, a gap or underground never leaves the player stuck
		// inside geometry after reverting (the ragdoll is still kinematic & collision-free
		// at this point, so moving the root is safe).
		PositionCharacterForExit();
		SetWeedNoClip(false);
		DestroyWeedVisual();
		ForceShowLocalRenderers();
		_localRenderers = null;
		RestoreHud();
		LogInfo("Exited tumbleweed form.");
	}

	private void Update()
	{
		if (!Active || _character == null)
		{
			return;
		}

		try
		{
			// Auto-revert: the vanilla weed self-destructs after ~15s on unmodded clients
			// (RemoveAfterSeconds, which we cannot disable there), so end the form at the
			// same point instead of leaving unmodded clients staring at an invisible weed.
			_activeSeconds += Time.deltaTime;
			if (TumbleweedPlugin.AutoRevertSeconds != null
			    && TumbleweedPlugin.AutoRevertSeconds.Value > 0f
			    && _activeSeconds >= TumbleweedPlugin.AutoRevertSeconds.Value)
			{
				LogInfo("Tumbleweed form auto-reverted after " + TumbleweedPlugin.AutoRevertSeconds.Value.ToString("0.#") + " seconds.");
				ExitWeed();
				return;
			}

			ClearNonMovementInput();
			KeepPlayerAlive();
			MaybeRenewWeed();
			UpdateWeedVisual();
			KeepLocalRenderersHidden();
			HideHud();
			if (!global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
			{
				BufferDashInput();
			}
			else
			{
				ConsumeDashInput();
			}

			// Safety net: if the weed somehow rolls out of the world, exit so the vanilla
			// warp/recovery logic can take over again (we block Warps while transformed).
			if (_weedRoot != null && _weedRoot.transform.position.y < VoidExitHeight)
			{
				LogInfo("Tumbleweed fell out of the world; exiting form.");
				ExitWeed();
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.Update", ex);
		}
	}

	private void FixedUpdate()
	{
		if (!Active || _character == null)
		{
			return;
		}

		try
		{
			// Follow on the physics frame (same recipe as the I'm a Ghost mod): writing
			// the character root transform here keeps it in sync with the weed's physics
			// body and is not overwritten by the game's animation/movement systems that
			// run on the render frame.
			FollowWeedWithCharacterRoot();
			DriveWeedPhysics();
			SendWeedNetworkSync();
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.FixedUpdate", ex);
		}
	}

	private void SendWeedNetworkSync()
	{
		if (!_weedIsNetworked || _weedRoot == null || _weedRigidbody == null || !PhotonNetwork.InRoom) return;
		if (Time.unscaledTime < _nextNetworkSyncTime) return;
		_nextNetworkSyncTime = Time.unscaledTime + NetworkSyncInterval;
		try
		{
			PhotonView view = _weedRoot.GetComponent<PhotonView>();
			if (view == null || !view.IsMine) return;
			PhotonNetwork.RaiseEvent(NetworkSyncEventCode,
				new object[]
				{
					NetworkSyncMagic,
					view.ViewID,
					_weedRigidbody.position,
					_weedRigidbody.rotation,
					_weedRigidbody.linearVelocity,
					_weedRigidbody.angularVelocity
				},
				new RaiseEventOptions { Receivers = ReceiverGroup.Others },
				new SendOptions { Reliability = false });
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.SendWeedNetworkSync", ex);
		}
	}

	private void LateUpdate()
	{
		if (!Active || _character == null)
		{
			return;
		}

		try
		{
			SyncCharacterData();
			if (!CameraOverridePatchActive)
			{
				RefreshCamera();
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.LateUpdate", ex);
		}
	}

	private void OnDestroy()
	{
		if (Active)
		{
			Active = false;
			ActiveWeedCharacter = null;
			SetWeedNoClip(false);
			DestroyWeedVisual();
			ForceShowLocalRenderers();
			RestoreHud();
		}
	}

	// ------------------------------------------------------------------
	// Physics: WASD rolling force, sprint multiplier, grounded hop.
	// ------------------------------------------------------------------

	private void DriveWeedPhysics()
	{
		if (_weedRigidbody == null)
		{
			return;
		}

		// Unified menu open: freeze the raw sprint/jump key reads below so menu
		// clicks never leak into the form. WASD movement is already zeroed
		// natively — the menu sets GUIManager.windowBlockingInput, which makes
		// Character.CanDoInput() false and CharacterInput.Sample() reset movementInput.
		if (global::TransformState.MenuOpen || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
		{
			return;
		}

		Vector3 forward = GetFlatLookDirection();
		Vector2 input = GetMovementInput();
		Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
		Vector3 move = forward * input.y + right * input.x;
		if (move.sqrMagnitude > 1f)
		{
			move.Normalize();
		}

		bool sprinting = Transform.Core.GameInput.SprintHeld(TumbleweedPlugin.SprintKey.Value);
		float multiplier = sprinting ? Mathf.Max(1f, TumbleweedPlugin.SprintMultiplier.Value) : 1f;
		float force = TumbleweedPlugin.MovementForce.Value * multiplier;
		float speedCap = TumbleweedPlugin.MaxSpeed.Value * multiplier;

		if (move.sqrMagnitude > 0.0001f)
		{
			Vector3 velocity = _weedRigidbody.linearVelocity;
			Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
			if (flatVelocity.magnitude < speedCap)
			{
				_weedRigidbody.AddForce(move * force, ForceMode.Acceleration);
			}
		}

		if (Transform.Core.GameInput.JumpPressed(TumbleweedPlugin.JumpKey.Value)
		    && Time.time >= _nextJumpAllowedTime
		    && IsWeedGrounded())
		{
			Vector3 velocity = _weedRigidbody.linearVelocity;
			_weedRigidbody.linearVelocity = new Vector3(
				velocity.x,
				Mathf.Max(velocity.y, TumbleweedPlugin.JumpSpeed.Value),
				velocity.z);
			_nextJumpAllowedTime = Time.time + JumpCooldown;
		}

		// RMB: quick dash toward the view direction (unified scheme — right-click is the
		// form's special ability; for the tumbleweed that's a burst of speed).
		if (HasBufferedDash() && Time.time >= _nextDashAllowedTime)
		{
			ConsumeDashInput();
			Vector3 velocity = _weedRigidbody.linearVelocity;
			Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
			Vector3 dashVelocity = forward * TumbleweedPlugin.DashForce.Value;
			_weedRigidbody.linearVelocity = flatVelocity + dashVelocity + Vector3.up * velocity.y;
			_nextDashAllowedTime = Time.time + Mathf.Max(0.2f, TumbleweedPlugin.DashCooldown.Value);
		}
	}

	private bool IsWeedGrounded()
	{
		if (_weedRoot == null)
		{
			return false;
		}
		float radius = GetWeedWorldRadius();
		return Physics.Raycast(
			_weedRoot.transform.position,
			Vector3.down,
			radius + GroundCheckExtra,
			Physics.DefaultRaycastLayers,
			QueryTriggerInteraction.Ignore);
	}

	private float GetWeedWorldRadius()
	{
		try
		{
			if (_weedCollider != null)
			{
				return Mathf.Max(0.1f, _weedCollider.bounds.extents.x);
			}
		}
		catch
		{
		}
		return 0.5f;
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
	// Input / survival
	// ------------------------------------------------------------------

	private void ClearNonMovementInput()
	{
		if (_character == null || _character.input == null)
		{
			return;
		}
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

	private void BufferDashInput()
	{
		if (global::TransformState.MenuOpen || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl) return;
		if (Transform.Core.GameInput.UseSecondaryPressed(KeyCode.Mouse1))
		{
			_dashPressedTime = Time.time;
		}
	}

	private bool HasBufferedDash()
	{
		return Time.time - _dashPressedTime <= DashInputBufferSeconds;
	}

	private void ConsumeDashInput()
	{
		_dashPressedTime = -10f;
	}

	private void KeepPlayerAlive()
	{
		if (_character == null || _character.data == null)
		{
			return;
		}

		_character.data.dead = false;
		_character.data.zombified = false;
		_character.data.passedOut = false;
		_character.data.fullyPassedOut = false;
		_character.data.fallSeconds = 0f;
		_character.data.isSprinting = false;
	}

	private Vector2 GetMovementInput()
	{
		if (_character == null || _character.input == null)
		{
			return Vector2.zero;
		}
		Vector2 raw = _character.input.movementInput;
		if (raw.sqrMagnitude <= 0.0001f) raw = Transform.Core.GameInput.Move();
		raw.x = Mathf.Clamp(raw.x, -1f, 1f);
		raw.y = Mathf.Clamp(raw.y, -1f, 1f);
		return raw;
	}

	private void SyncCharacterData()
	{
		if (_character == null || _character.data == null)
		{
			return;
		}

		Vector3 center = _character.Center;
		Vector3 velocity = _prevCenter.sqrMagnitude > 0f ? (center - _prevCenter) / Mathf.Max(Time.deltaTime, 0.0001f) : Vector3.zero;
		_prevCenter = center;

		_character.data.avarageLastFrameVelocity = _character.data.avarageVelocity;
		_character.data.avarageVelocity = velocity;
		_character.data.worldMovementInput = _character.data.worldMovementInput_Grounded = Vector3.zero;
		_character.data.sinceGrounded = Mathf.Min(_character.data.sinceGrounded, 0.1f);
	}

	private void FollowWeedWithCharacterRoot()
	{
		if (_weedRoot == null || _character == null)
		{
			return;
		}

	// Keep the player's torso (character.Center) glued to the weed's center every
	// frame by repositioning the ragdoll root with the delta - recomputed live so a
	// ragdoll pose reshaped by the game while kinematic (isKinematic) never drifts the
	// player out of the ball's middle. Remotes never see this local transform: the
	// network broadcast of the rider's position is redirected to a buried spot (see the
	// CharacterSyncer.GetDataToWrite postfix), so unmodded clients never receive a
	// coordinate inside the live weed ball.
	Vector3 weedCenter = _weedRoot.transform.position;
		Vector3 currentCenter = _character.Center;
		transform.position += weedCenter - currentCenter;
	}

	// ------------------------------------------------------------------
	// Exit positioning: put the player back on solid ground before the
	// ragdoll physics and colliders are re-enabled, so reverting never
	// leaves them inside a wall or below the terrain.
	// ------------------------------------------------------------------

	private void PositionCharacterForExit()
	{
		if (_character == null)
		{
			return;
		}

		try
		{
			Vector3 start = _weedRoot != null ? _weedRoot.transform.position : transform.position;
			Vector3 target = ResolveSimpleRestorePosition(start);
			MoveCharacterToExit(target);
			ResetExitFallState(target);
			LogInfo("Repositioned player to restore spot: " + target);
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.PositionCharacterForExit", ex);
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

	private static bool IsFiniteVector(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
		       && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
		       && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
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
				// 定位前刚体仍可能 kinematic，清零只在非 kinematic 时执行（避免 Unity 警告）。
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
	// interpolation off (same recipe as the I'm a Ghost mod).
	// ------------------------------------------------------------------

	private void SetWeedNoClip(bool enable)
	{
		if (_character == null || _character.refs == null || _character.refs.ragdoll == null)
		{
			return;
		}

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
					if (rig == null)
					{
						continue;
					}
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
			LogError("TumbleweedController.SetWeedNoClip(" + enable + ")", ex);
		}
	}

	/// <summary>
	/// The game creates every character ragdoll rigidbody with
	/// RigidbodyInterpolation.Interpolate, while kinematic parts are driven by writing their
	/// transforms directly - interpolated rigidbodies whose transform is written directly
	/// jitter (see the I'm a Ghost 2.02.a fix). While transformed we therefore disable
	/// interpolation on all ragdoll bodies and restore the original values on exit.
	/// </summary>
	private void SetRagdollInterpolation(bool weedEnabled)
	{
		CharacterRagdoll ragdoll = _character != null && _character.refs != null ? _character.refs.ragdoll : null;
		if (ragdoll == null || ragdoll.partList == null)
		{
			return;
		}

		if (!weedEnabled)
		{
			for (int i = 0; i < _savedInterpolations.Count; i++)
			{
				Rigidbody rig = _savedInterpolations[i].Key;
				if (rig != null)
				{
					try
					{
						rig.interpolation = _savedInterpolations[i].Value;
					}
					catch
					{
					}
				}
			}
			_savedInterpolations.Clear();
			return;
		}

		_savedInterpolations.Clear();
		foreach (Bodypart part in ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null)
			{
				continue;
			}
			try
			{
				if (rig.interpolation != RigidbodyInterpolation.None)
				{
					_savedInterpolations.Add(new KeyValuePair<Rigidbody, RigidbodyInterpolation>(rig, rig.interpolation));
					rig.interpolation = RigidbodyInterpolation.None;
				}
			}
			catch
			{
			}
		}
	}

	// ------------------------------------------------------------------
	// Local visuals: while transformed the player model is HIDDEN so the
	// local view matches remote clients (which only receive a buried
	// coordinate and never see the rider on the ball) - the camera follows
	// the weed and the user just sees the tumbleweed rolling.
	// Original renderer states are restored on exit so parts the game had
	// already hidden stay hidden after leaving tumbleweed form.
	// ------------------------------------------------------------------

	private Renderer[] _localRenderers;
	private readonly Dictionary<Renderer, bool> _localRendererStates = new Dictionary<Renderer, bool>();

	private void HideLocalRenderers()
	{
		if (_character == null)
		{
			return;
		}
		try
		{
			_localRendererStates.Clear();
			_localRenderers = ((Component)_character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in _localRenderers)
			{
				if (renderer != null)
				{
					_localRendererStates[renderer] = renderer.enabled;
					renderer.enabled = false;
				}
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.HideLocalRenderers", ex);
		}
	}

	// Re-applies the hide each frame (cheap, no allocation) so a renderer the
	// game re-enables mid-transform cannot pop the player model back on screen.
	private void KeepLocalRenderersHidden()
	{
		if (_localRenderers == null)
		{
			return;
		}
		try
		{
			foreach (Renderer renderer in _localRenderers)
			{
				if (renderer != null && renderer.enabled)
				{
					renderer.enabled = false;
				}
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.KeepLocalRenderersHidden", ex);
		}
	}

	private void ForceShowLocalRenderers()
	{
		if (_character == null)
		{
			return;
		}
		try
		{
			Renderer[] renderers = ((Component)_character).GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in renderers)
			{
				if (renderer == null)
				{
					continue;
				}
				if (_localRendererStates.TryGetValue(renderer, out bool wasEnabled))
				{
					renderer.enabled = wasEnabled;
				}
				else if (!renderer.enabled)
				{
					renderer.enabled = true;
				}
			}
			_localRendererStates.Clear();
			_localRenderers = null;
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.ForceShowLocalRenderers", ex);
		}
	}

	// ------------------------------------------------------------------
	// HUD: the shared Transform filter hides every HUD element INCLUDING the
	// status bar while transformed (the tumbleweed doesn't run on the
	// player's real stamina) and restores them on exit — see
	// Core/TransformHud.cs. It re-checks late-spawning canvases itself.
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
	// Weed visual: spawn the vanilla tumbleweed prefab (networked over
	// Photon like the game's own TumbleWeedSpawner, so even unmodded
	// clients see it) and keep its renderer in sync with the config.
	// ------------------------------------------------------------------

	private void BuildWeedVisual()
	{
		Vector3 position = _character != null ? _character.Center : transform.position;
		BuildWeedVisual(position, Quaternion.identity);
	}

	private void BuildWeedVisual(Vector3 position, Quaternion rotation)
	{
		if (_weedRoot != null || _character == null)
		{
			return;
		}

		try
		{
			GameObject prefab = Resources.Load<GameObject>("tumbleweed");
			if (prefab == null)
			{
				LogInfo("Resources.Load(\"tumbleweed\") returned null; no tumbleweed visual will be shown.");
				return;
			}

			if (PhotonNetwork.InRoom && _character.photonView != null && _character.photonView.ViewID > 0)
			{
				BuildNetworkedWeed(position, rotation);
			}
			else
			{
				BuildLocalWeed(prefab, position, rotation);
			}

			if (_weedRoot != null)
			{
				// The prefab self-destructs after a few seconds and animates its scale in
				// from zero (vanilla behaviour); our weed must stay alive at full size for
				// as long as the player is transformed. Remote modded clients repeat this
				// through the Harmony patches (they cannot run this controller).
				DisableVanillaLifetime(_weedRoot);
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.BuildWeedVisual", ex);
			DestroyWeedVisual();
		}
	}

	private void BuildNetworkedWeed(Vector3 position, Quaternion rotation)
	{
		GameObject instance = PhotonNetwork.Instantiate(
			"tumbleweed",
			position,
			rotation,
			0,
			new object[] { NetworkVisualMarker, _character.photonView.ViewID });
		instance.name = "ImTumbleweedNetworked";
		_weedRoot = instance;
		_weedIsNetworked = true;
		_weedRigidbody = instance.GetComponent<Rigidbody>();
		_weedCollider = instance.GetComponent<SphereCollider>();

		// Keep the TumbleWeed component enabled: its OnCollisionEnter knockdown/thorn
		// behaviour is the fun part. Its FixedUpdate chase AI is suppressed by the Harmony
		// patch (identified through the instantiation-data marker).
		TumbleWeed weed = instance.GetComponent<TumbleWeed>();
		if (weed != null)
		{
			weed.testFullPower = false;
		}

		// The character root follows the weed, so the weed's collider must never push the
		// driver's ragdoll (the Harmony patches set the same ignore up on remote clients).
		IgnoreCollisionWithCharacter(instance, _character);

		LogInfo("Spawned networked tumbleweed visual with vanilla tumbleweed prefab.");
	}

	private void BuildLocalWeed(GameObject prefab, Vector3 position, Quaternion rotation)
	{
		GameObject instance = UnityObject.Instantiate(prefab, position, rotation);
		instance.name = "ImTumbleweedLocal";
		_weedRoot = instance;
		_weedIsNetworked = false;
		_weedRigidbody = instance.GetComponent<Rigidbody>();
		_weedCollider = instance.GetComponent<SphereCollider>();

		// Offline there is no instantiation-data marker for the Harmony patches, so the
		// whole vanilla script (AI and collision knockdown) is disabled outright. The
		// self-destruct timer and scale-in animation are disabled for both spawn paths
		// in BuildWeedVisual.
		TumbleWeed weed = instance.GetComponent<TumbleWeed>();
		if (weed != null)
		{
			weed.enabled = false;
		}

		LogInfo("Spawned local-only tumbleweed visual (offline / single-player).");
	}

	// ------------------------------------------------------------------
	// Weed renewal: unmodded clients run the vanilla RemoveAfterSeconds
	// (~15s self-destruct) on our networked weed because they have no
	// Harmony patches to disable it. To keep the weed visible for the
	// whole (now unlimited) transformation there, the owner destroys and
	// respawns it before that timer can fire, carrying position, rotation
	// and momentum over so the swap is as seamless as possible.
	// ------------------------------------------------------------------

	private float _nextRenewTime;
	private bool _pendingRenewal;
	private Vector3 _renewPosition;
	private Quaternion _renewRotation;
	private Vector3 _renewVelocity;
	private Vector3 _renewAngularVelocity;

	private float GetRenewInterval()
	{
		if (TumbleweedPlugin.WeedRenewInterval == null)
		{
			return 0f;
		}
		float value = TumbleweedPlugin.WeedRenewInterval.Value;
		// 0 = disabled; otherwise clamp below the vanilla 15s self-destruct.
		return value <= 0f ? 0f : Mathf.Clamp(value, 1f, 14f);
	}

	private void MaybeRenewWeed()
	{
		float interval = GetRenewInterval();
		if (interval <= 0f || !PhotonNetwork.InRoom)
		{
			return;
		}

		// The previous frame destroyed the old weed; spawn the replacement now.
		// This branch MUST NOT depend on _weedIsNetworked (DestroyWeedVisual reset it
		// to false) or on _weedRoot (still null): otherwise the respawn never runs and
		// the player is left without a weed to follow. Destroy and Instantiate also
		// MUST NOT happen on the same frame: PUN2 then fires the destroy event before
		// the fresh view is registered and remote clients log "Ev Destroy Failed.
		// Could not find PhotonView", leaving the player frozen at the transform spot.
		if (_pendingRenewal)
		{
			_pendingRenewal = false;
			try
			{
				BuildWeedVisual(_renewPosition, _renewRotation);
				// Carry the momentum into the fresh instance so neither the owner nor
				// remote clients see the weed come to a halt mid-roll.
				if (_weedRigidbody != null)
				{
					_weedRigidbody.linearVelocity = _renewVelocity;
					_weedRigidbody.angularVelocity = _renewAngularVelocity;
				}
				// The weed root changed; re-point the rider -> weed mapping.
				RegisterWeedCharacter(_character.photonView.ViewID, _weedRoot);
			}
			catch (Exception ex)
			{
				LogError("TumbleweedController.RenewWeed(rebuild)", ex);
			}
			return;
		}

		if (!_weedIsNetworked)
		{
			return;
		}
		if (Time.time < _nextRenewTime)
		{
			return;
		}
		_nextRenewTime = Time.time + interval;

		if (_weedRoot == null || _weedRigidbody == null)
		{
			return;
		}
		try
		{
			_renewPosition = _weedRoot.transform.position;
			_renewRotation = _weedRoot.transform.rotation;
			_renewVelocity = _weedRigidbody.linearVelocity;
			_renewAngularVelocity = _weedRigidbody.angularVelocity;

			LogInfo("Renewing networked tumbleweed (keeps it alive on unmodded clients).");
			DestroyWeedVisual();
			_pendingRenewal = true;
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.RenewWeed(destroy)", ex);
		}
	}

	/// <summary>
	/// Stops the prefab's vanilla lifetime scripts: RemoveAfterSeconds would destroy the
	/// weed after a few seconds, and ScaleIn would shrink its collider to near zero for
	/// about a second first (letting the weed fall through the ground while tiny). Both
	/// are disabled before their Start runs, so neither ever ticks. Also called by the
	/// Harmony patches on remote modded clients (identified through the instantiation-data
	/// marker) whose instance data may not be controllable from here.
	/// </summary>
	internal static void DisableVanillaLifetime(GameObject weedRoot)
	{
		RemoveAfterSeconds remover = weedRoot.GetComponent<RemoveAfterSeconds>();
		if (remover != null)
		{
			remover.enabled = false;
		}

		ScaleIn scaleIn = weedRoot.GetComponent<ScaleIn>();
		if (scaleIn != null)
		{
			scaleIn.enabled = false;
		}
	}

	internal static void ApplyNetworkSyncEvent(EventData photonEvent)
	{
		if (photonEvent == null || photonEvent.Code != NetworkSyncEventCode) return;
		try
		{
			object[] data = photonEvent.CustomData as object[];
			if (data == null || data.Length < 6 || !(data[0] is string magic) || magic != NetworkSyncMagic) return;
			if (!(data[1] is int viewId)) return;
			PhotonView view = PhotonView.Find(viewId);
			if (view == null || view.IsMine || !IsMarkedNetworkWeed(view)) return;
			GameObject root = view.gameObject;
			Rigidbody rig = root.GetComponent<Rigidbody>();
			if (rig == null) return;

			Vector3 pos = (Vector3)data[2];
			Quaternion rot = (Quaternion)data[3];
			Vector3 vel = (Vector3)data[4];
			Vector3 ang = (Vector3)data[5];
			float distance = Vector3.Distance(rig.position, pos);
			if (distance > 2.5f)
			{
				rig.position = pos;
				rig.rotation = rot;
			}
			else
			{
				rig.position = Vector3.Lerp(rig.position, pos, 0.45f);
				rig.rotation = Quaternion.Slerp(rig.rotation, rot, 0.45f);
			}
			// 远端刚体可能为 kinematic（位置已同步），设速度只在非 kinematic 时执行（避免 Unity 警告）。
			if (!rig.isKinematic)
			{
				rig.linearVelocity = vel;
				rig.angularVelocity = ang;
			}
		}
		catch (Exception ex)
		{
			LogError("TumbleweedController.ApplyNetworkSyncEvent", ex);
		}
	}

	private static bool IsMarkedNetworkWeed(PhotonView view)
	{
		object[] data = view != null ? view.InstantiationData : null;
		return data != null
		       && data.Length > 0
		       && data[0] is string marker
		       && marker == NetworkVisualMarker;
	}

	/// <summary>Used by Harmony on every modded client to set up collision ignores for the
	/// networked weed (remote clients cannot run this controller). The character root
	/// follows the weed, so the weed's collider must never push the driver's ragdoll.</summary>
	internal static void IgnoreCollisionWithCharacter(GameObject weedRoot, Character character)
	{
		if (weedRoot == null || character == null || character.refs == null || character.refs.ragdoll == null)
		{
			return;
		}

		Collider[] weedColliders = weedRoot.GetComponentsInChildren<Collider>(true);
		if (weedColliders.Length == 0)
		{
			return;
		}

		foreach (Bodypart part in character.refs.ragdoll.partList)
		{
			if (part == null)
			{
				continue;
			}
			Collider[] partColliders = part.GetComponentsInChildren<Collider>(true);
			foreach (Collider weedCollider in weedColliders)
			{
				foreach (Collider partCollider in partColliders)
				{
					if (weedCollider != null && partCollider != null && weedCollider.enabled && partCollider.enabled)
					{
						try
						{
							Physics.IgnoreCollision(weedCollider, partCollider, true);
						}
						catch
						{
						}
					}
				}
			}
		}
	}

	private void UpdateWeedVisual()
	{
		if (_weedRoot == null)
		{
			return;
		}

		// The weed is the physics body, so visibility only toggles its renderer - never
		// SetActive, which would freeze the rigidbody.
		Renderer weedRenderer = _weedRoot.GetComponent<Renderer>();
		if (weedRenderer != null && weedRenderer.enabled != TumbleweedPlugin.ShowWeedVisual.Value)
		{
			weedRenderer.enabled = TumbleweedPlugin.ShowWeedVisual.Value;
		}
	}

	private void DestroyWeedVisual()
	{
		if (_weedRoot != null)
		{
			if (_weedIsNetworked && PhotonNetwork.InRoom)
			{
				try
				{
					PhotonNetwork.Destroy(_weedRoot);
				}
				catch
				{
					// If we don't own it anymore or it's already gone, fall back silently.
				}
			}
			else
			{
				Destroy(_weedRoot);
			}
		}

		_weedRoot = null;
		_weedRigidbody = null;
		_weedCollider = null;
		_weedIsNetworked = false;
		if (_character != null && _character.photonView != null)
		{
			UnregisterWeedCharacter(_character.photonView.ViewID);
		}
	}

	private Vector3 GetWeedCenter()
	{
		return _weedRoot != null ? _weedRoot.transform.position : _character.Center;
	}

	// ------------------------------------------------------------------
	// Camera: driven either from the Harmony postfix on
	// MainCameraMovement.LateUpdate or from this controller's LateUpdate
	// (DefaultExecutionOrder 600 > 500).
	// ------------------------------------------------------------------

	internal static void ApplyCameraOverrideForLocalWeed()
	{
		Character character = ActiveWeedCharacter;
		if (character == null)
		{
			return;
		}
		TumbleweedController controller = ((Component)character).GetComponent<TumbleweedController>();
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
			if (camera == null)
			{
				return;
			}

			Vector3 weedCenter = GetWeedCenter();
			Vector3 forward = GetCameraForward(_character);
			Vector3 lookDirection = _character.data != null ? _character.data.lookDirection.normalized : forward;
			float verticalLook = Mathf.Clamp(lookDirection.y, -0.35f, 0.65f);

			Vector3 lookTarget = weedCenter
			                     + Vector3.up * (GetCameraHeight() * 0.5f + verticalLook * 2f)
			                     + forward * CameraLookAhead;
			Vector3 desiredPosition = weedCenter
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
			LogError("TumbleweedController.RefreshCamera", ex);
		}
	}

	private static float GetCameraDistance()
	{
		float value = TumbleweedPlugin.CameraDistance != null ? TumbleweedPlugin.CameraDistance.Value : DefaultCameraDistance;
		return Mathf.Clamp(value, 6f, 30f);
	}

	private static float GetCameraHeight()
	{
		float value = TumbleweedPlugin.CameraHeight != null ? TumbleweedPlugin.CameraHeight.Value : DefaultCameraHeight;
		return Mathf.Clamp(value, 1f, 14f);
	}

	private static float GetCameraFov()
	{
		float value = TumbleweedPlugin.CameraFov != null ? TumbleweedPlugin.CameraFov.Value : DefaultCameraFov;
		return Mathf.Clamp(value, 60f, 110f);
	}

	private static Vector3 GetCameraForward(Character character)
	{
		Vector3 forward = character.data != null ? character.data.lookDirection_Flat : Vector3.zero;
		forward = Vector3.ProjectOnPlane(forward, Vector3.up);
		if (forward.sqrMagnitude < 0.0001f && Camera.main != null)
		{
			forward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
		}
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.ProjectOnPlane(((Component)character).transform.forward, Vector3.up);
		}
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}
		return forward.normalized;
	}

	private static Quaternion GetCameraRotation(Vector3 cameraPosition, Vector3 lookTarget)
	{
		Vector3 lookDirection = lookTarget - cameraPosition;
		if (lookDirection.sqrMagnitude < 0.0001f)
		{
			lookDirection = Vector3.forward;
		}
		return Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
	}
}
