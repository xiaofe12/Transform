using System;
using System.Collections.Generic;
using System.Reflection;
using ExitGames.Client.Photon;
using HarmonyLib;
using Peak;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ImRaven;

/// <summary>
/// Shared flight controller for Condor and Raven. Legacy Condor identifiers remain stable
/// so existing configuration and room form IDs continue to work. A visual-only copy of the
/// scene Condor follows the player; its AI, nest and scene PhotonView are never cloned.
/// Local secondary-use attacks are disabled. Legacy remote grab state remains supported
/// so older modded peers can still release their carried players cleanly.
/// </summary>
[DefaultExecutionOrder(600)]
public sealed class RavenController : MonoBehaviour
{
	private const float GroundClearanceMin = 0.8f;
	private const float VisualFaceMoveSharpness = 6f;
	private const float CameraLookAhead = 3f;
	private const float CameraRotationSharpness = 12f;
	private const float DefaultCameraDistance = 8f;
	private const float DefaultCameraHeight = 2.5f;
	private const float DefaultCameraFov = 80f;

	/// <summary>Custom Photon event used to replicate the owner-driven raven transform.</summary>
	private const byte NetworkSyncEventCode = 199;
	private const string NetworkSyncMagic = "Transform.Condor.Sync";
	private const string NetworkReleaseMagic = "Transform.Condor.Release";
	private const string CrowSyncMagic = "Transform.Crow.Sync";
	private const string CrowReleaseMagic = "Transform.Crow.Release";
	private const float NetworkSyncInterval = 0.05f;
	private const float RemoteRiderTimeout = 1.5f;

	// Prefer the live scene model; the installed game files also support a fresh Airport visit.
	private static GameObject _visualTemplate;
	private static bool _templateFromScene;
	private static Vector3 _grabAnchorLocal = Vector3.down;
	private static float _nextTemplateProbe;
	private static bool _loggedTemplateFailure;

	private Character _character;
	private readonly global::Transform.Core.FlightMotion _flightMotion = new global::Transform.Core.FlightMotion();
	private readonly global::Transform.Core.FlightCameraRig _cameraRig = new global::Transform.Core.FlightCameraRig();
	private readonly CondorNetworkPose _networkPose = new CondorNetworkPose();
	private Vector3 _renderCenter;
	private float _cameraSmoothedFov;
	private int _lastCameraFrame = -1;
	private int _lastPresentationFrame = -1;
	private Quaternion _cameraSmoothedRotation;
	private bool _cameraInitialized;
	private Vector3 _moveDirection;

	private GameObject _visualRoot;
	private float _nextNetworkSyncTime;
	private Vector3 _originalHandLocalPosition;
	private bool _savedHandPosition;

	// Local player body renderers are hidden while in Condor form (the bird replaces them).
	private Renderer[] _localRenderers;
	private readonly Dictionary<Renderer, bool> _localRendererStates = new Dictionary<Renderer, bool>();

	/// <summary>Remote characters whose owner is currently a raven (keyed by character PhotonView
	/// id). Modded clients hide those riders and skip their ragdoll interpolation; entries are
	/// refreshed by the 20 Hz sync events and dropped after a timeout or an explicit release.</summary>
	private sealed class RemoteRiderState
	{
		public Character Character;
		public float LastSeen;
		public bool Hidden;
		/// <summary>True for current senders, which always broadcast the real flight position.
		/// Retain the flag so older senders using hidden coordinates remain compatible.</summary>
		public bool BodyVisible;
		public GameObject Visual;
		public Vector3 Position;
		public Quaternion Rotation;
		public bool Carrying;
		public bool Crow;
		public bool Grounded;
		public Vector3 Velocity;
		public float PoseTime;
		public bool HasPose;
		public Rigidbody Hand;
		public bool HandWasKinematic;
		public Vector3 HandLocalPosition;
	}

	private static readonly Dictionary<int, RemoteRiderState> RemoteRiders = new Dictionary<int, RemoteRiderState>();

	// Ragdoll rigidbodies whose interpolation we disabled while in Condor form, mapped to
	// their original setting so SetRavenNoClip(false) can restore them exactly.
	private readonly List<KeyValuePair<Rigidbody, RigidbodyInterpolation>> _savedInterpolations =
		new List<KeyValuePair<Rigidbody, RigidbodyInterpolation>>();

	public bool Active { get; private set; }
	internal bool IsCrow { get; private set; }
	private BirdFlightSettings Settings => IsCrow ? RavenPlugin.CrowSettings : RavenPlugin.CondorSettings;
	private readonly CrowGroundState _crowGround = new CrowGroundState();
	private readonly RaycastHit[] _crowGroundHits = new RaycastHit[32];

	/// <summary>The character this controller is driving, for the plugin's camera override.</summary>
	public Character CharacterRef => _character;

	/// <summary>True while the controller is active and its character is still alive/valid.</summary>
	public bool IsValid()
	{
		if (!Active || _character == null || _character.data == null)
		{
			return false;
		}
		return !_character.data.dead && !_character.data.fullyPassedOut;
	}

	private static void LogInfo(string message) => RavenPlugin.Log?.LogInfo("[Condor] " + message);
	private static void LogError(string entryPoint, Exception ex) => RavenPlugin.Log?.LogError("[Condor] " + entryPoint + ": " + ex);

	/// <summary>The local character currently in Condor form, or null.</summary>
	private static Character ActiveRavenCharacter;

	/// <summary>
	/// True once the camera Harmony fallback (a postfix on MainCameraMovement.LateUpdate) has
	/// been applied. While true the camera is driven from that postfix; otherwise this
	/// controller's own LateUpdate (execution order 600) drives it.
	/// </summary>
	internal static bool CameraOverridePatchActive { get; set; }

