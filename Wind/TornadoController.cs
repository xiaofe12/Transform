using System;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using PhotonPlayer = Photon.Realtime.Player;

namespace ImTornado;

[DefaultExecutionOrder(600)]
public sealed class TornadoController : MonoBehaviour
{
	private const float HoverLerpRate = 4f;
	private const float HoverSnapDistance = 1.2f;
	private const float HoverMaxClimbSpeed = 6f;
	private const float VelocitySharpness = 7f;
	private const float MaxVelocity = 34f;
	private const float AngularVelocityDamping = 0.92f;
	private const float MaxAngularVelocity = 10f;
	private const float SpinAngularSpeed = 3.2f;
	private const float DefaultCameraDistance = 18f;
	private const float DefaultCameraHeight = 6f;
	private const float CameraLookAhead = 5f;
	private const float CameraCollisionRadius = 0.35f;
	private const float CameraTerrainClearance = 0.8f;
	private const float CameraSmoothTime = 0.08f;
	private const float CameraRotationSharpness = 12f;
	private const float DefaultCameraFov = 82f;

	// Unmodded-room fallback ("waypoint mode") tuning. The vanilla Tornado AI
	// chases its selected waypoint at 15 m/s, and the vanilla attract/capture
	// forces run every FixedUpdate — our RPC fallbacks are throttled, so they
	// scale the impulse to keep the same net acceleration.
	private const float WaypointSyncInterval = 0.4f;
	private const float PushInterval = 0.1f;
	private const float FallRpcInterval = 0.5f;
	private const float VanillaChaseSpeed = 15f;
	private const float DefaultTornadoRange = 25f;
	private const float DefaultCaptureDistance = 10f;

	private Character _character;
	private Vector3 _prevCenter;
	private Vector3 _cameraVelocity;
	private Vector3 _cameraSmoothedPosition;
	private Quaternion _cameraSmoothedRotation;
	private bool _cameraHasSmoothedPosition;
	private float _lastGroundY;
	private GameObject _tornadoVisualRoot;
	private PhotonView _tornadoPhotonView;
	private bool _tornadoIsNetworked;
	private float _nextTornadoVisualSync;

	// Waypoint mode state (see UpdateWaypointFollow / PushUnmoddedPlayers).
	private bool _waypointMode;
	private PhotonView _spawnerView;
	private float _nextWaypointSyncTime;
	private float _nextPushTime;
	private float _nextFallRpcTime;
	private Vector3 _lastVisualPos;
	private float _lastVisualPosTime;

	internal const string NetworkVisualMarker = "ImTornado.Visual";
	private const float NetworkVisualSyncInterval = 0.25f;

	/// <summary>The game's Tornado component on our spawned networked tornado, or null.
	/// Used by Harmony patches to identify our tornado and suppress its AI.</summary>
	public static Tornado ActiveTornadoAI { get; private set; }

	/// <summary>The local character currently in tornado form, or null. Harmony patches
	/// use this to keep the tornado player out of the vanilla AI's own push lists.</summary>
	public static Character ActiveTornadoCharacter { get; private set; }

	public bool Active { get; private set; }

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

	private static void LogInfo(string message) => WindPlugin.Log?.LogInfo("[I'm a Tornado] " + message);
	private static void LogError(string entryPoint, Exception ex) => WindPlugin.Log?.LogError("[I'm a Tornado] " + entryPoint + ": " + ex);

	/// <summary>True while the local character is in tornado form and being driven by this controller.</summary>
	public static bool IsLocalTornadoCharacter(Character character)
	{
		if (character == null || !character.IsLocal)
		{
			return false;
		}
		TornadoController controller = ((Component)character).GetComponent<TornadoController>();
		return controller != null && controller.Active;
	}

