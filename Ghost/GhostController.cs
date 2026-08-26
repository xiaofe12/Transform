using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Peak;
using Photon.Pun;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ImGhost;

/// <summary>
/// Runtime controller added to the local player's Character while they are in big-ghost form.
///
/// The ghost is the game's own "GhostBall" prefab (the big ghost that rises from the fog in the
/// misty swamp when the Hazard_BigGhost run setting is on). We spawn it over Photon so every
/// client (including unmodded ones) sees it, suppress its AI with Harmony on modded clients,
/// and drive the owner's rigidbody. The vanilla "PhysicsSyncer" component on the prefab then
/// replicates position/rotation/velocity to every client automatically - no custom RPCs needed.
///
/// Movement is no-clip through walls (like the game's carried-player state): the ragdoll is
/// switched to kinematic with its colliders disabled via CharacterRagdoll.ToggleKinematic /
/// ToggleCollision, and the character root transform is moved directly every FixedUpdate.
///
/// Execution order is set after MainCameraMovement (DefaultExecutionOrder 500) so this
/// controller's LateUpdate can push the camera onto the flying ghost after the vanilla camera
/// code runs, without any Harmony patches.
/// </summary>
[DefaultExecutionOrder(600)]
public sealed class GhostController : MonoBehaviour
{
	private const float GroundClearanceMin = 0.8f;
	private const float BallFaceMoveSharpness = 6f;
	private const float CameraLookAhead = 4f;
	private const float CameraSmoothTime = 0.08f;
	private const float CameraRotationSharpness = 12f;
	private const float DefaultCameraDistance = 16f;
	private const float DefaultCameraHeight = 5f;
	private const float DefaultCameraFov = 80f;

	private Character _character;
	private Vector3 _prevCenter;
	private Vector3 _cameraVelocity;
	private Vector3 _cameraSmoothedPosition;
	private Quaternion _cameraSmoothedRotation;
	private bool _cameraHasSmoothedPosition;
	private Vector3 _moveDirection;

	private GameObject _ghostVisualRoot;
	private GhostBall _ghostBall;
	private Rigidbody _ghostRigidbody;
	private bool _ghostIsNetworked;

	private bool _attacking;
	private float _attackStartedAt;
	private bool _attackExploded;

	internal const string NetworkVisualMarker = "ImGhost.Visual";

	/// <summary>The game's GhostBall component on our spawned ghost, or null.
	/// Used by Harmony patches to identify our ghost and suppress its AI.</summary>
	public static GhostBall ActiveGhostBall { get; private set; }

	/// <summary>The local character currently in ghost form, or null.</summary>
	public static Character ActiveGhostCharacter { get; private set; }

	/// <summary>
	/// True once the camera Harmony fallback (a postfix on MainCameraMovement.LateUpdate)
	/// has been applied. While true, the camera is driven from that postfix - which runs
	/// immediately after the vanilla camera code regardless of execution order - and this
	/// controller's own LateUpdate skips RefreshCamera to avoid applying it twice per frame.
	/// If the patch could not be applied (e.g. the game renamed the method), this stays
	/// false and the execution-order approach (DefaultExecutionOrder 600) remains the fallback.
	/// </summary>
	internal static bool CameraOverridePatchActive { get; set; }

	private static readonly FieldInfo ReadyToExplodeField =
		typeof(GhostBall).GetField("_readyToExplode", BindingFlags.NonPublic | BindingFlags.Instance);
	private static readonly FieldInfo ExplodingField =
		typeof(GhostBall).GetField("exploding", BindingFlags.NonPublic | BindingFlags.Instance);
	private static readonly FieldInfo TickUntilDespawnField =
		typeof(GhostBall).GetField("tickUntilDespawn", BindingFlags.NonPublic | BindingFlags.Instance);