	/// <summary>True while the local character is in Condor form and being driven by this controller.</summary>
	public static bool IsLocalRavenCharacter(Character character)
	{
		if (character == null || !character.IsLocal)
		{
			return false;
		}
		RavenController controller = ((Component)character).GetComponent<RavenController>();
		return controller != null && controller.Active;
	}

	public void EnterRaven(Character character, bool crow = false)
	{
		IsCrow = crow;
		_character = character;
		_flightMotion.Reset(character.transform.position, character.Center);
		Rigidbody hip = FindPartRig(character, BodypartType.Hip);
		_networkPose.Reset(_flightMotion.Position, hip != null ? hip.position : _flightMotion.Position);
		_renderCenter = _flightMotion.Position;
		_cameraRig.Reset();
		_lastCameraFrame = -1;
		_lastPresentationFrame = -1;
		_cameraInitialized = false;
		_nextNetworkSyncTime = 0f;
		_moveDirection = Vector3.zero;
		Active = true;
		ActiveRavenCharacter = character;
		enabled = true;
		Rigidbody hand = FindPartRig(character, BodypartType.Hand_R);
		_savedHandPosition = hand != null;
		if (hand != null) _originalHandLocalPosition = character.transform.InverseTransformPoint(hand.position);
		ForceRestoreCharacterCollision();
		SetRavenNoClip(true);
		_crowGround.Reset();
		if (IsCrow && character.data != null && character.data.isGrounded)
		{
			float ground = GetCrowGroundHeight(_flightMotion.Position);
			if (!float.IsNaN(ground))
			{
				_crowGround.Reset(true);
				ApplyFlightStep(_crowGround.Step(_flightMotion.Position, Vector3.zero, ground, false));
				_flightMotion.Reset(transform.position, _flightMotion.Position);
				_renderCenter = _flightMotion.Position;
			}
		}
		BuildRavenVisual();
		if (_visualRoot == null)
		{
			ExitRaven();
			return;
		}
		HideLocalRenderers();
		HideHud();
		LogInfo(IsCrow ? "Entered Raven form." : "Entered Condor form.");
	}

	public void ExitRaven()
	{
		if (!Active && _visualRoot == null) return;
		Active = false; ActiveRavenCharacter = null; enabled = false; _moveDirection = Vector3.zero;
		var cleanup = new global::Transform.Core.CleanupSequence((step, ex) => global::Transform.TransformPlugin.Log?.LogWarning("[RavenController] Cleanup " + step + ": " + ex));
		cleanup.Step("grab", () => { ReleaseGrab(); });
		cleanup.Step("position", () => { global::Transform.Core.RestoreSafety.MoveBeforePhysics(_character); });
		cleanup.Step("hand", () => { RestoreHandPosition(); });
		cleanup.Step("physics", () => { SetRavenNoClip(false); });
		cleanup.Step("collision", () => { ForceRestoreCharacterCollision(); });
		cleanup.Step("renderers", () => { RestoreLocalRenderers(); });
		cleanup.Step("visual", () => { DestroyRavenVisual(); });
		cleanup.Step("HUD", () => { RestoreHud(); });

	}

	private void Update()
	{
		if (!Active || _character == null)
		{
			return;
		}

		try
		{
			ClearNonMovementInput();
			KeepPlayerAlive();
			// No local attacks: also release any stale/native grab state.
			if (HasGrab()) ReleaseGrab();
			// The HUD canvases and renderers may be re-enabled after we transform.
			HideHud();
			KeepLocalRenderersHidden();
		}
		catch (Exception ex)
		{
			LogError("RavenController.Update", ex);
		}
	}

	private void FixedUpdate()
	{
		if (!Active || _character == null || _character.refs == null)
		{
			return;
		}

		try
		{
			MoveRavenTransform();
			SyncCharacterData();
			UpdateGrabAnchor();
			SendNetworkSync();
		}
		catch (Exception ex)
		{
			LogError("RavenController.FixedUpdate", ex);
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
			PreparePresentation();
			if (!CameraOverridePatchActive)
			{
				RefreshCamera();
			}
		}
		catch (Exception ex)
		{
			LogError("RavenController.LateUpdate", ex);
		}
	}

	private void OnDestroy()
	{
		ExitRaven();
	}

	// ------------------------------------------------------------------
	// HUD
	// ------------------------------------------------------------------

	private void HideHud()
	{
		global::Transform.Core.TransformHud.TickHide();
	}

	private void RestoreHud()
	{
		global::Transform.Core.TransformHud.Restore();
	}

	// ------------------------------------------------------------------
	// Body renderers: the raven is much smaller than the player, so the body
	// is hidden while transformed (local player here, modded remotes below).
	// ------------------------------------------------------------------

	private void HideLocalRenderers()
	{
		if (_character == null)
		{
			return;
		}
		try
		{
			if (_localRenderers == null)
			{
				_localRenderers = ((Component)_character).GetComponentsInChildren<Renderer>(true);
				_localRendererStates.Clear();
				foreach (Renderer renderer in _localRenderers)
				{
					if (renderer != null)
					{
						_localRendererStates[renderer] = renderer.enabled;
					}
				}
			}
			foreach (Renderer renderer in _localRenderers)
			{
				if (renderer != null)
				{
					renderer.enabled = false;
				}
			}
		}
		catch (Exception ex)
		{
			LogError("RavenController.HideLocalRenderers", ex);
		}
	}

	private void KeepLocalRenderersHidden()
	{
		if (_localRenderers == null)
		{
			return;
		}
		foreach (Renderer renderer in _localRenderers)
		{
			if (renderer != null && renderer.enabled)
			{
				renderer.enabled = false;
			}
		}
	}