	public void EnterTornado(Character character)
	{
		_character = character;
		_prevCenter = character.Center;
		_cameraVelocity = Vector3.zero;
		_cameraHasSmoothedPosition = false;
		_lastVisualPosTime = 0f;
		// Seed the ground reference before the visual is built so the funnel is
		// grounded from the very first frame (ApplyHoverVelocity refreshes it every
		// physics step afterwards).
		float groundY = GetGroundHeight(character.Center);
		_lastGroundY = float.IsNaN(groundY) ? character.Center.y : groundY;
		Active = true;
		ActiveTornadoCharacter = character;
		enabled = true;
		SetTornadoNoClip(true);
		BuildTornadoVisual();
		HideHud();
		LogInfo("Entered tornado form.");
	}

	public void ExitTornado()
	{
		Active = false;
		ActiveTornadoCharacter = null;
		enabled = false;
		SetTornadoNoClip(false);
		DestroyTornadoVisual();
		RestoreHud();
		LogInfo("Exited tornado form.");
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
			UpdateTornadoVisual();
			// Keep looking for the HUD canvases: they may spawn after we transform
			// (e.g. after a scene load), and we want them hidden the whole time.
			HideHud();
		}
		catch (Exception ex)
		{
			LogError("TornadoController.Update", ex);
		}
	}

	private void FixedUpdate()
	{
		if (!Active || _character == null || _character.refs == null || _character.refs.ragdoll == null)
		{
			return;
		}

		try
		{
			if (!global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
			{
				ApplyMovementVelocity();
			}
			ApplyHoverVelocity();
			ApplySpinningMotion();
			StabilizeRagdoll();
			PushUnmoddedPlayers();
		}
		catch (Exception ex)
		{
			LogError("TornadoController.FixedUpdate", ex);
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
			RefreshCamera();
		}
		catch (Exception ex)
		{
			LogError("TornadoController.LateUpdate", ex);
		}
	}

	private void OnDestroy()
	{
		if (Active)
		{
			Active = false;
			ActiveTornadoCharacter = null;
			SetTornadoNoClip(false);
			DestroyTornadoVisual();
			RestoreHud();
		}
	}

	// ------------------------------------------------------------------
	// Collision: while flying the tornado glides through walls, players and
	// props, so the ragdoll colliders are turned off (mirrors how the game
	// handles carried players and how the I'm a Ghost mod flies). Unlike a
	// ghost we keep the rigidbodies simulated (non-kinematic) because our
	// movement writes their velocities directly every FixedUpdate; ToggleCollision
	// only toggles colliders and leaves kinematics untouched, so it is safe here.
	// ------------------------------------------------------------------

	/// <summary>
	/// enable=true: break any carry and disable the ragdoll colliders so nothing blocks
	/// the flight path. enable=false: restore colliders, re-enable gravity and halt
	/// leftover spin/fling so the restored player drops cleanly instead of shooting off.
	/// </summary>
	private void SetTornadoNoClip(bool enable)
	{
		if (_character == null || _character.refs == null || _character.refs.ragdoll == null)
		{
			return;
		}

		CharacterRagdoll ragdoll = _character.refs.ragdoll;
		try
		{
			if (enable && _character.data != null)
			{
				// Same as So Fly's takeoff and I'm a Ghost: drop whoever we are carrying
				// (or whoever is carrying us) before going no-clip, so nobody gets dragged
				// through walls by the tornado. BreakCharacterCarrying is the game's own
				// public API for both directions.
				_character.BreakCharacterCarrying(true);
			}

			ragdoll.ToggleCollision(!enable);

			if (ragdoll.partList != null)
			{
				foreach (Bodypart part in ragdoll.partList)
				{
					Rigidbody rig = part != null ? part.Rig : null;
					if (rig == null || rig.isKinematic)
					{
						continue;
					}
					rig.useGravity = !enable;
				}
			}

			if (!enable)
			{
				ragdoll.HaltBodyVelocity(false);
			}
		}
		catch (Exception ex)
		{
			LogError("TornadoController.SetTornadoNoClip(" + enable + ")", ex);
		}
	}

	// ------------------------------------------------------------------
	// HUD: the shared Transform filter hides every HUD element INCLUDING the
	// status bar while transformed (the tornado doesn't run on the player's
	// real stamina) and restores them on exit — see Core/TransformHud.cs.
	// It re-checks late-spawning canvases itself.
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
		if (raw.sqrMagnitude <= 0.0001f) raw = Transform.Core.GameInput.Move();
		raw.x = Mathf.Clamp(raw.x, -1f, 1f);
		raw.y = Mathf.Clamp(raw.y, -1f, 1f);
		return raw;
	}

	// ------------------------------------------------------------------
	// Physics: WASD gliding, hover, spin, ragdoll stabilization
	// ------------------------------------------------------------------

	private void ApplyMovementVelocity()
	{
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

		float speed = WindPlugin.MovementSpeed.Value;
		Vector3 desiredVelocity = move * speed;

		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null || rig.isKinematic)
			{
				continue;
			}
			Vector3 currentVelocity = rig.linearVelocity;
			Vector3 horizontal = currentVelocity;
			horizontal.y = 0f;
			Vector3 targetHorizontal = Vector3.Lerp(horizontal, desiredVelocity, Time.fixedDeltaTime * VelocitySharpness);
			Vector3 newVelocity = targetHorizontal + Vector3.up * currentVelocity.y;
			rig.linearVelocity = Vector3.ClampMagnitude(newVelocity, MaxVelocity);
		}
	}

	private void ApplyHoverVelocity()
	{
		if (_character == null)
		{
			return;
		}

		Vector3 center = _character.Center;
		float groundY = GetGroundHeight(center);
		if (float.IsNaN(groundY))
		{
			groundY = _lastGroundY;
		}
		else
		{
			_lastGroundY = groundY;
		}

		float targetY = groundY + WindPlugin.HoverHeight.Value;
		float delta = targetY - center.y;

		float verticalVelocity = 0f;
		if (Mathf.Abs(delta) > HoverSnapDistance)
		{
			verticalVelocity = Mathf.Clamp(delta * HoverLerpRate, -HoverMaxClimbSpeed, HoverMaxClimbSpeed);
		}

		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null || rig.isKinematic)
			{
				continue;
			}
			Vector3 v = rig.linearVelocity;
			v.y = verticalVelocity;
			rig.linearVelocity = v;
		}
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

	private void ApplySpinningMotion()
	{
		if (_character == null || _character.refs == null || _character.refs.ragdoll == null)
		{
			return;
		}

		Vector3 center = _character.Center;
		float spin = SpinAngularSpeed;

		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null || rig.isKinematic)
			{
				continue;
			}

			Vector3 offset = rig.position - center;
			offset.y = 0f;
			if (offset.sqrMagnitude > 0.0001f)
			{
				Vector3 tangent = Vector3.Cross(Vector3.up, offset.normalized).normalized;
				Vector3 orbitalVelocity = tangent * spin;
				Vector3 current = rig.angularVelocity;
				current.y = Mathf.Lerp(current.y, orbitalVelocity.y + spin, Time.fixedDeltaTime * 2f);
				current.x = Mathf.Lerp(current.x, orbitalVelocity.x, Time.fixedDeltaTime * 2f);
				current.z = Mathf.Lerp(current.z, orbitalVelocity.z, Time.fixedDeltaTime * 2f);
				rig.angularVelocity = Vector3.ClampMagnitude(current, MaxAngularVelocity);
			}
			else
			{
				rig.angularVelocity = Vector3.Lerp(rig.angularVelocity, Vector3.up * spin, Time.fixedDeltaTime * 2f);
			}
		}
	}

	private void StabilizeRagdoll()
	{
		if (_character.refs.ragdoll == null)
		{
			return;
		}

		// Note: we deliberately do NOT force currentRagdollControll to zero here.
		// The game's animation system re-raises it toward 1 every frame and pulls
		// the body into its normal pose; we let it do that so the character keeps
		// a readable human pose inside the funnel, and instead win each physics
		// frame by overwriting linear/angular velocity after the game's forces.

		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null || rig.isKinematic)
			{
				continue;
			}

			rig.useGravity = false;
			// Discard any force the game's movement/gravity accumulated this physics
			// step (CharacterMovement.FixedUpdate ran before us), so our velocity
			// override below is what actually integrates.
			part.forcesToAdd = Vector3.zero;
			rig.angularVelocity *= AngularVelocityDamping;
			if (rig.angularVelocity.magnitude > MaxAngularVelocity)
			{
				rig.angularVelocity = Vector3.ClampMagnitude(rig.angularVelocity, MaxAngularVelocity);
			}
			if (rig.linearVelocity.y < -MaxVelocity)
			{
				Vector3 v = rig.linearVelocity;
				v.y = -MaxVelocity;
				rig.linearVelocity = v;
			}
		}
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

	// ------------------------------------------------------------------
	// Networked tornado visual: spawned via PhotonNetwork.Instantiate so the
	// vanilla "Tornado" prefab exists on every client.  Modded clients identify
	// this instance through PhotonView.InstantiationData and Harmony-suppress the
	// vanilla Tornado AI/lifetime/push behaviour.  Unmodded clients still run the
	// vanilla script because they cannot receive our patches; there is no vanilla
	// RPC that disables Tornado AI or directly sets its transform.
	// ------------------------------------------------------------------

	private void BuildTornadoVisual()
	{
		if (_tornadoVisualRoot != null || _character == null)
		{
			return;
		}

		try
		{
			GameObject prefab = Resources.Load<GameObject>("Tornado");
			if (prefab == null)
			{
				LogInfo("Resources.Load(\"Tornado\") returned null; no tornado visual will be shown.");
				return;
			}

			if (PhotonNetwork.InRoom && _character.photonView != null && _character.photonView.ViewID > 0)
			{
				BuildNetworkedTornado(prefab);
			}
			else
			{
				BuildLocalTornado(prefab);
			}
		}
		catch (Exception ex)
		{
			LogError("TornadoController.BuildTornadoVisual", ex);
			DestroyTornadoVisual();
		}
	}

	private void BuildNetworkedTornado(GameObject prefab)
	{
		GameObject instance = PhotonNetwork.Instantiate(
			"Tornado",
			GetTornadoVisualPosition(),
			Quaternion.identity,
			0,
			new object[] { NetworkVisualMarker });
		instance.name = "WindTornadoNetworked";
		_tornadoVisualRoot = instance;
		_tornadoIsNetworked = true;
		// Evaluate before the force-sync below: it decides whether the very first
		// RPCA_SyncTornado is a broadcast (all modded) or routed per player.
		_waypointMode = WindPlugin.UnmoddedRoomSupport.Value && WindPlugin.RoomHasUnmoddedPlayers();

		ActiveTornadoAI = instance.GetComponent<Tornado>();

		_tornadoPhotonView = instance.GetComponent<PhotonView>();
		instance.transform.localScale = Vector3.one;

		instance.SetActive(true); // Tornado visual always shown (config removed).
		SyncNetworkTornadoVisual(force: true);
		LogInfo("Spawned networked tornado visual with vanilla Tornado prefab"
			+ (_waypointMode ? " (waypoint follow for unmodded players)." : "."));
	}

	private void BuildLocalTornado(GameObject prefab)
	{
		GameObject instance = UnityObject.Instantiate(prefab, GetTornadoVisualPosition(), Quaternion.identity);
		instance.name = "WindTornadoVisual";

		// Strip gameplay scripts so the local-only visual never pushes players.
		// We disable each component immediately (so nothing can run/push this frame) and
		// defer the actual destruction to end of frame with Destroy instead of DestroyImmediate,
		// which is unsafe to call mid-frame.
		foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
		{
			if (behaviour == null)
			{
				continue;
			}
			Type type = behaviour.GetType();
			if (type == typeof(Animator) || type == typeof(ParticleSystem))
			{
				continue;
			}
			behaviour.enabled = false;
			UnityObject.Destroy(behaviour);
		}
		foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
		{
			collider.enabled = false;
			UnityObject.Destroy(collider);
		}

		instance.transform.localScale = Vector3.one;
		foreach (ParticleSystem particleSystem in instance.GetComponentsInChildren<ParticleSystem>(true))
		{
			ParticleSystem.MainModule mainModule = particleSystem.main;
			mainModule.playOnAwake = true;
			particleSystem.Play();
		}

		_tornadoVisualRoot = instance;
		_tornadoIsNetworked = false;
		instance.SetActive(true); // Tornado visual always shown (config removed).
		LogInfo("Spawned local-only tornado visual (offline / single-player).");
	}

	private void UpdateTornadoVisual()
	{
		if (_tornadoVisualRoot == null || _character == null)
		{
			return;
		}

		_tornadoVisualRoot.transform.position = GetTornadoVisualPosition();
		_tornadoVisualRoot.transform.localScale = Vector3.one;

		if (_tornadoIsNetworked)
		{
			// Waypoint tick first: it (re)evaluates _waypointMode, which decides
			// how the sync tick below routes its RPCs.
			UpdateWaypointFollow();
			SyncNetworkTornadoVisual();
		}
	}

	/// <summary>
	/// The vanilla Tornado prefab's origin is its ground contact point: the funnel mesh
	/// rises ~146m above it and the base swirl disc (radius ~22m) sits at y≈4-11m
	/// (measured from the prefab via UnityPy). Ground the root on the terrain under the
	/// player like the vanilla Tornado.Movement() does, so the funnel touches the ground
	/// and the hovering player (default HoverHeight 7m) ends up centered inside the base
	/// swirl — the tornado's core — instead of hanging off a funnel that floats 7m up.
	/// </summary>
	private Vector3 GetTornadoVisualPosition()
	{
		Vector3 center = _character.Center;
		return new Vector3(center.x, _lastGroundY, center.z);
	}

	private void DestroyTornadoVisual()
	{
		ActiveTornadoAI = null;
		_tornadoPhotonView = null;
		_waypointMode = false;
		_spawnerView = null;

		if (_tornadoVisualRoot != null)
		{
			if (_tornadoIsNetworked && PhotonNetwork.InRoom)
			{
				try
				{
					PhotonNetwork.Destroy(_tornadoVisualRoot);
				}
				catch
				{
					// If we don't own it anymore or it's already gone, fall back silently.
				}
			}
			else
			{
				Destroy(_tornadoVisualRoot);
			}
		}
		_tornadoVisualRoot = null;
		_tornadoIsNetworked = false;
	}

	private void SyncNetworkTornadoVisual(bool force = false)
	{
		if (_tornadoPhotonView == null || _character == null || !PhotonNetwork.InRoom)
		{
			return;
		}
		if (!force && Time.unscaledTime < _nextTornadoVisualSync)
		{
			return;
		}

		_nextTornadoVisualSync = Time.unscaledTime + NetworkVisualSyncInterval;
		try
		{
			Vector3 visualPos = _tornadoVisualRoot.transform.position;
			if (!_waypointMode)
			{
				// Everyone is modded: broadcast the visual position. Modded clients
				// patch this vanilla RPC and interpret the Vector3 as the visual
				// position (the grounded root, not the hovering Center); with no
				// waypoint target set anywhere, the vel write on vanilla clients
				// is inert.
				_tornadoPhotonView.RPC("RPCA_SyncTornado", RpcTarget.All, visualPos);
				return;
			}

			// Waypoint mode: route the position only to modded players. Unmodded
			// clients must never receive a position vector here — their vanilla
			// RPCA_SyncTornado assigns it to vel, which Movement() integrates, so
			// a world-position-sized vel would fling their funnel across the map.
			// They get velocity-seeded syncs in UpdateWaypointFollow instead.
			foreach (PhotonPlayer player in PhotonNetwork.PlayerList)
			{
				if (player == null || player.IsLocal || !WindPlugin.PlayerHasMod(player))
				{
					continue;
				}
				_tornadoPhotonView.RPC("RPCA_SyncTornado", player, visualPos);
			}
		}
		catch (Exception ex)
		{
			LogError("TornadoController.SyncNetworkTornadoVisual", ex);
		}
	}

	// ------------------------------------------------------------------
	// Unmodded-room fallback ("waypoint mode"). Unmodded clients run the
	// vanilla Tornado script: Movement() only moves the funnel toward the
	// waypoint selected through the vanilla RPCA_InitTornado /
	// RPCA_SelectTargetPos channel (15 m/s ground chase). While unmodded
	// players are in the room we repeatedly broadcast the waypoint nearest
	// to our position so their funnel follows us around.
	// ------------------------------------------------------------------

	private void UpdateWaypointFollow()
	{
		if (!WindPlugin.UnmoddedRoomSupport.Value)
		{
			_waypointMode = false;
			return;
		}

		if (Time.unscaledTime < _nextWaypointSyncTime)
		{
			return;
		}
		_nextWaypointSyncTime = Time.unscaledTime + WaypointSyncInterval;

		// Re-evaluated every tick: an unmodded player joining or leaving flips
		// the mode live, and per-player RPC routing adapts on the next sync.
		_waypointMode = WindPlugin.RoomHasUnmoddedPlayers();
		if (!_waypointMode || _tornadoPhotonView == null || !PhotonNetwork.InRoom)
		{
			return;
		}

		if (_spawnerView == null)
		{
			FindTornadoSpawner();
			if (_spawnerView == null)
			{
				// Level has no TornadoSpawner: the funnel stays put for unmodded
				// players (the real-push RPCs below still work).
				return;
			}
		}

		UnityEngine.Transform points = _spawnerView.transform != null ? _spawnerView.transform.Find("TornadoPoints") : null;
		if (points == null || points.childCount == 0)
		{
			return;
		}

		// Pick the waypoint nearest to the funnel's grounded position.
		Vector3 visualPos = GetTornadoVisualPosition();
		int nearest = 0;
		float nearestSqr = float.MaxValue;
		for (int i = 0; i < points.childCount; i++)
		{
			UnityEngine.Transform waypoint = points.GetChild(i);
			if (waypoint == null)
			{
				continue;
			}
			Vector3 offset = waypoint.position - visualPos;
			offset.y = 0f;
			float sqr = offset.sqrMagnitude;
			if (sqr < nearestSqr)
			{
				nearestSqr = sqr;
				nearest = i;
			}
		}

		// Seed unmodded clients' vel with our real (capped) velocity: keeps their
		// vanilla Movement() simulation well-behaved, heals the normal→waypoint
		// mode transition (earlier broadcasts stored a position-sized vel), and
		// converges late joiners.
		Vector3 velocity = GetVisualVelocity();
		foreach (PhotonPlayer player in PhotonNetwork.PlayerList)
		{
			if (player == null || player.IsLocal || WindPlugin.PlayerHasMod(player))
			{
				continue;
			}
			_tornadoPhotonView.RPC("RPCA_SyncTornado", player, velocity);
		}

		// The vanilla channel that arms and steers the unmodded clients' funnel
		// AI. InitTornado is idempotent and both are resent every tick, so late
		// joiners converge without relying on buffered RPCs.
		_tornadoPhotonView.RPC("RPCA_InitTornado", RpcTarget.All, _spawnerView.ViewID);
		_tornadoPhotonView.RPC("RPCA_SelectTargetPos", RpcTarget.All, nearest);
	}

	private void FindTornadoSpawner()
	{
		TornadoSpawner spawner = UnityObject.FindFirstObjectByType<TornadoSpawner>();
		PhotonView spawnerView = spawner != null ? ((Component)spawner).GetComponent<PhotonView>() : null;
		if (spawnerView != null && spawnerView.ViewID > 0)
		{
			_spawnerView = spawnerView;
			LogInfo("Waypoint follow armed via scene TornadoSpawner (view ID " + spawnerView.ViewID + ").");
		}
	}

	private Vector3 GetVisualVelocity()
	{
		Vector3 pos = GetTornadoVisualPosition();
		Vector3 velocity = Vector3.zero;
		if (_lastVisualPosTime > 0f && Time.time > _lastVisualPosTime)
		{
			velocity = (pos - _lastVisualPos) / (Time.time - _lastVisualPosTime);
		}
		_lastVisualPos = pos;
		_lastVisualPosTime = Time.time;
		velocity.y = 0f;
		return Vector3.ClampMagnitude(velocity, VanillaChaseSpeed);
	}

	// ------------------------------------------------------------------
	// Real pushes for unmodded rooms. The vanilla attract/capture forces are
	// simulated locally on every client, but an unmodded client's funnel only
	// follows waypoints — a player standing next to the REAL tornado position
	// may be nowhere near that client's simulated funnel. So we re-issue the
	// game's own networked force channel (RPCA_AddForceToBodyPart, the same
	// RPC the game's hazards use) against unmodded players near our actual
	// position. Modded players are skipped: their clients already run the
	// vanilla push simulation against the exact synced funnel position.
	// ------------------------------------------------------------------

	private void PushUnmoddedPlayers()
	{
		if (!_tornadoIsNetworked || _character == null)
		{
			return;
		}
		if (Time.unscaledTime < _nextPushTime)
		{
			return;
		}
		_nextPushTime = Time.unscaledTime + PushInterval;

		float force = WindPlugin.PushForce.Value;
		if (force <= 0f)
		{
			return;
		}
		List<Character> characters = Character.AllCharacters;
		if (characters == null || characters.Count == 0)
		{
			return;
		}

		Tornado ai = ActiveTornadoAI;
		float range = ai != null ? ai.range : DefaultTornadoRange;
		float captureDistance = ai != null ? ai.captureDistance : DefaultCaptureDistance;
		Vector3 visualPos = GetTornadoVisualPosition();
		bool fallDue = Time.unscaledTime >= _nextFallRpcTime;
		// Vanilla applies these forces every FixedUpdate; this RPC channel runs
		// every PushInterval, so scale the magnitude to keep the same net
		// acceleration (Acceleration mode: dv = force * fixedDeltaTime per hit).
		float throttleScale = PushInterval / Mathf.Max(Time.fixedDeltaTime, 0.0001f);

		foreach (Character target in characters)
		{
			if (target == null || target.IsLocal || target == _character)
			{
				continue;
			}
			if (target.photonView == null || WindPlugin.PlayerHasMod(target.photonView.Owner))
			{
				continue;
			}
			if (target.data == null || target.data.dead)
			{
				continue;
			}

			Vector3 offset = target.Center - visualPos;
			float height = offset.y;
			offset.y = 0f;
			float distance = offset.magnitude;
			if (distance > range || height < -10f || height > 50f)
			{
				continue;
			}

			// Mirror of the vanilla AttractCharacters() falloff (same curves the
			// spawned prefab carries, same crouch resistance).
			Vector3 toFunnel = distance > 0.001f ? -offset / distance : Vector3.zero;
			float proximity = Mathf.Clamp01(1f - distance / range);
			float inStrength = ai != null && ai.inStrC != null ? ai.inStrC.Evaluate(proximity) : proximity;
			float upStrength = ai != null && ai.upStrC != null ? ai.upStrC.Evaluate(proximity) : proximity;
			float crouchMultiplier = target.data.isCrouching ? 0.25f : 1f;

			Vector3 wholeBody = toFunnel * (force * inStrength * 1.2f) + Vector3.up * (force * upStrength);
			wholeBody *= crouchMultiplier;

			bool captured = distance < captureDistance && !target.IsStuck();
			if (captured)
			{
				// Mirror of the vanilla CapturedCharacter() forces: orbit tangent,
				// gentle pull toward the orbit spot, and the signature lift.
				Vector3 radial = distance > 0.001f ? offset / distance * VanillaChaseSpeed : Vector3.zero;
				Vector3 orbitSpot = visualPos + radial;
				float groundY = GetGroundHeight(orbitSpot);
				if (!float.IsNaN(groundY) && groundY > orbitSpot.y)
				{
					orbitSpot.y = groundY;
				}
				Vector3 toOrbit = orbitSpot - target.Center;
				toOrbit.y = 0f;
				wholeBody += Vector3.Cross(Vector3.up, radial).normalized * force
				             + toOrbit * (force * 0.2f)
				             + Vector3.up * (19f + Mathf.Abs(height));
			}

			target.photonView.RPC("RPCA_AddForceToBodyPart", RpcTarget.All,
				BodypartType.Torso, Vector3.zero, wholeBody * throttleScale);

			if (captured && fallDue)
			{
				// The vanilla capture also trips its victim; same args the game uses.
				target.photonView.RPC("RPCA_Fall", RpcTarget.All, 0.5f, 0f);
			}
		}

		if (fallDue)
		{
			_nextFallRpcTime = Time.unscaledTime + FallRpcInterval;
		}
	}

	// ------------------------------------------------------------------
	// Camera: MainCameraMovement.LateUpdate (DefaultExecutionOrder 500) runs
	// first and pulls the camera to the ragdoll head. This controller runs
	// at order 600, so it overrides the camera onto the flying tornado last.
	// ------------------------------------------------------------------

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

			Vector3 lookTarget = GetThirdPersonCameraLookTarget(_character);
			Vector3 desiredPosition = ResolveCameraCollision(lookTarget, GetThirdPersonCameraPosition(_character));
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
			LogError("TornadoController.RefreshCamera", ex);
		}
	}

	private static Vector3 GetThirdPersonCameraPosition(Character character)
	{
		Vector3 forward = GetCameraForward(character);
		return character.Center
		       + Vector3.up * (GetCameraHeight() + 2f)
		       - forward * GetCameraDistance();
	}

	private static Vector3 GetThirdPersonCameraLookTarget(Character character)
	{
		Vector3 forward = GetCameraForward(character);
		Vector3 lookDirection = character.data != null ? character.data.lookDirection.normalized : forward;
		float verticalLook = Mathf.Clamp(lookDirection.y, -0.35f, 0.65f);
		return character.Center
		       + Vector3.up * (GetCameraHeight() + verticalLook * 3f)
		       + forward * CameraLookAhead;
	}

	private static float GetCameraDistance()
	{
		float value = WindPlugin.CameraDistance != null ? WindPlugin.CameraDistance.Value : DefaultCameraDistance;
		return Mathf.Clamp(value, 8f, 30f);
	}

	private static float GetCameraHeight()
	{
		float value = WindPlugin.CameraHeight != null ? WindPlugin.CameraHeight.Value : DefaultCameraHeight;
		return Mathf.Clamp(value, 2f, 14f);
	}

	private static float GetCameraFov()
	{
		float value = WindPlugin.CameraFov != null ? WindPlugin.CameraFov.Value : DefaultCameraFov;
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

	private Vector3 ResolveCameraCollision(Vector3 lookTarget, Vector3 desiredPosition)
	{
		RaycastHit hit = HelperFunctions.LineCheck(
			lookTarget,
			desiredPosition,
			HelperFunctions.LayerType.TerrainMap,
			CameraCollisionRadius);
		if (hit.transform != null)
		{
			desiredPosition = hit.point + hit.normal * CameraTerrainClearance;
		}

		float groundY = GetGroundHeight(desiredPosition);
		if (!float.IsNaN(groundY) && desiredPosition.y < groundY + CameraTerrainClearance)
		{
			desiredPosition.y = groundY + CameraTerrainClearance;
		}
		return desiredPosition;
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