	// Fast ref-returning accessors for the two fields the expression sync reads every
	// frame (no per-call boxing, unlike FieldInfo.GetValue). Null if the Harmony version
	// cannot build them; the FieldInfo fallbacks above still cover that case.
	private static readonly AccessTools.FieldRef<GhostBall, bool> ReadyToExplodeRef =
		MakeFieldRef<GhostBall, bool>("_readyToExplode");
	private static readonly AccessTools.FieldRef<GhostBall, bool> ExplodingRef =
		MakeFieldRef<GhostBall, bool>("exploding");

	private static AccessTools.FieldRef<T, F> MakeFieldRef<T, F>(string name) where T : class
	{
		try
		{
			return AccessTools.FieldRefAccess<T, F>(name);
		}
		catch
		{
			return null;
		}
	}

	// Animator parameter hashes for the ghost's five face/expression bools. Resolved lazily from
	// the GhostBall's own hash fields (AN_READY / AN_FADE / AN_EXPLODE / AN_SAD / AN_BURNING) so we
	// never have to guess the parameter names; falls back to StringToHash if a field is missing.
	private static int _anReady, _anFade, _anExplode, _anSad, _anBurning;
	private static bool _anParamsResolved;

	// Per-ball expression cache: the Animator of the ball currently being driven plus the
	// last values written, so steady-state frames skip both GetComponent and the five
	// native Animator.SetBool calls (the values only change on charge/explode edges).
	// Keyed per GhostBall instance (several players can be ghosts at once) and held via
	// ConditionalWeakTable so entries die with their destroyed balls.
	private sealed class ExpressionState
	{
		public Animator Animator;
		public bool ValuesKnown;
		public bool Ready, Fade, Explode, Sad, Burning;
	}

	private static readonly ConditionalWeakTable<GhostBall, ExpressionState> ExpressionStates =
		new ConditionalWeakTable<GhostBall, ExpressionState>();


	// Ragdoll rigidbodies whose interpolation we disabled while in ghost form, mapped to
	// their original setting so SetGhostNoClip(false) can restore them exactly.
	private readonly List<KeyValuePair<Rigidbody, RigidbodyInterpolation>> _savedInterpolations =
		new List<KeyValuePair<Rigidbody, RigidbodyInterpolation>>();

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

	private static void LogInfo(string message) => GhostPlugin.Log?.LogInfo("[I'm a Ghost] " + message);
	private static void LogError(string entryPoint, Exception ex) => GhostPlugin.Log?.LogError("[I'm a Ghost] " + entryPoint + ": " + ex);

	/// <summary>True while the local character is in ghost form and being driven by this controller.</summary>
	public static bool IsLocalGhostCharacter(Character character)
	{
		if (character == null || !character.IsLocal)
		{
			return false;
		}
		GhostController controller = ((Component)character).GetComponent<GhostController>();
		return controller != null && controller.Active;
	}

	public void EnterGhost(Character character)
	{
		_character = character;
		_prevCenter = character.Center;
		_cameraVelocity = Vector3.zero;
		_cameraHasSmoothedPosition = false;
		// Always start from a clean attack state so re-transforming after an attack
		// (which exits with _attacking still true) works more than once.
		_attacking = false;
		_attackExploded = false;
		_attackStartedAt = 0f;
		Active = true;
		ActiveGhostCharacter = character;
		enabled = true;
		ForceRestoreCharacterCollision();
		SetGhostNoClip(true);
		BuildGhostVisual();
		HideHud();
		LogInfo("Entered ghost form.");
	}