	private void RestoreLocalRenderers()
	{
		if (_localRenderers == null)
		{
			return;
		}
		foreach (Renderer renderer in _localRenderers)
		{
			if (renderer == null)
			{
				continue;
			}
			bool wasEnabled = true;
			_localRendererStates.TryGetValue(renderer, out wasEnabled);
			renderer.enabled = wasEnabled;
		}
		_localRendererStates.Clear();
		_localRenderers = null;
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
		if (raw.sqrMagnitude <= 0.0001f) raw = global::Transform.Core.GameInput.Move();
		raw.x = Mathf.Clamp(raw.x, -1f, 1f);
		raw.y = Mathf.Clamp(raw.y, -1f, 1f);
		return raw;
	}

	// ------------------------------------------------------------------
	// Physics: no-clip flight - kinematic ragdoll (colliders off) and the
	// character root transform is moved directly every physics step.
	// ------------------------------------------------------------------

	private void SetRavenNoClip(bool enable)
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
				// Let go of anyone we are carrying through the vanilla item-carry path.
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
			LogError("RavenController.SetRavenNoClip(" + enable + ")", ex);
		}
	}

	private void ForceRestoreCharacterCollision()
	{
		if (_character == null || _character.refs == null || _character.refs.ragdoll == null)
		{
			return;
		}

		try
		{
			CharacterRagdoll ragdoll = _character.refs.ragdoll;
			ragdoll.ToggleKinematic(false);
			ragdoll.ToggleCollision(true);

			if (ragdoll.partList == null)
			{
				return;
			}

			foreach (Bodypart part in ragdoll.partList)
			{
				if (part == null)
				{
					continue;
				}

				Rigidbody rig = part.Rig;
				if (rig != null)
				{
					rig.isKinematic = false;
					rig.detectCollisions = true;
					rig.useGravity = true;
					rig.WakeUp();
				}

				Collider[] colliders = part.GetComponentsInChildren<Collider>(true);
				if (colliders == null)
				{
					continue;
				}
				foreach (Collider collider in colliders)
				{
					if (collider != null && !collider.enabled)
					{
						collider.enabled = true;
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogError("RavenController.ForceRestoreCharacterCollision", ex);
		}
	}

	/// <summary>See GhostController.SetRagdollInterpolation: direct transform writes and
	/// interpolated rigidbodies fight each other, producing jitter.</summary>
	private void SetRagdollInterpolation(bool ravenEnabled)
	{
		CharacterRagdoll ragdoll = _character != null && _character.refs != null ? _character.refs.ragdoll : null;
		if (ragdoll == null || ragdoll.partList == null)
		{
			return;
		}

		if (!ravenEnabled)
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

	private void MoveRavenTransform()
	{
		// Unified menu open: freeze flight input so menu clicks never leak into the form.
		if (global::TransformState.MenuOpen || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
		{
			_moveDirection = Vector3.zero;
			ApplyFlightStep(Vector3.zero);
			return;
		}

		Vector3 forward = _character.data != null ? _character.data.lookDirection_Flat : Vector3.zero;
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
		}
		forward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}

		Vector2 input = GetMovementInput();
		Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
		Vector3 move = forward * input.y + right * input.x;
		if (move.sqrMagnitude > 1f)
		{
			move.Normalize();
		}

		float vertical = 0f;
		if (global::Transform.Core.GameInput.JumpHeld(Settings.UpKey.Value))
		{
			vertical += 1f;
		}
		if (global::Transform.Core.GameInput.CrouchHeld(Settings.DownKey.Value))
		{
			vertical -= 1f;
		}

		float speedMultiplier = 1f;
		if (global::Transform.Core.GameInput.SprintHeld(Settings.SprintKey.Value))
		{
			speedMultiplier = Mathf.Max(1f, Settings.SprintMultiplier.Value);
		}

		Vector3 delta = (move * Settings.MovementSpeed.Value + Vector3.up * (vertical * Settings.VerticalSpeed.Value))
		                * speedMultiplier * Time.fixedDeltaTime;

		// Do not sink through the terrain while descending near it.
		if (IsCrow)
		{
			Vector3 probe = _flightMotion.Position + (_crowGround.Grounded && vertical <= 0f
				? Vector3.zero : new Vector3(delta.x, 0f, delta.z));
			delta = _crowGround.Step(_flightMotion.Position, delta, GetCrowGroundHeight(probe), vertical > 0f);
			if (_crowGround.Grounded) move = Vector3.zero;
		}
		else if (delta.y < 0f)
		{
			float groundY = GetGroundHeight(_flightMotion.Position);
			if (!float.IsNaN(groundY))
			{
				float remaining = Mathf.Min(0f, groundY + GroundClearanceMin - _flightMotion.Position.y);
				delta.y = Mathf.Clamp(delta.y, remaining, 0f);
			}
		}

		_moveDirection = move.sqrMagnitude > 0.0001f ? move : Vector3.zero;

		ApplyFlightStep(delta);
	}

	private void ApplyFlightStep(Vector3 delta)
	{
		_flightMotion.Step(delta, Time.fixedDeltaTime);
		transform.position = _flightMotion.RootPosition;
	}

	private float GetCrowGroundHeight(Vector3 position)
	{
		// Query nearby support from above the bird, not 400m above a roof/cave.
		int count = Physics.RaycastNonAlloc(position + Vector3.up * 0.25f, Vector3.down,
			_crowGroundHits, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
		float height = float.NaN;
		for (int i = 0; i < count; i++)
		{
			RaycastHit hit = _crowGroundHits[i];
			if (hit.collider == null || hit.normal.y < 0.5f
				|| hit.collider.GetComponentInParent<Character>() != null
				|| (CharacterRagdoll.TryGetCharacterFromCollider(hit.collider, out Character owner) && owner != null)) continue;
			if (float.IsNaN(height) || hit.point.y > height) height = hit.point.y;
		}
		return height;
	}

	private float GetGroundHeight(Vector3 position)
	{
		try
		{
			RaycastHit hit = HelperFunctions.GetGroundPosRaycast(position + Vector3.up * 400f, HelperFunctions.LayerType.Terrain);
			if (hit.transform != null)
			{
				return hit.point.y;
			}
		}
		catch
		{
		}

		try
		{
			RaycastHit hit2 = HelperFunctions.GetGroundPosRaycast(position + Vector3.up * 400f, HelperFunctions.LayerType.TerrainMap);
			if (hit2.transform != null)
			{
				return hit2.point.y;
			}
		}
		catch
		{
		}

		return float.NaN;
	}

	private void SyncCharacterData()
	{
		if (_character == null || _character.data == null)
		{
			return;
		}

		// Velocity is measured in the physics step that produced the displacement.
		// Render frames may contain zero or several steps; dividing by their delta
		// alternated between zero and spikes during ascent/descent.
		_character.data.avarageLastFrameVelocity = _character.data.avarageVelocity;
		_character.data.avarageVelocity = _flightMotion.Velocity;
		_character.data.worldMovementInput = _character.data.worldMovementInput_Grounded = Vector3.zero;
		_character.data.sinceGrounded = Mathf.Min(_character.data.sinceGrounded, 0.1f);
	}

	// ------------------------------------------------------------------
	// Cleanup and legacy remote carry compatibility. Local grabbing is disabled.
	// ------------------------------------------------------------------

	private bool HasGrab()
	{
		if (_character == null || _character.data == null)
		{
			return false;
		}
		return _character.data.grabJoint != null || GetGrabbedPlayer(_character.data) != null;
	}

	/// <summary>
	/// Write a stable, real-world vanilla pose for all peers. Never hide a Condor by
	/// falsifying its position: unmodded clients cannot undo that offset or pin the hand.
	/// </summary>
	internal static void WriteLocalNetworkPose(Character character, ref CharacterSyncData packet)
	{
		if (character == null || !character.IsLocal) return;
		RavenController controller = ((Component)character).GetComponent<RavenController>();
		if (controller == null || !controller.Active) return;
		controller._networkPose.Write(controller._flightMotion.Position, controller._flightMotion.Velocity, ref packet);
	}

	// CharacterData.grabbedPlayer / grabbingPlayer are internal to the game assembly,
	// so they are read through cached reflection fields.
	private static readonly FieldInfo GrabbedPlayerField = AccessTools.Field(typeof(CharacterData), "grabbedPlayer");

	private static readonly MethodInfo LimitFallingMethod = AccessTools.Method(typeof(Character), "LimitFalling");

	internal static Character GetGrabbedPlayer(CharacterData data)
	{
		return data != null && GrabbedPlayerField != null ? GrabbedPlayerField.GetValue(data) as Character : null;
	}

	private static void LimitFalling(Character character)
	{
		try
		{
			LimitFallingMethod?.Invoke(character, null);
		}
		catch
		{
		}
	}

	private void ReleaseGrab()
	{
		if (_character == null || _character.data == null || !HasGrab())
		{
			return;
		}
		if (_character.GetComponent<CharacterGrabbing>() == null)
		{
			return;
		}
		try
		{
			_character.photonView.RPC("RPCA_GrabUnattach", RpcTarget.All);
			_nextNetworkSyncTime = 0f;
			LogInfo("Condor dropped the carried player.");
		}
		catch (Exception ex)
		{
			LogError("RavenController.ReleaseGrab", ex);
		}
	}

	private void UpdateGrabAnchor()
	{
		if (_visualRoot == null || _character == null) return;
		Rigidbody hand = FindPartRig(_character, BodypartType.Hand_R);
		if (hand == null) return;
		Vector3 anchor = _flightMotion.Position + _visualRoot.transform.rotation * _grabAnchorLocal;
		hand.position = anchor;
		hand.transform.position = anchor;
	}

	private void RestoreHandPosition()
	{
		if (!_savedHandPosition || _character == null) return;
		Rigidbody hand = FindPartRig(_character, BodypartType.Hand_R);
		if (hand != null)
		{
			Vector3 position = _character.transform.TransformPoint(_originalHandLocalPosition);
			hand.position = position;
			hand.transform.position = position;
		}
		_savedHandPosition = false;
	}

	private static bool FindGrabPart(Character character, BodypartType preferred, out Rigidbody rig, out BodypartType partType)
	{
		rig = null;
		partType = preferred;
		if (character == null || character.refs == null || character.refs.ragdoll == null)
		{
			return false;
		}
		List<Bodypart> parts = character.refs.ragdoll.partList;
		if (parts == null || parts.Count == 0)
		{
			return false;
		}

		Bodypart fallback = null;
		foreach (Bodypart part in parts)
		{
			if (part == null || part.Rig == null)
			{
				continue;
			}
			if (part.partType == preferred)
			{
				rig = part.Rig;
				partType = part.partType;
				return true;
			}
			if (fallback == null)
			{
				fallback = part;
			}
		}

		if (fallback == null)
		{
			return false;
		}
		rig = fallback.Rig;
		partType = fallback.partType;
		return true;
	}

	private static Rigidbody FindPartRig(Character character, BodypartType type)
	{
		Rigidbody rig;
		BodypartType actualType;
		return FindGrabPart(character, type, out rig, out actualType) && actualType == type ? rig : null;
	}

	// ------------------------------------------------------------------
	// Condor visual: copy only the animated model, never the scene AI or PhotonView.
	// ------------------------------------------------------------------

	internal static bool EnsureVisualTemplate(bool crow = false)
	{
		if (crow) return CrowVisualAsset.EnsureTemplate();
		if (_visualTemplate != null && _templateFromScene) return true;
		if (Time.unscaledTime < _nextTemplateProbe) return _visualTemplate != null;
		_nextTemplateProbe = Time.unscaledTime + 2f;
		foreach (Condor condor in Resources.FindObjectsOfTypeAll<Condor>())
		{
			if (condor == null || condor.anim == null || condor.anim.transform == condor.transform) continue;
			GameObject model = condor.anim.gameObject;
			// The game's model subtree contains rendering, animation and audio only. Refuse a
			// changed hierarchy rather than duplicating a scene PhotonView or gameplay AI.
			if (model.GetComponentsInChildren<MonoBehaviour>(true).Length != 0) continue;
			GameObject template = new GameObject("Transform.Condor.Template");
			template.SetActive(false);
			UnityObject.DontDestroyOnLoad(template);
			GameObject copy = UnityObject.Instantiate(model, template.transform, false);
			copy.transform.localPosition = condor.transform.InverseTransformPoint(model.transform.position);
			copy.transform.localRotation = Quaternion.Inverse(condor.transform.rotation) * model.transform.rotation;
			copy.transform.localScale = Vector3.Scale(model.transform.lossyScale,
				new Vector3(1f / condor.transform.lossyScale.x, 1f / condor.transform.lossyScale.y, 1f / condor.transform.lossyScale.z));
			copy.SetActive(true);
			foreach (Collider collider in template.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
			foreach (Rigidbody rig in template.GetComponentsInChildren<Rigidbody>(true))
			{
				rig.isKinematic = true;
				rig.detectCollisions = false;
			}
			if (condor.dragToPoint != null)
				_grabAnchorLocal = condor.transform.InverseTransformPoint(condor.dragToPoint.position);
			GameObject previous = _visualTemplate;
			_visualTemplate = template;
			_templateFromScene = true;
			// A disk-created Airport template must not permanently mask the original
			// model when the playable map loads. Replace existing visuals in place too.
			if (previous != null)
			{
				RavenController local = ActiveRavenCharacter != null ? ActiveRavenCharacter.GetComponent<RavenController>() : null;
				if (local != null && !local.IsCrow && local._visualRoot != null)
					local._visualRoot = ReplaceVisual(local._visualRoot, local.HasGrab());
				foreach (RemoteRiderState state in RemoteRiders.Values)
					if (!state.Crow && state.Visual != null) state.Visual = ReplaceVisual(state.Visual, state.Carrying);
				UnityObject.Destroy(previous);
			}
			_loggedTemplateFailure = false;
			LogInfo("Cached the original volcano Condor model, materials, wing trails and flight audio.");
			return true;
		}
		if (_visualTemplate != null) return true;
		// Wait for a playable character so Airport's normal shaders are loaded. Probing
		// during startup must not cache a substitute material before the scene is ready.
		if (Character.localCharacter == null && RemoteRiders.Count == 0) return false;
		try
		{
			_visualTemplate = CondorVisualAsset.CreateTemplate(out _grabAnchorLocal);
			_templateFromScene = false;
			_loggedTemplateFailure = false;
			LogInfo("Loaded Condor model, textures, flight animations and glide/flap audio from installed game resources (available in Airport).");
			return true;
		}
		catch (Exception ex)
		{
			if (!_loggedTemplateFailure) LogError("Loading Condor visual from installed game resources", ex);
			_loggedTemplateFailure = true;
			_nextTemplateProbe = Time.unscaledTime + 30f;
			return false;
		}
	}

	private static GameObject CreateCondorVisual(Vector3 position, Quaternion rotation, bool crow = false)
	{
		if (crow) return CrowVisualAsset.Create(position, rotation);
		if (!EnsureVisualTemplate()) return null;
		GameObject visual = UnityObject.Instantiate(_visualTemplate, position, rotation);
		visual.name = "Transform.Condor.Visual";
		visual.SetActive(true);
		ClearWingTrails(visual);
		SetVisualCarrying(visual, false);
		return visual;
	}

	private static GameObject ReplaceVisual(GameObject previous, bool carrying)
	{
		GameObject replacement = CreateCondorVisual(previous.transform.position, previous.transform.rotation);
		if (replacement == null) return previous;
		replacement.transform.localScale = previous.transform.localScale;
		SetVisualCarrying(replacement, carrying);
		previous.SetActive(false);
		UnityObject.Destroy(previous);
		return replacement;
	}

	private static void ClearWingTrails(GameObject visual)
	{
		// Cloning a flying scene object must not copy its old world-space trail.
		foreach (TrailRenderer trail in visual.GetComponentsInChildren<TrailRenderer>(true)) trail.Clear();
	}

	private static void SetVisualCarrying(GameObject visual, bool carrying)
	{
		ApplyFlightPresentation(visual, Vector3.zero, carrying, 0f);
	}

	private static CondorFlightMode ApplyFlightPresentation(GameObject visual, Vector3 velocity, bool carrying, float deltaTime, bool grounded = false)
	{
		if (visual == null) return CondorFlightMode.Flap;
		var crowPresentation = visual.GetComponent<CrowFlightPresentation>();
		if (crowPresentation != null) return crowPresentation.Apply(velocity, deltaTime, grounded);
		var presentation = visual.GetComponent<CondorFlightPresentation>();
		if (presentation == null) presentation = visual.AddComponent<CondorFlightPresentation>();
		return presentation.Apply(velocity, deltaTime, carrying);
	}

	private void BuildRavenVisual()
	{
		if (_visualRoot == null && _character != null)
		{
			_visualRoot = CreateCondorVisual(_flightMotion.Position, GetVisualRotation(), IsCrow);
			global::Transform.Core.RestoreSafety.Ignore(_visualRoot);
		}
	}

	private Quaternion GetVisualRotation()
	{
		Vector3 forward = _character != null && _character.data != null
			? Vector3.ProjectOnPlane(_character.data.lookDirection, Vector3.up)
			: Vector3.zero;
		if (forward.sqrMagnitude < 0.0001f)
		{
			return Quaternion.identity;
		}
		return Quaternion.LookRotation(forward.normalized, Vector3.up);
	}

	private void UpdateRavenVisual()
	{
		if (_visualRoot == null || _character == null)
		{
			return;
		}

		UnityEngine.Transform visual = _visualRoot.transform;
		visual.position = _renderCenter;
		CondorFlightMode mode = ApplyFlightPresentation(_visualRoot, _flightMotion.Velocity, HasGrab(), Time.deltaTime, IsCrow && _crowGround.Grounded);
		if (IsCrow && _crowGround.Grounded)
		{
			visual.rotation = Quaternion.Euler(0f, visual.eulerAngles.y, 0f);
			return;
		}

		Vector3 faceDirection = _moveDirection.sqrMagnitude > 0.01f ? _moveDirection : GetVisualFaceDirection();
		if (faceDirection.sqrMagnitude > 0.01f)
		{
			Quaternion targetRotation = Quaternion.LookRotation(faceDirection.normalized, Vector3.up)
				* Quaternion.Euler(mode == CondorFlightMode.Dive ? 40f : 0f, 0f, 0f);
			visual.rotation = Quaternion.Slerp(visual.rotation, targetRotation, Time.deltaTime * VisualFaceMoveSharpness);
		}
	}

	private Vector3 GetVisualFaceDirection()
	{
		Vector3 forward = _character.data != null
			? Vector3.ProjectOnPlane(_character.data.lookDirection_Flat, Vector3.up)
			: Vector3.zero;
		if (forward.sqrMagnitude < 0.0001f)
		{
			Camera camera = Camera.main;
			if (camera != null)
			{
				forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
			}
		}
		return forward;
	}

	private void DestroyRavenVisual()
	{
		if (PhotonNetwork.InRoom) SendNetworkRelease();
		if (_visualRoot != null) Destroy(_visualRoot);
		_visualRoot = null;
	}

	// Scene Condors are not Photon resource prefabs. Replicate visual state by the
	// owner's existing character view ID; each modded peer creates a model-only copy.
	private void SendNetworkSync()
	{
		if (_visualRoot == null || !PhotonNetwork.InRoom || _character?.photonView == null) return;
		if (Time.unscaledTime < _nextNetworkSyncTime) return;
		_nextNetworkSyncTime = Time.unscaledTime + NetworkSyncInterval;
		UnityEngine.Transform visual = _visualRoot.transform;
		PhotonNetwork.RaiseEvent(NetworkSyncEventCode,
			new object[] { IsCrow ? CrowSyncMagic : NetworkSyncMagic, _character.photonView.ViewID, _flightMotion.Position, visual.rotation,
				true, HasGrab(), _flightMotion.Velocity, IsCrow && _crowGround.Grounded },
			new RaiseEventOptions { Receivers = ReceiverGroup.Others },
			new SendOptions { Reliability = false });
	}

	internal static void ApplyNetworkSyncEvent(EventData photonEvent)
	{
		if (photonEvent == null || photonEvent.Code != NetworkSyncEventCode)
		{
			return;
		}
		try
		{
			object[] data = photonEvent.CustomData as object[];
			if (data == null || data.Length < 2 || !(data[0] is string magic))
			{
				return;
			}
			if (!(data[1] is int characterViewId)) return;
			PhotonView view = PhotonView.Find(characterViewId);
			// A peer may only update the character it owns.
			if (view == null || view.IsMine || view.OwnerActorNr != photonEvent.Sender) return;
			if (magic == NetworkReleaseMagic || magic == CrowReleaseMagic)
			{
				// A reliable release for the previous bird can arrive after the new
				// bird's unreliable pose. Do not remove that new visual.
				if (RemoteRiders.TryGetValue(characterViewId, out RemoteRiderState released)
					&& released.Crow == (magic == CrowReleaseMagic)) ReleaseRemoteRider(characterViewId);
				return;
			}
			if ((magic != NetworkSyncMagic && magic != CrowSyncMagic) || data.Length < 6
				|| !(data[2] is Vector3 position) || !(data[3] is Quaternion rotation)
				|| !(data[4] is bool bodyVisible) || !(data[5] is bool carrying)) return;
			if (!IsFinite(position) || !IsFinite(new Vector3(rotation.x, rotation.y, rotation.z))
				|| float.IsNaN(rotation.w) || float.IsInfinity(rotation.w)) return;
			TrackRemoteRider(characterViewId, bodyVisible);
			if (!RemoteRiders.TryGetValue(characterViewId, out RemoteRiderState state)) return;
			bool crow = magic == CrowSyncMagic;
			if (state.Crow != crow)
			{
				RestoreRemoteRider(state);
				state.Hidden = false;
				state.Visual = null;
				state.HasPose = false;
			}
			state.Crow = crow;
			float elapsed = Time.unscaledTime - state.PoseTime;
			Vector3 velocity = data.Length > 6 && data[6] is Vector3 supplied && IsFinite(supplied)
				? supplied : state.HasPose && elapsed > 0.01f && elapsed < 0.5f && Vector3.Distance(position, state.Position) < 10f
				? (position - state.Position) / elapsed : Vector3.zero;
			state.Velocity = Vector3.ClampMagnitude(velocity, 150f);
			state.PoseTime = Time.unscaledTime;
			state.HasPose = true;
			state.Position = position;
			state.Rotation = rotation;
			state.Carrying = carrying;
			state.Grounded = crow && data.Length > 7 && data[7] is bool grounded && grounded;
		}
		catch (Exception ex)
		{
			LogError("RavenController.ApplyNetworkSyncEvent", ex);
		}
	}

	private void SendNetworkRelease()
	{
		try
		{
			if (_character == null || _character.photonView == null)
			{
				return;
			}
			PhotonNetwork.RaiseEvent(NetworkSyncEventCode,
				new object[] { IsCrow ? CrowReleaseMagic : NetworkReleaseMagic, _character.photonView.ViewID },
				new RaiseEventOptions { Receivers = ReceiverGroup.Others },
				new SendOptions { Reliability = true });
		}
		catch (Exception ex)
		{
			LogError("RavenController.SendNetworkRelease", ex);
		}
	}

	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
			&& !float.IsNaN(value.y) && !float.IsInfinity(value.y)
			&& !float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}

	internal static void TickRemoteVisuals()
	{
		// Keep the disk-to-scene Condor upgrade while it is in use, without
		// repeatedly loading resources from idle module ticks.
		bool needsCondor = RavenPlugin.IsActive;
		if (!needsCondor)
			foreach (RemoteRiderState rider in RemoteRiders.Values)
				if (!rider.Crow) { needsCondor = true; break; }
		if (needsCondor) EnsureVisualTemplate();
		List<int> expired = null;
		foreach (KeyValuePair<int, RemoteRiderState> pair in RemoteRiders)
		{
			RemoteRiderState state = pair.Value;
			if (!PhotonNetwork.InRoom || state.Character == null || Time.unscaledTime - state.LastSeen > RemoteRiderTimeout)
			{
				if (expired == null) expired = new List<int>();
				expired.Add(pair.Key);
				continue;
			}
			if (state.Visual == null) state.Visual = CreateCondorVisual(state.Position, state.Rotation, state.Crow);
			if (state.Visual == null) continue;
			UnityEngine.Transform visual = state.Visual.transform;
			float blend = 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime);
			bool teleported = Vector3.Distance(visual.position, state.Position) > 3f;
			visual.position = teleported ? state.Position : Vector3.Lerp(visual.position, state.Position, blend);
			if (teleported) ClearWingTrails(state.Visual);
			visual.rotation = Quaternion.Slerp(visual.rotation, state.Rotation, blend);
			ApplyFlightPresentation(state.Visual, Time.unscaledTime - state.PoseTime < 0.5f ? state.Velocity : Vector3.zero, state.Carrying, Time.unscaledDeltaTime, state.Grounded);
			SetCharacterRenderers(state.Character, false);
			state.Hidden = true;
		}
		if (expired != null) foreach (int id in expired) ReleaseRemoteRider(id);
	}

	internal static void ClearRemoteVisuals()
	{
		foreach (RemoteRiderState state in RemoteRiders.Values) RestoreRemoteRider(state);
		RemoteRiders.Clear();
		if (_visualTemplate != null) UnityObject.Destroy(_visualTemplate);
		_visualTemplate = null;
		_templateFromScene = false;
		CondorVisualAsset.Clear();
		CrowVisualAsset.Clear();
		_loggedTemplateFailure = false;
		_nextTemplateProbe = 0f;
	}

	// CharacterSyncer transmits the hip, not the hand. The victim's client needs a
	// kinematic hand at the talons too, otherwise the joint merely drags the remote arm.
	internal static void UpdateRemoteGrabAnchor(Character character)
	{
		if (character == null || character.IsLocal || character.photonView == null) return;
		if (!RemoteRiders.TryGetValue(character.photonView.ViewID, out RemoteRiderState state)) return;
		if (state.Character != character || Time.unscaledTime - state.LastSeen > RemoteRiderTimeout) return;
		if (state.Hand == null)
		{
			state.Hand = FindPartRig(character, BodypartType.Hand_R);
			if (state.Hand == null) return;
			state.HandWasKinematic = state.Hand.isKinematic;
			state.HandLocalPosition = character.transform.InverseTransformPoint(state.Hand.position);
		}
		state.Hand.isKinematic = true;
		Vector3 anchor = state.Position + state.Rotation * _grabAnchorLocal;
		state.Hand.position = anchor;
		state.Hand.transform.position = anchor;
		Character carried = GetGrabbedPlayer(character.data);
		if (carried != null && carried.IsLocal) LimitFalling(carried);
	}

	private static void TrackRemoteRider(int characterViewId, bool bodyVisible)
	{
		if (characterViewId <= 0)
		{
			return;
		}
		Character character = null;
		PhotonView characterView = PhotonView.Find(characterViewId);
		if (characterView != null)
		{
			character = characterView.GetComponent<Character>();
		}
		if (character == null)
		{
			return;
		}
		RemoteRiderState state;
		if (!RemoteRiders.TryGetValue(characterViewId, out state))
		{
			state = new RemoteRiderState { Character = character };
			RemoteRiders[characterViewId] = state;
		}
		else
		{
			state.Character = character;
		}
		state.BodyVisible = bodyVisible;
		state.LastSeen = Time.unscaledTime;
	}

	/// <summary>Called from the CharacterSyncer prefix on every modded client: returns true to
	/// skip the ragdoll interpolation for a remote raven owner, and keeps their body hidden.
	/// Entries are dropped on an explicit release event or when sync events stop arriving.</summary>
	internal static bool IsHiddenRemoteRider(Character character)
	{
		if (character == null || character.IsLocal || character.photonView == null)
		{
			return false;
		}
		int characterViewId = character.photonView.ViewID;
		RemoteRiderState state;
		if (!RemoteRiders.TryGetValue(characterViewId, out state))
		{
			return false;
		}
		if (state.Character != character)
		{
			RestoreRemoteRider(state);
			RemoteRiders.Remove(characterViewId);
			return false;
		}
		if (Time.unscaledTime - state.LastSeen > RemoteRiderTimeout)
		{
			RestoreRemoteRider(state);
			RemoteRiders.Remove(characterViewId);
			return false;
		}
		// Missing/changed assets must leave the ordinary remote player visible.
		if (state.Visual == null) return false;
		if (!state.Hidden)
		{
			SetCharacterRenderers(character, false);
			state.Hidden = true;
		}
		// Current senders always use real positions: let vanilla interpolation follow
		// those, keeping visibility separate. Only legacy senders bury their wire pose.
		return !state.BodyVisible;
	}

	private static void ReleaseRemoteRider(int characterViewId)
	{
		RemoteRiderState state;
		if (!RemoteRiders.TryGetValue(characterViewId, out state))
		{
			return;
		}
		RestoreRemoteRider(state);
		RemoteRiders.Remove(characterViewId);
	}

	private static void RestoreRemoteRider(RemoteRiderState state)
	{
		if (state != null && state.Visual != null) UnityObject.Destroy(state.Visual);
		if (state != null && state.Hand != null)
		{
			if (state.Character != null)
			{
				Vector3 position = state.Character.transform.TransformPoint(state.HandLocalPosition);
				state.Hand.position = position;
				state.Hand.transform.position = position;
			}
			state.Hand.isKinematic = state.HandWasKinematic;
			state.Hand = null;
		}
		if (state != null && state.Character != null && state.Hidden)
		{
			SetCharacterRenderers(state.Character, true);
		}
	}

	private static void SetCharacterRenderers(Character character, bool enabled)
	{
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
		catch
		{
		}
	}

	// ------------------------------------------------------------------
	// Camera (same third-person recipe as the ghost/tumbleweed forms).
	// ------------------------------------------------------------------

	internal static void ApplyCameraOverrideForLocalRaven()
	{
		Character character = ActiveRavenCharacter;
		if (character == null)
		{
			return;
		}
		RavenController controller = ((Component)character).GetComponent<RavenController>();
		if (controller != null && controller.IsValid())
		{
			controller.RefreshCamera();
		}
	}

	private void PreparePresentation()
	{
		if (_lastPresentationFrame == Time.frameCount) return;
		_lastPresentationFrame = Time.frameCount;
		float fraction = Time.fixedDeltaTime > 0f ? (Time.time - Time.fixedTime) / Time.fixedDeltaTime : 1f;
		_renderCenter = _flightMotion.RenderPosition(fraction);
		UpdateRavenVisual();
	}

	private void RefreshCamera()
	{
		if (global::Transform.Core.ThirdPartyCameras.ExternalCameraActive)
		{
			_cameraRig.Reset();
			_cameraInitialized = false;
			return;
		}
		if (_lastCameraFrame == Time.frameCount) return;
		try
		{
			Camera camera = Camera.main;
			if (camera == null) return;
			_lastCameraFrame = Time.frameCount;
			PreparePresentation();
			Vector3 forward = GetCameraForward(_character);
			Vector3 lookDirection = _character.data != null ? _character.data.lookDirection.normalized : forward;
			float verticalLook = Mathf.Clamp(lookDirection.y, -0.35f, 0.65f);
			float height = GetCameraHeight();
			Vector3 cameraOffset = Vector3.up * (height + 1f) - forward * GetCameraDistance();
			Vector3 lookOffset = Vector3.up * (height + 0.6f + verticalLook * 2f) + forward * CameraLookAhead;
			_cameraRig.Step(_renderCenter, cameraOffset, lookOffset, Time.deltaTime);
			Quaternion desiredRotation = Quaternion.LookRotation(_cameraRig.LookDirection.normalized, Vector3.up);
			if (!_cameraInitialized)
			{
				_cameraSmoothedRotation = desiredRotation;
				_cameraSmoothedFov = camera.fieldOfView;
				_cameraInitialized = true;
			}
			else
			{
				float rotationT = 1f - Mathf.Exp(-CameraRotationSharpness * Time.deltaTime);
				_cameraSmoothedRotation = Quaternion.Slerp(_cameraSmoothedRotation, desiredRotation, rotationT);
			}
			camera.transform.SetPositionAndRotation(_cameraRig.Position, _cameraSmoothedRotation);
			_cameraSmoothedFov = Mathf.Lerp(_cameraSmoothedFov, GetCameraFov(), 1f - Mathf.Exp(-4f * Time.deltaTime));
			camera.fieldOfView = _cameraSmoothedFov;
		}
		catch (Exception ex)
		{
			LogError("RavenController.RefreshCamera", ex);
		}
	}

	private float GetCameraDistance()
	{
		float value = Settings.CameraDistance != null ? Settings.CameraDistance.Value : DefaultCameraDistance;
		return Mathf.Clamp(value, 3f, 15f);
	}

	private float GetCameraHeight()
	{
		float value = Settings.CameraHeight != null ? Settings.CameraHeight.Value : DefaultCameraHeight;
		return Mathf.Clamp(value, 0.3f, 8f);
	}

	private float GetCameraFov()
	{
		float value = Settings.CameraFov != null ? Settings.CameraFov.Value : DefaultCameraFov;
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

}