	public void ExitGhost()
	{
		if (!Active && _ghostVisualRoot == null)
		{
			return;
		}
		Active = false;
		ActiveGhostCharacter = null;
		enabled = false;
		// Reset attack state, otherwise the next transformation would see the stale
		// "attack already over" flags and instantly exit again (one-time-only bug).
		_attacking = false;
		_attackExploded = false;
		_attackStartedAt = 0f;
		SetGhostNoClip(false);
		ForceRestoreCharacterCollision();
		DestroyGhostVisual();
		RestoreHud();
		LogInfo("Exited ghost form.");
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
			if (!global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl || _attacking)
			{
				UpdateAttack();
			}
			// UpdateAttack may auto-revert (attack finished); skip the rest of the frame
			// so the restored HUD is not hidden again by the HideHud() below.
			if (!Active)
			{
				return;
			}
			UpdateGhostVisual();
			// Keep looking for the HUD canvases: they may spawn after we transform
			// (e.g. after a scene load), and we want them hidden the whole time.
			HideHud();
		}
		catch (Exception ex)
		{
			LogError("GhostController.Update", ex);
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
			MoveGhostTransform();
		}
		catch (Exception ex)
		{
			LogError("GhostController.FixedUpdate", ex);
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
			// Camera override: when the Harmony fallback patch is active the postfix on
			// MainCameraMovement.LateUpdate drives the camera (robust to execution-order
			// changes in the game). Otherwise we rely on this controller's own LateUpdate
			// running after MainCameraMovement (DefaultExecutionOrder 600 > 500).
			if (!CameraOverridePatchActive)
			{
				RefreshCamera();
			}
		}
		catch (Exception ex)
		{
			LogError("GhostController.LateUpdate", ex);
		}
	}

	private void OnDestroy()
	{
		if (Active)
		{
			Active = false;
			ActiveGhostCharacter = null;
			SetGhostNoClip(false);
			DestroyGhostVisual();
			RestoreHud();
		}
	}

	// ------------------------------------------------------------------
	// HUD: the shared Transform filter hides every HUD element INCLUDING the
	// status bar while transformed (the ghost doesn't run on the player's
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
	// Physics: no-clip flight - kinematic ragdoll (colliders off) and the
	// character root transform is moved directly every physics step.
	// ------------------------------------------------------------------

	/// <summary>
	/// Ghosts drift through walls and players. This mirrors what the game does for carried
	/// players (and what the So Fly flight mod does): ToggleKinematic(true) makes every ragdoll
	/// rigidbody follow the character root we move directly, and ToggleCollision(false) removes
	/// the ragdoll colliders so nothing blocks the flight path. On exit everything is restored
	/// and body velocities are halted so the restored player doesn't shoot off.
	/// </summary>
	private void SetGhostNoClip(bool enable)
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
				// Same as So Fly's takeoff: let go of anyone we are carrying.
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
			LogError("GhostController.SetGhostNoClip(" + enable + ")", ex);
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
			LogError("GhostController.ForceRestoreCharacterCollision", ex);
		}
	}

	/// <summary>
	/// The game (since the 2.02.a "Glider networking" patch) creates every character ragdoll
	/// rigidbody with RigidbodyInterpolation.Interpolate (RigCreator.AddRigidbodyToPart), while
	/// CharacterRagdoll.FixedUpdate drives the kinematic parts by writing their transforms
	/// directly (ResetTransform) and this controller moves the character root the same way.
	/// Interpolated rigidbodies whose transform is written directly fight the interpolation
	/// buffer every frame - the classic Unity jitter - and since Character.Center reads a
	/// ragdoll part position, the ghost ball AND camera jittered with it. While in ghost form we
	/// therefore disable interpolation on all ragdoll bodies (restoring the original values on
	/// exit, so normal play is untouched).
	/// </summary>
	private void SetRagdollInterpolation(bool ghostEnabled)
	{
		CharacterRagdoll ragdoll = _character != null && _character.refs != null ? _character.refs.ragdoll : null;
		if (ragdoll == null || ragdoll.partList == null)
		{
			return;
		}

		if (!ghostEnabled)
		{
			// Restore exactly what we changed.
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

	private void MoveGhostTransform()
	{
		// Movement is locked while the attack is playing out.
		if (_attacking)
		{
			_moveDirection = Vector3.zero;
			return;
		}

		// Unified menu open: freeze flight input (raw-key up/down/sprint reads below)
		// so menu clicks never leak into the form. WASD movement is already zeroed
		// natively — the menu sets GUIManager.windowBlockingInput, which makes
		// Character.CanDoInput() false and CharacterInput.Sample() reset movementInput.
		if (global::TransformState.MenuOpen || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
		{
			_moveDirection = Vector3.zero;
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
		if (Transform.Core.GameInput.JumpHeld(GhostPlugin.UpKey.Value))
		{
			vertical += 1f;
		}
		if (Transform.Core.GameInput.CrouchHeld(GhostPlugin.DownKey.Value))
		{
			vertical -= 1f;
		}

		// Hold Shift to sprint: multiply both horizontal and vertical speed.
		float speedMultiplier = 1f;
		if (Transform.Core.GameInput.SprintHeld(GhostPlugin.SprintKey.Value))
		{
			speedMultiplier = Mathf.Max(1f, GhostPlugin.SprintMultiplier.Value);
		}

		Vector3 delta = (move * GhostPlugin.MovementSpeed.Value + Vector3.up * (vertical * GhostPlugin.VerticalSpeed.Value))
		                * speedMultiplier * Time.fixedDeltaTime;

		// Do not sink through the terrain while descending near it.
		if (delta.y < 0f)
		{
			float groundY = GetGroundHeight(_character.Center);
			if (!float.IsNaN(groundY) && _character.Center.y <= groundY + GroundClearanceMin)
			{
				delta.y = Mathf.Max(delta.y, 0f);
			}
		}

		_moveDirection = move.sqrMagnitude > 0.0001f ? move : Vector3.zero;

		if (delta.sqrMagnitude > 0f)
		{
			transform.position += delta;
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
	// Attack: right-click -> charge -> explode -> auto-revert
	// ------------------------------------------------------------------

	private void UpdateAttack()
	{
		if (!_attacking)
		{
			if (!global::TransformState.MenuOpen
			    && !global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl
			    && Transform.Core.GameInput.UseSecondaryPressed(GhostPlugin.AttackKey.Value))
			{
				StartAttack();
			}
			return;
		}

		float elapsed = Time.unscaledTime - _attackStartedAt;
		if (!_attackExploded && elapsed >= GhostPlugin.AttackChargeSeconds.Value)
		{
			_attackExploded = true;
			DoExplode();
		}

		if (elapsed >= GhostPlugin.AttackChargeSeconds.Value + GhostPlugin.AttackRevertSeconds.Value)
		{
			ExitGhost();
		}
	}

	private void StartAttack()
	{
		_attacking = true;
		_attackExploded = false;
		_attackStartedAt = Time.unscaledTime;
		SetGhostValue(ReadyToExplodeField, true);
		LogInfo("Ghost attack charging...");
	}

	private void DoExplode()
	{
		LogInfo("Ghost attack exploded!");

		// Vanilla explosion visual. The owner always gets a local spawn; when the prefab
		// is network-registered (carries its own PhotonView) we also spawn it over Photon
		// so every client - including unmodded ones - sees the blast. The knockback forces
		// themselves are local physics, which the game's loose ragdoll sync carries to other
		// clients. Everything is wrapped so a failed networked spawn degrades to local only.
		if (_ghostBall != null && _ghostBall.explosionPrefab != null)
		{
			GameObject prefab = _ghostBall.explosionPrefab;
			try
			{
				if (PhotonNetwork.InRoom && prefab.GetComponent<PhotonView>() != null)
				{
					PhotonNetwork.Instantiate(prefab.name, _character.Center, Quaternion.identity);
				}
				else
				{
					UnityObject.Instantiate(prefab, _character.Center, Quaternion.identity);
				}
			}
			catch (Exception ex)
			{
				// Best-effort: if the networked spawn is unavailable, fall back to a local
				// effect so the owner still sees the explosion.
				try
				{
					UnityObject.Instantiate(prefab, _character.Center, Quaternion.identity);
				}
				catch (Exception ex2)
				{
					LogError("GhostController.DoExplode (explosion prefab)", ex2);
				}
				LogError("GhostController.DoExplode (networked explosion)", ex);
			}
		}

		SetGhostValue(ExplodingField, true);

		// Knock nearby players down through the vanilla RPC so every client (modded or
		// not) sees them fall. The ghost player itself is protected by a Harmony patch.
		KnockDownNearbyPlayers();
	}

	private void KnockDownNearbyPlayers()
	{
		float radius = GhostPlugin.AttackRadius.Value;
		float knockback = GhostPlugin.KnockbackForce.Value;
		Vector3 center = _character.Center;
		foreach (Character candidate in Character.AllCharacters)
		{
			if (candidate == null || candidate == _character)
			{
				continue;
			}
			if (candidate.data == null || !candidate.data.fullyConscious)
			{
				continue;
			}
			if (candidate.refs == null || candidate.refs.view == null)
			{
				continue;
			}
			Vector3 offset = candidate.Center - center;
			float distance = offset.magnitude;
			if (distance > radius || distance < 0.001f)
			{
				continue;
			}

			// Networked radial impulse: every client (modded or not) applies this to the
			// victim's ragdoll, so the knockback is felt even in unmodded rooms.
			Vector3 direction = offset / distance;
			Vector3 force = direction * knockback + Vector3.up * (knockback * 0.55f);
			try
			{
				candidate.refs.view.RPC("RPCA_AddForceAtPosition", RpcTarget.All, force, center, radius);
			}
			catch (Exception ex)
			{
				LogError("GhostController.KnockDownNearbyPlayers (force)", ex);
			}

			// Knock them down through the vanilla RPC on every client.
			try
			{
				candidate.refs.view.RPC("RPCA_Fall", RpcTarget.All, 1.2f, 0.25f);
			}
			catch (Exception ex)
			{
				LogError("GhostController.KnockDownNearbyPlayers (fall)", ex);
			}
		}
	}

	private static void SetGhostValue(FieldInfo field, object value)
	{
		try
		{
			if (field != null && ActiveGhostBall != null)
			{
				field.SetValue(ActiveGhostBall, value);
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// Drives the ghost ball's five face-expression animator parameters from its current state.
	///
	/// The vanilla GhostBall.Update calls UpdateAnimations() every frame on the owner
	/// (photonView.IsMine) to copy _readyToExplode / exploding / tickUntilDespawn / burnHealth /
	/// hasTarget / burning onto the Animator's five expression bools (AN_READY, AN_FADE,
	/// AN_EXPLODE, AN_SAD, AN_BURNING). We suppress that Update on every client, so without this
	/// the non-attack expressions are never set and remote clients were left showing every
	/// expression at once ("all expressions").
	///
	/// IMPORTANT: this mod's ghost is player-controlled, so the vanilla formula's
	/// AN_SAD = !hasTarget, AN_FADE = despawn-lifetime and AN_BURNING = fire don't apply -
	/// it never hunts (hasTarget is always false), is reverted by the controller instead of
	/// despawning via lifetime, and never ignites. Replicating those blindly forced SAD on
	/// permanently, making the sad face the default. We therefore drive only the two states
	/// that are meaningful here (charge -> ReadyToExplode, explode -> exploding) and leave
	/// sad / fade / burning off, so idle shows the ghost's neutral face and charge/explode show
	/// exactly one expression on every client. Unmodded clients are unaffected (they never run
	/// our code).
	///
	/// Perf: the Animator is cached per ball and Animator.SetBool only runs when a value
	/// actually changed (it crosses into native code), so a steady-state frame costs just two
	/// ref field reads. The per-ball cache lives in a ConditionalWeakTable, so it also stays
	/// correct when several players are in ghost form at once, and entries die with their balls.
	/// </summary>
	internal static void ApplyGhostBallExpression(GhostBall ghostBall)
	{
		if (ghostBall == null)
		{
			return;
		}
		try
		{
			ExpressionState state = ExpressionStates.GetOrCreateValue(ghostBall);
			if (state.Animator == null)
			{
				state.Animator = ((Component)ghostBall).GetComponent<Animator>();
				state.ValuesKnown = false;
			}
			Animator anim = state.Animator;
			if (anim == null)
			{
				return;
			}
			EnsureAnimatorParams(ghostBall);

			bool ready = ReadGhostBool(ghostBall, ReadyToExplodeRef, ReadyToExplodeField);
			bool exploding = ReadGhostBool(ghostBall, ExplodingRef, ExplodingField);

			// Only the charge/explode states are meaningful for a player-controlled ghost.
			// SAD (no AI target), FADE (lifetime despawn) and BURNING (fire) are forced off:
			// they would otherwise pin the sad face as the permanent default.
			bool fade = false;
			bool sad = false;
			bool burningExpression = false;

			SetBoolIfChanged(anim, _anReady, ready, state.ValuesKnown, ref state.Ready);
			SetBoolIfChanged(anim, _anFade, fade, state.ValuesKnown, ref state.Fade);
			SetBoolIfChanged(anim, _anExplode, exploding, state.ValuesKnown, ref state.Explode);
			SetBoolIfChanged(anim, _anSad, sad, state.ValuesKnown, ref state.Sad);
			SetBoolIfChanged(anim, _anBurning, burningExpression, state.ValuesKnown, ref state.Burning);
			state.ValuesKnown = true;
		}
		catch (Exception ex)
		{
			LogError("GhostController.ApplyGhostBallExpression", ex);
			// Drop the cached state so the next frame re-resolves instead of retrying stale data.
			ExpressionStates.Remove(ghostBall);
		}
	}

	private static void SetBoolIfChanged(Animator anim, int param, bool value, bool valuesKnown, ref bool lastValue)
	{
		if (valuesKnown && lastValue == value)
		{
			return;
		}
		anim.SetBool(param, value);
		lastValue = value;
	}

	private static bool ReadGhostBool(GhostBall ghostBall, AccessTools.FieldRef<GhostBall, bool> fastRef, FieldInfo fallback)
	{
		return fastRef != null ? fastRef(ghostBall) : FieldAsBool(fallback, ghostBall);
	}

	private static void ClearExpressionCache(GhostBall ghostBall)
	{
		if (ghostBall != null)
		{
			ExpressionStates.Remove(ghostBall);
		}
	}

	private static bool FieldAsBool(FieldInfo field, object instance)
	{
		try
		{
			return field != null && field.GetValue(instance) is bool value && value;
		}
		catch
		{
			return false;
		}
	}

	private static void EnsureAnimatorParams(GhostBall ghostBall)
	{
		if (_anParamsResolved)
		{
			return;
		}
		_anReady = ResolveAnHash(ghostBall, "AN_READY", "ReadyToExplode");
		_anFade = ResolveAnHash(ghostBall, "AN_FADE", "FadeOut");
		_anExplode = ResolveAnHash(ghostBall, "AN_EXPLODE", "Exploded");
		_anSad = ResolveAnHash(ghostBall, "AN_SAD", "Sad");
		_anBurning = ResolveAnHash(ghostBall, "AN_BURNING", "Burning");
		_anParamsResolved = true;
	}

	private static int ResolveAnHash(GhostBall ghostBall, string fieldName, string fallbackParam)
	{
		FieldInfo hashField = typeof(GhostBall).GetField(
			fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
		if (hashField != null && hashField.GetValue(ghostBall) is int hash && hash != 0)
		{
			return hash;
		}
		return Animator.StringToHash(fallbackParam);
	}

	// ------------------------------------------------------------------
	// Ghost visual: spawn the vanilla GhostBall prefab (networked over Photon,
	// so even unmodded clients see it) and follow the character with it.
	// ------------------------------------------------------------------

	private void BuildGhostVisual()
	{
		if (_ghostVisualRoot != null || _character == null)
		{
			return;
		}

		try
		{
			GameObject prefab = Resources.Load<GameObject>("GhostBall");
			if (prefab == null)
			{
				LogInfo("Resources.Load(\"GhostBall\") returned null; no ghost visual will be shown.");
				return;
			}

			if (PhotonNetwork.InRoom && _character.photonView != null && _character.photonView.ViewID > 0)
			{
				BuildNetworkedGhost(prefab);
			}
			else
			{
				BuildLocalGhost(prefab);
			}
		}
		catch (Exception ex)
		{
			LogError("GhostController.BuildGhostVisual", ex);
			DestroyGhostVisual();
		}
	}

	private void BuildNetworkedGhost(GameObject prefab)
	{
		GameObject instance = PhotonNetwork.Instantiate(
			"GhostBall",
			_character.Center,
			Quaternion.identity,
			0,
			new object[] { NetworkVisualMarker, _character.photonView.ViewID });
		instance.name = "ImGhostNetworked";
		_ghostVisualRoot = instance;
		_ghostIsNetworked = true;

		_ghostBall = instance.GetComponent<GhostBall>();
		ActiveGhostBall = _ghostBall;

		_ghostRigidbody = instance.GetComponent<Rigidbody>();

		// The owner drives the ball by moving its transform; PhysicsSyncer on the prefab
		// replicates position/rotation/velocity to every other client automatically.
		if (_ghostRigidbody != null)
		{
			_ghostRigidbody.isKinematic = true;
			// We write the ball transform directly every frame; any interpolation on the
			// rigidbody would fight those writes and make the ball jitter (see 2.02.a note
			// in SetRagdollInterpolation). Remote clients are unaffected - their PhysicsSyncer
			// uses MovePosition, which is interpolation-friendly.
			_ghostRigidbody.interpolation = RigidbodyInterpolation.None;
		}

		KeepGhostAliveIndefinitely();
		instance.SetActive(true); // Ghost visual always shown (config removed).
		LogInfo("Spawned networked ghost visual with vanilla GhostBall prefab.");
	}

	private void BuildLocalGhost(GameObject prefab)
	{
		GameObject instance = UnityObject.Instantiate(prefab, _character.Center, Quaternion.identity);
		instance.name = "ImGhostLocal";
		_ghostVisualRoot = instance;
		_ghostIsNetworked = false;

		_ghostBall = instance.GetComponent<GhostBall>();
		ActiveGhostBall = _ghostBall;

		_ghostRigidbody = instance.GetComponent<Rigidbody>();

		// Stop the vanilla AI outright (no PhotonView marker to Harmony-patch offline).
		if (_ghostBall != null)
		{
			_ghostBall.enabled = false;
		}
		if (_ghostRigidbody != null)
		{
			_ghostRigidbody.isKinematic = true;
			_ghostRigidbody.interpolation = RigidbodyInterpolation.None;
		}

		IgnoreCollisionWithOwnRagdoll(instance);
		KeepGhostAliveIndefinitely();
		instance.SetActive(true); // Ghost visual always shown (config removed).
		LogInfo("Spawned local-only ghost visual (offline / single-player).");
	}

	private void KeepGhostAliveIndefinitely()
	{
		SetGhostValue(TickUntilDespawnField, -1000f);
		if (_ghostBall != null)
		{
			_ghostBall.lifetime = 9999f;
		}
	}

	private static void IgnoreCollisionWithOwnRagdoll(GameObject ghostRoot)
	{
		// Same rules as the networked case: never collide with the driving character's ragdoll.
		IgnoreCollisionWithCharacter(ghostRoot, ActiveGhostCharacter);
	}

	/// <summary>Used by Harmony on every modded client to set up collision ignores for the
	/// networked ghost (remote clients cannot run the controller).</summary>
	internal static void IgnoreCollisionWithCharacter(GameObject ghostRoot, Character character)
	{
		if (ghostRoot == null || character == null || character.refs == null || character.refs.ragdoll == null)
		{
			return;
		}

		Collider[] ghostColliders = ghostRoot.GetComponentsInChildren<Collider>(true);
		if (ghostColliders.Length == 0)
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
			foreach (Collider ballCollider in ghostColliders)
			{
				foreach (Collider partCollider in partColliders)
				{
					if (ballCollider != null && partCollider != null && ballCollider.enabled && partCollider.enabled)
					{
						try
						{
							Physics.IgnoreCollision(ballCollider, partCollider, true);
						}
						catch
						{
						}
					}
				}
			}
		}
	}

	private void UpdateGhostVisual()
	{
		if (_ghostVisualRoot == null || _character == null)
		{
			return;
		}

		_ghostVisualRoot.transform.position = _character.Center;

		// Face the glide direction (yaw only) - PhysicsSyncer syncs the rotation.
		if (_moveDirection.sqrMagnitude > 0.01f)
		{
			Quaternion targetRotation = Quaternion.LookRotation(_moveDirection, Vector3.up);
			_ghostVisualRoot.transform.rotation = Quaternion.Slerp(
				_ghostVisualRoot.transform.rotation,
				targetRotation,
				Time.deltaTime * BallFaceMoveSharpness);
		}

		// Drive the ghost's face expression on the owner too, so it matches what remote clients
		// compute from the synced state (the vanilla UpdateAnimations is suppressed on every
		// client, so without this the owner's expression would also drift out of sync).
		ApplyGhostBallExpression(_ghostBall);
	}

	private void DestroyGhostVisual()
	{
		ActiveGhostBall = null;

		if (_ghostVisualRoot != null)
		{
			if (_ghostIsNetworked && PhotonNetwork.InRoom)
			{
				try
				{
					PhotonNetwork.Destroy(_ghostVisualRoot);
				}
				catch
				{
					// If we don't own it anymore or it's already gone, fall back silently.
				}
			}
			else
			{
				Destroy(_ghostVisualRoot);
			}
		}

		ClearExpressionCache(_ghostBall);
		_ghostVisualRoot = null;
		_ghostBall = null;
		_ghostRigidbody = null;
		_ghostIsNetworked = false;
	}

	// ------------------------------------------------------------------
	// Camera: MainCameraMovement.LateUpdate (DefaultExecutionOrder 500) runs
	// first and pulls the camera to the ragdoll head. This controller runs
	// at order 600, so it overrides the camera onto the flying ghost last.
	// ------------------------------------------------------------------

	/// <summary>
	/// Called by the Harmony postfix on MainCameraMovement.LateUpdate (when the camera
	/// fallback patch is active) to push the camera onto the ghost right after the vanilla
	/// camera code finishes, independent of execution order.
	/// </summary>
	internal static void ApplyCameraOverrideForLocalGhost()
	{
		Character character = ActiveGhostCharacter;
		if (character == null)
		{
			return;
		}
		GhostController controller = ((Component)character).GetComponent<GhostController>();
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

			Vector3 lookTarget = GetThirdPersonCameraLookTarget(_character);
			// The ghost noclips through walls, so the camera follows it straight through
			// geometry too - no collision pull-in, no ground clamping, no view shake when
			// passing through airport walls / the plane / props.
			Vector3 desiredPosition = GetThirdPersonCameraPosition(_character);
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
			LogError("GhostController.RefreshCamera", ex);
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
		float value = GhostPlugin.CameraDistance != null ? GhostPlugin.CameraDistance.Value : DefaultCameraDistance;
		return Mathf.Clamp(value, 8f, 30f);
	}

	private static float GetCameraHeight()
	{
		float value = GhostPlugin.CameraHeight != null ? GhostPlugin.CameraHeight.Value : DefaultCameraHeight;
		return Mathf.Clamp(value, 2f, 14f);
	}

	private static float GetCameraFov()
	{
		float value = GhostPlugin.CameraFov != null ? GhostPlugin.CameraFov.Value : DefaultCameraFov;
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
