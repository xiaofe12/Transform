using System;
using System.Collections.Generic;
using System.Reflection;
using Photon.Pun;
using UnityEngine;

namespace ImScoutmaster;

[DefaultExecutionOrder(-100)]
public sealed class PlayerScoutmasterController : MonoBehaviour
{
	private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	private static readonly MethodInfo ScoutmasterResetInputMethod = typeof(Scoutmaster).GetMethod("ResetInput", InstanceFlags);
	private static readonly MethodInfo ScoutmasterDoVisualsMethod = typeof(Scoutmaster).GetMethod("DoVisuals", InstanceFlags);
	private static readonly MethodInfo GrabbingThrowMethod = typeof(CharacterGrabbing).GetMethod("Throw", InstanceFlags);
	private static readonly FieldInfo ScoutmasterCharacterField = typeof(Scoutmaster).GetField("character", InstanceFlags);
	private static readonly FieldInfo ScoutmasterCurrentTargetField = typeof(Scoutmaster).GetField("_currentTarget", InstanceFlags);
	private static readonly FieldInfo ScoutmasterChillForSecondsField = typeof(Scoutmaster).GetField("chillForSeconds", InstanceFlags);
	private static readonly FieldInfo CharacterDataGrabbedPlayerField = typeof(CharacterData).GetField("grabbedPlayer", InstanceFlags);
	private static readonly FieldInfo BodypartCharacterField = typeof(Bodypart).GetField("character", InstanceFlags);
	private static readonly FieldInfo BodypartTargetRotationField = typeof(Bodypart).GetField("targetRotation", InstanceFlags);
	private static readonly FieldInfo BodypartLastTargetRotationField = typeof(Bodypart).GetField("lastTargetRotation", InstanceFlags);
	private static readonly FieldInfo BodypartPrevRotField = typeof(Bodypart).GetField("prevRot", InstanceFlags);
	private static readonly BodypartType[] GroundedFootParts =
	{
		BodypartType.Foot_L,
		BodypartType.Foot_R,
		BodypartType.Toe_L,
		BodypartType.Toe_R
	};
	private const float ClimbSurfaceProbeDistance = 2.25f;
	private const float GrabProbeDistance = 6.5f;
	private const float GrabProbeRadius = 1.15f;
	private const float GrabProbeRetrySeconds = 0.08f;
	private const float GrabProbeSuccessCooldownSeconds = 0.2f;
	private const float OriginalScoutmasterClimbInput = 1f;
	// ---- 每帧性能优化：RaycastNonAlloc 复用静态 buffer（零 GC 分配）；ResetInput 委托化 ----
	private static readonly RaycastHit[] GroundProbeHitsBuffer = new RaycastHit[64];
	private static readonly RaycastHit[] ClimbProbeHitsBuffer = new RaycastHit[64];
	private static Action<Scoutmaster> _scoutmasterResetInputAction;
	private const float MovementInputDeadzone = 0.01f;
	private const float GroundedFootGroundClearance = 0.03f;
	private const float GroundProbeUp = 2.5f;
	private const float GroundProbeDistance = 9f;
	private const float GroundProbeMaxAboveOrigin = 0.25f;
	private const float GroundingCrouchMaxStep = 0.35f;
	private const float GroundingStandingMaxStep = 0.22f;
	private const float GroundingSharpness = 18f;
	private const float GroundingCrouchFloatingTolerance = 0.025f;
	private const float GroundingStandingFloatingTolerance = 0.055f;
	private const float LowObstacleStepMinHeight = 0.055f;
	private const float LowObstacleStepMaxHeight = 0.62f;
	private const float LowObstacleProbeHeight = 0.26f;
	private const float LowObstacleProbeBackoff = 0.18f;
	private const float LowObstacleProbeForward = 1.05f;
	private const float LowObstacleTopProbeForward = 0.34f;
	private const float LowObstacleTopProbeUp = 0.95f;
	private const float LowObstacleStepClearance = 0.06f;
	private const float LowObstacleStepSharpness = 22f;
	private const float LowObstacleMaxStepPerFrame = 0.18f;
	private const float ThrowSupplementalForceMultiplier = 0.18f;
	private const float HeadAngularVelocityLimit = 1.5f;
	private const float HeadRotationSharpness = 28f;
	private const float HeadRotationSnapAngle = 55f;
	private const float HeadVisualMaxUpY = 0.17f;
	private const float HeadVisualMaxDownY = -0.45f;
	private bool _climbWasHeld;
	private bool _reachWasHeld;
	private int _lastReachFrame = -1;
	private int _pendingRemoteGrabUnattachFrame = -1;
	private int _lastGroundingFrame = -1;
	private int _lastLowObstacleStepFrame = -1;

	private Character _sourceCharacter;
	private Scoutmaster _scoutmaster;
	private Character _character;
	private float _lastThrowTime;
	private float _nextGrabProbeTime;
	private float _reachHoldStartTime;
	private Vector3 _lastStableLookDirection = Vector3.forward;
	private static bool _disableOriginalResetInput;

	internal static void AppendMissingReflectionMembers(List<string> missing)
	{
		Plugin.CheckReflectionMember(missing, ScoutmasterResetInputMethod, "Scoutmaster.ResetInput");
		Plugin.CheckReflectionMember(missing, ScoutmasterDoVisualsMethod, "Scoutmaster.DoVisuals");
		Plugin.CheckReflectionMember(missing, GrabbingThrowMethod, "CharacterGrabbing.Throw");
		Plugin.CheckReflectionMember(missing, ScoutmasterCharacterField, "Scoutmaster.character");
		Plugin.CheckReflectionMember(missing, ScoutmasterCurrentTargetField, "Scoutmaster._currentTarget");
		Plugin.CheckReflectionMember(missing, ScoutmasterChillForSecondsField, "Scoutmaster.chillForSeconds");
		Plugin.CheckReflectionMember(missing, CharacterDataGrabbedPlayerField, "CharacterData.grabbedPlayer");
		Plugin.CheckReflectionMember(missing, BodypartCharacterField, "Bodypart.character");
		Plugin.CheckReflectionMember(missing, BodypartTargetRotationField, "Bodypart.targetRotation");
		Plugin.CheckReflectionMember(missing, BodypartLastTargetRotationField, "Bodypart.lastTargetRotation");
		Plugin.CheckReflectionMember(missing, BodypartPrevRotField, "Bodypart.prevRot");
	}

	public static bool IsControlled(Scoutmaster scoutmaster)
	{
		if (scoutmaster == null)
		{
			return false;
		}

		PlayerScoutmasterController controller = ((Component)scoutmaster).GetComponent<PlayerScoutmasterController>();
		return controller != null && controller.enabled && controller._scoutmaster == scoutmaster;
	}

	public void Initialize(Character sourceCharacter, Scoutmaster scoutmaster, Character character)
	{
		_sourceCharacter = sourceCharacter;
		_scoutmaster = scoutmaster;
		_character = character;
		_lastThrowTime = 0f;
		_nextGrabProbeTime = 0f;
		_reachHoldStartTime = 0f;
		_climbWasHeld = false;
		_reachWasHeld = false;
		_lastReachFrame = -1;
		_pendingRemoteGrabUnattachFrame = -1;
		_lastGroundingFrame = -1;
		_lastStableLookDirection = ResolveInitialLookDirection(sourceCharacter, character);
		enabled = true;
	}

	internal void ActivateDynamicRagdollControl()
	{
		if (_character == null || _character.refs?.ragdoll?.partList == null)
		{
			return;
		}

		try
		{
			foreach (Bodypart part in _character.refs.ragdoll.partList)
			{
				Rigidbody rig = part != null ? part.Rig : null;
				if (rig == null)
				{
					continue;
				}

				// 先解除 kinematic 再清速度，避免对 kinematic 刚体设速度产生 Unity 警告。
				rig.detectCollisions = true;
				rig.useGravity = true;
				rig.isKinematic = false;
				rig.linearVelocity = Vector3.zero;
				rig.angularVelocity = Vector3.zero;
				rig.WakeUp();
			}
		}
		catch
		{
		}
	}

	private void Update()
	{
		if (!IsReady())
		{
			return;
		}

		ControlTick();
	}

	private void LateUpdate()
	{
		if (!IsReady())
		{
			return;
		}

		Character.localCharacter = _character;
		if (IsControlPausedByIncapacitation() || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
		{
			ClearActiveControlInputs();
			return;
		}
		HandleReachAndThrow();
		StabilizeControlledHead();
		KeepGroundedFeetAligned();
		ApplyLowObstacleStepAssist();
	}

	internal void ControlTick()
	{
		if (!IsReady())
		{
			return;
		}

		Character.localCharacter = _character;
		KeepAliveAndControllable();
		if (IsControlPausedByIncapacitation() || global::Transform.Core.ThirdPartyCameras.ShouldPauseFormControl)
		{
			ClearActiveControlInputs();
			ScoutmasterCurrentTargetField?.SetValue(_scoutmaster, null);
			RefreshOriginalScoutmasterVisuals();
			ResetOriginalScoutmasterInputSafely();
			return;
		}
		ScoutmasterCurrentTargetField?.SetValue(_scoutmaster, null);
		RefreshOriginalScoutmasterVisuals();
		ResetOriginalScoutmasterInputSafely();
		ApplyOriginalScoutmasterSprintInput();
		HandleClimbInput();
		EnsureClimbSurfaceStillValid();
		HandleReachAndThrow();
		KeepGroundedFeetAligned();
		ApplyLowObstacleStepAssist();
	}

	private bool IsReady()
	{
		return _scoutmaster != null && _character != null && _character.data != null && _character.refs != null;
	}

	private static Vector3 ResolveInitialLookDirection(Character sourceCharacter, Character fallbackCharacter)
	{
		Vector3 direction = ResolveCharacterLookDirection(sourceCharacter);
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = ResolveCharacterLookDirection(fallbackCharacter);
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = Vector3.forward;
		}

		return direction.normalized;
	}

	private static Vector3 ResolveCharacterLookDirection(Character character)
	{
		if (character == null)
		{
			return Vector3.zero;
		}

		Vector3 direction = character.data != null ? character.data.lookDirection : Vector3.zero;
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = character.data != null ? character.data.lookDirection_Flat : Vector3.zero;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = ((Component)character).transform.forward;
		}

		return direction;
	}

	private void KeepAliveAndControllable()
	{
		bool incapacitated = IsControlPausedByIncapacitation();
		_character.isScoutmaster = true;
		_character.isZombie = false;
		_character.data.isScoutmaster = true;
		Plugin.SetCharacterDeadWithoutReconnect(_character, false);
		_character.data.zombified = false;
		if (!incapacitated)
		{
			_character.data.passedOut = false;
			_character.data.fullyPassedOut = false;
			_character.data.fallSeconds = 0f;
			Plugin.ApplyControlledScoutmasterRagdollBlend(_character);
			_character.data.extraStamina = 0f;
			Plugin.ClearAssistJumpState(_character);
			StabilizeControlledHead();
		}

		if (_sourceCharacter != null && _sourceCharacter.data != null)
		{
			Plugin.SetCharacterDeadWithoutReconnect(_sourceCharacter, false);
			_sourceCharacter.data.zombified = false;
			_sourceCharacter.data.passedOut = false;
			_sourceCharacter.data.fullyPassedOut = false;
			_sourceCharacter.data.fallSeconds = 0f;
			Plugin.ClearAssistJumpState(_sourceCharacter);
		}
	}

	private bool IsControlPausedByIncapacitation()
	{
		return Plugin.IsControlledScoutmasterIncapacitated(_character);
	}

	private void ClearActiveControlInputs()
	{
		if (_character == null)
		{
			return;
		}

		try
		{
			if (_character.input != null)
			{
				_character.input.usePrimaryIsPressed = false;
				_character.input.usePrimaryWasPressed = false;
				_character.input.useSecondaryIsPressed = false;
				_character.input.useSecondaryWasPressed = false;
				_character.input.useSecondaryWasReleased = false;
				_character.input.sprintIsPressed = false;
				_character.input.sprintWasPressed = false;
				_character.input.jumpWasPressed = false;
				_character.input.jumpIsPressed = false;
				_character.input.movementInput = Vector2.zero;
				_character.input.lookInput = Vector2.zero;
			}

			if (_character.data != null)
			{
				_character.data.isReaching = false;
				_character.data.isSprinting = false;
			}

			_reachHoldStartTime = 0f;
			_reachWasHeld = false;
			_climbWasHeld = false;
			}
		catch
		{
		}
	}

	private void StabilizeControlledHead()
	{
		if (_character == null || _character.data == null)
		{
			return;
		}

		Vector3 lookDirection = ResolveStableLookDirection();
		Vector3 flatLook = Vector3.ProjectOnPlane(lookDirection, Vector3.up);
		if (!IsFiniteVector(flatLook) || flatLook.sqrMagnitude < 0.0001f)
		{
			flatLook = Vector3.ProjectOnPlane(((Component)_character).transform.forward, Vector3.up);
		}
		if (!IsFiniteVector(flatLook) || flatLook.sqrMagnitude < 0.0001f)
		{
			flatLook = Vector3.forward;
		}

		lookDirection.Normalize();
		flatLook.Normalize();

		Vector3 right = Vector3.Cross(Vector3.up, lookDirection);
		if (!IsFiniteVector(right) || right.sqrMagnitude < 0.0001f)
		{
			right = Vector3.right;
		}
		right.Normalize();

		Vector3 up = Vector3.Cross(lookDirection, right);
		if (!IsFiniteVector(up) || up.sqrMagnitude < 0.0001f)
		{
			up = Vector3.up;
		}
		else
		{
			up.Normalize();
		}

		_character.data.lookDirection = lookDirection;
		_character.data.lookDirection_Flat = flatLook;
		_character.data.lookDirection_Right = right;
		_character.data.lookDirection_Up = up;

		Vector3 headLookDirection = ClampHeadVisualLookDirection(lookDirection, flatLook);
		Vector3 headRight = Vector3.Cross(Vector3.up, headLookDirection);
		if (!IsFiniteVector(headRight) || headRight.sqrMagnitude < 0.0001f)
		{
			headRight = right;
		}
		headRight.Normalize();
		Vector3 headUp = Vector3.Cross(headLookDirection, headRight);
		if (!IsFiniteVector(headUp) || headUp.sqrMagnitude < 0.0001f)
		{
			headUp = Vector3.up;
		}
		else
		{
			headUp.Normalize();
		}

		AlignHeadToLookDirection(headLookDirection, headUp);
	}

	private static Vector3 ClampHeadVisualLookDirection(Vector3 lookDirection, Vector3 flatLook)
	{
		if (!IsFiniteVector(flatLook) || flatLook.sqrMagnitude < 0.0001f)
		{
			flatLook = Vector3.ProjectOnPlane(lookDirection, Vector3.up);
		}
		if (!IsFiniteVector(flatLook) || flatLook.sqrMagnitude < 0.0001f)
		{
			flatLook = Vector3.forward;
		}
		flatLook.Normalize();

		float vertical = IsFiniteVector(lookDirection) ? lookDirection.y : 0f;
		vertical = Mathf.Clamp(vertical, HeadVisualMaxDownY, HeadVisualMaxUpY);
		float flatScale = Mathf.Sqrt(Mathf.Max(0f, 1f - vertical * vertical));
		Vector3 visualDirection = flatLook * flatScale + Vector3.up * vertical;
		if (!IsFiniteVector(visualDirection) || visualDirection.sqrMagnitude < 0.0001f)
		{
			return flatLook;
		}

		return visualDirection.normalized;
	}

	private void AlignHeadToLookDirection(Vector3 lookDirection, Vector3 up)
	{
		Bodypart headPart = _character.refs?.head ?? Plugin.GetBodypart(_character, BodypartType.Head);
		Rigidbody headRig = headPart != null ? headPart.Rig : null;
		UnityEngine.Transform headTransform = headPart != null ? headPart.transform : null;
		if (headRig == null && headTransform == null)
		{
			return;
		}

		Quaternion targetRotation = BuildLookRotation(lookDirection, up);
		if (!IsFiniteQuaternion(targetRotation))
		{
			return;
		}

		Quaternion currentRotation = headRig != null ? headRig.rotation : headTransform.rotation;
		Quaternion rotation = targetRotation;
		if (IsFiniteQuaternion(currentRotation) && Quaternion.Angle(currentRotation, targetRotation) <= HeadRotationSnapAngle)
		{
			float lerp = Time.deltaTime > 0f ? Mathf.Clamp01(Time.deltaTime * HeadRotationSharpness) : 1f;
			rotation = Quaternion.Slerp(currentRotation, targetRotation, lerp);
		}

		if (headRig != null)
		{
			headRig.rotation = rotation;
			if (!headRig.isKinematic)
			{
				headRig.angularVelocity = Vector3.zero;
				headRig.maxAngularVelocity = HeadAngularVelocityLimit;
			}
			headRig.WakeUp();
		}
		if (headTransform != null)
		{
			headTransform.rotation = rotation;
			// 同步动画旋转字段，防止 Animator 与物理 rotation 互相覆盖导致抽搐。
			SyncHeadAnimationRotation(headPart);
		}
	}

	private static void SyncHeadAnimationRotation(Bodypart head)
	{
		if (head == null)
		{
			return;
		}

		try
		{
			Quaternion localRotation = head.transform.localRotation;
			BodypartTargetRotationField?.SetValue(head, localRotation);
			BodypartLastTargetRotationField?.SetValue(head, localRotation);
			BodypartPrevRotField?.SetValue(head, head.transform.rotation);
		}
		catch
		{
		}
	}

	private static Quaternion BuildLookRotation(Vector3 forward, Vector3 up)
	{
		if (!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}
		forward.Normalize();

		if (!IsFiniteVector(up) || up.sqrMagnitude < 0.0001f)
		{
			up = Vector3.up;
		}
		up.Normalize();

		// forward/up 近平行时用叉积重建 up，避免万向节锁导致的朝向抖动。
		if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.96f)
		{
			Vector3 right = Vector3.Cross(Vector3.up, forward);
			if (!IsFiniteVector(right) || right.sqrMagnitude < 0.0001f)
			{
				right = Vector3.Cross(Vector3.right, forward);
			}
			if (!IsFiniteVector(right) || right.sqrMagnitude < 0.0001f)
			{
				right = Vector3.right;
			}
			right.Normalize();
			up = Vector3.Cross(forward, right).normalized;
		}

		return Quaternion.LookRotation(forward, up);
	}

	private static bool IsFiniteQuaternion(Quaternion value)
	{
		return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z) && IsFiniteFloat(value.w);
	}
	private Vector3 ResolveStableLookDirection()
	{
		Vector3 direction = _character.data.lookDirection;
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = _character.data.lookDirection_Flat;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = ((Component)_character).transform.forward;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = Camera.main != null ? Camera.main.transform.forward : Vector3.zero;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = _lastStableLookDirection;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = Vector3.forward;
		}

		direction.Normalize();
		_lastStableLookDirection = direction;
		return direction;
	}


	private static bool IsFiniteVector(Vector3 value)
	{
		return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z);
	}

	private static bool IsFiniteFloat(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value);
	}

	private void RefreshOriginalScoutmasterVisuals()
	{
		try
		{
			ScoutmasterDoVisualsMethod?.Invoke(_scoutmaster, null);
		}
		catch
		{
		}
	}

	private void ResetOriginalScoutmasterInputSafely()
	{
		if (_disableOriginalResetInput || ScoutmasterResetInputMethod == null || _scoutmaster == null)
		{
			return;
		}

		// ResetInput() 读取组件私有 character 字段，该字段在 Scoutmaster.Start() 后才赋值——
		// 首帧（执行序 -100 早于 Start）前静默跳过，玩家输入本就直接采样。
		if (ScoutmasterCharacterField != null && ScoutmasterCharacterField.GetValue(_scoutmaster) == null)
		{
			return;
		}

		try
		{
			// 委托化：避免每帧反射 Invoke。
			if (_scoutmasterResetInputAction == null)
			{
				_scoutmasterResetInputAction = (Action<Scoutmaster>)Delegate.CreateDelegate(typeof(Action<Scoutmaster>), ScoutmasterResetInputMethod);
			}
			_scoutmasterResetInputAction(_scoutmaster);
		}
		catch (TargetInvocationException ex)
		{
			_disableOriginalResetInput = true;
			Plugin.Log?.LogWarning("[I'm Scoutmaster] Scoutmaster.ResetInput failed; using player CharacterInput state directly. " + (ex.InnerException?.Message ?? ex.Message));
		}
		catch (Exception ex)
		{
			_disableOriginalResetInput = true;
			Plugin.Log?.LogWarning("[I'm Scoutmaster] Scoutmaster.ResetInput disabled: " + ex.Message);
		}
	}

	private void HandleReachAndThrow()
	{
		if (_lastReachFrame == Time.frameCount)
		{
			return;
		}
		_lastReachFrame = Time.frameCount;
		FlushPendingRemoteGrabUnattach();

		bool reachHeld = IsReachHeld();
		bool reachPressed = IsReachPressed() || (reachHeld && !_reachWasHeld);
		bool reachReleased = IsReachReleased() || (!reachHeld && _reachWasHeld);
		bool hasHeldTarget = HasHeldTarget();
		_reachWasHeld = reachHeld;

		if (_character.input != null)
		{
			_character.input.useSecondaryIsPressed = reachHeld;
			_character.input.useSecondaryWasPressed = reachPressed;
			_character.input.useSecondaryWasReleased = reachReleased;
		}

		if (reachPressed)
		{
			_reachHoldStartTime = Time.time;
			_nextGrabProbeTime = 0f;
		}

		if (reachHeld)
		{
			_character.data.isReaching = true;
			_character.data.sincePressReach = 0f;
			if (!hasHeldTarget)
			{
				hasHeldTarget = TryGrabPlayerInView() || HasHeldTarget();
			}
		}
		else
		{
			_character.data.isReaching = false;
		}

		if (reachReleased)
		{
			if (hasHeldTarget)
			{
				ThrowHeldPlayer();
			}
			else
			{
				Plugin.BroadcastControlledScoutmasterStopReaching(_character);
			}
		}
	}

	private bool TryGrabPlayerInView()
	{
		if (_character?.refs?.grabbing == null || _character.data == null || _character.data.grabJoint != null)
		{
			return false;
		}
		if (Time.time < _nextGrabProbeTime)
		{
			return false;
		}

		ResolveGrabProbe(out Vector3 origin, out Vector3 direction);

		bool grabbed = Plugin.TryGrabControlledScoutmasterTarget(
			_character.refs.grabbing,
			_character,
			_sourceCharacter,
			origin,
			direction.normalized,
			GrabProbeDistance,
			GrabProbeRadius);
		_nextGrabProbeTime = Time.time + (grabbed ? GrabProbeSuccessCooldownSeconds : GrabProbeRetrySeconds);
		return grabbed;
	}

	private void ResolveGrabProbe(out Vector3 origin, out Vector3 direction)
	{
		origin = Plugin.ResolveControlledGrabHandPosition(_character);
		if (!IsFiniteVector(origin) || origin.sqrMagnitude < 0.0001f)
		{
			origin = _character.Head;
		}
		if (!IsFiniteVector(origin) || origin.sqrMagnitude < 0.0001f)
		{
			origin = _character.Center + Vector3.up * 0.7f;
		}

		direction = Vector3.zero;
		Camera mainCamera = Camera.main;
		if (mainCamera != null)
		{
			Vector3 cameraOrigin = mainCamera.transform.position;
			Vector3 cameraDirection = mainCamera.transform.forward;
			if (IsFiniteVector(cameraOrigin) && IsFiniteVector(cameraDirection) && cameraDirection.sqrMagnitude > 0.0001f)
			{
				float aimDistance = GrabProbeDistance + Mathf.Clamp(Plugin.ThirdPersonDistance.Value, 2f, 16f) + 2f;
				Vector3 aimPoint = cameraOrigin + cameraDirection.normalized * aimDistance;
				direction = aimPoint - origin;
			}
		}

		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = _character.data.lookDirection;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = _character.data.lookDirection_Flat;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = ((Component)_character).transform.forward;
		}
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			direction = Vector3.forward;
		}

		direction.Normalize();
	}

	private void HandleClimbInput()
	{
		bool climbHeld = IsClimbHeld();
		bool climbPressed = IsClimbPressed();
		bool climbReleased = IsClimbReleased() || (_climbWasHeld && !climbHeld);
		_climbWasHeld = climbHeld;

		if (_character.input != null)
		{
			if (climbPressed)
			{
				_character.input.usePrimaryWasPressed = true;
			}
			if (!climbHeld && !climbPressed)
			{
				ClearPrimaryClimbInput(false);
			}
		}

		if (climbHeld || climbPressed)
		{
			if (_character.input != null)
			{
				_character.input.usePrimaryIsPressed = true;
			}
			try
			{
				if (_character.data.currentItem == null)
				{
					_character.data.sincePressClimb = 0f;
				}
			}
			catch
			{
			}

			bool startedClimbing = TryStartLocalClimb();
			if (startedClimbing || IsCharacterClimbing())
			{
				ApplyOriginalScoutmasterClimbInput();
			}
		}

		if (climbReleased)
		{
			ClearPrimaryClimbInput(true);
			if (IsCharacterClimbing())
			{
				Plugin.StopControlledScoutmasterClimb(_character.refs?.climbing, _character, 0f);
			}
		}
	}

	private bool TryStartLocalClimb()
	{
		if (_character == null || _character.data == null || _character.refs?.climbing == null)
		{
			return false;
		}
		if (_character.data.isClimbing || _character.data.isRopeClimbing || _character.data.isVineClimbing)
		{
			return false;
		}
		if (_character.data.currentItem != null)
		{
			return false;
		}

		CharacterClimbing climbing = _character.refs.climbing;
		if (!Plugin.CanControlledScoutmasterClimb(climbing))
		{
			return false;
		}

		Vector3 forward = Camera.main != null ? Camera.main.transform.forward : Vector3.zero;
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = _character.data.lookDirection;
		}
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = _character.data.lookDirection_Flat;
		}
		if (forward.sqrMagnitude < 0.0001f)
		{
			forward = ((Component)_character).transform.forward;
		}
		forward.Normalize();

		if (!TryFindLocalClimbHit(_character.Center, forward, 1.65f, out RaycastHit hit))
		{
			return false;
		}

		return Plugin.TryStartControlledScoutmasterClimb(climbing, _character, hit.point, hit.normal, "Local Scoutmaster climb");
	}

	private void ApplyOriginalScoutmasterClimbInput()
	{
		if (_character?.input == null || _character.data == null)
		{
			return;
		}

		Vector2 movementInput = _character.input.movementInput;
		movementInput.y = Mathf.Max(movementInput.y, OriginalScoutmasterClimbInput);
		_character.input.movementInput = movementInput;
	}

	private void ApplyOriginalScoutmasterSprintInput()
	{
		if (_character?.input == null || _character.data == null)
		{
			return;
		}
		if (IsCharacterClimbing())
		{
			_character.data.isSprinting = false;
			return;
		}

		Vector2 movementInput = ReadCurrentMovementInput();
		if (movementInput.sqrMagnitude <= MovementInputDeadzone * MovementInputDeadzone)
		{
			movementInput = _character.input.movementInput;
		}

		bool sprintHeld = IsSprintHeld();
		bool movingForward = movementInput.y > MovementInputDeadzone;
		if (!sprintHeld || !movingForward)
		{
			_character.data.isSprinting = false;
			return;
		}

		_character.input.sprintIsPressed = true;
		if (IsSprintPressed())
		{
			_character.input.sprintWasPressed = true;
		}
		_character.data.isCrouching = false;
		_character.data.isSprinting = true;
	}

	private void DriveOriginalScoutmasterControl()
	{
		if (_character?.data == null || _character.input == null)
		{
			return;
		}

		Vector2 movementInput = ReadCurrentMovementInput();
		bool jumpHeld = Transform.Core.GameInput.JumpHeld(KeyCode.Space);
		bool jumpPressed = Transform.Core.GameInput.JumpPressed(KeyCode.Space);
		_character.input.movementInput = movementInput;
		_character.input.jumpIsPressed = jumpHeld;
		_character.input.jumpWasPressed = jumpPressed;
	}

	private void DriveScoutmasterLookInput()
	{
		if (_character?.input == null)
		{
			return;
		}

		_character.input.lookInput = Transform.Core.GameInput.Look();
	}

	private static Vector2 ReadCurrentMovementInput()
	{
		Vector2 movementInput = Transform.Core.GameInput.Move();
		if (movementInput.sqrMagnitude <= MovementInputDeadzone * MovementInputDeadzone)
		{
			if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) movementInput += Vector2.up;
			if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) movementInput -= Vector2.up;
			if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) movementInput += Vector2.right;
			if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) movementInput -= Vector2.right;
		}

		if (movementInput.sqrMagnitude > 1f) movementInput.Normalize();
		return movementInput;
	}

	private bool TryFindLocalClimbHit(Vector3 origin, Vector3 forward, float distance, out RaycastHit climbHit)
	{
		climbHit = default;
		int hitCount;
		try
		{
			// RaycastNonAlloc 复用静态 buffer（零分配）；命中按 distance 升序，首个过滤命中即最近。
			hitCount = Physics.RaycastNonAlloc(origin, forward, ClimbProbeHitsBuffer, Mathf.Max(distance, 0.25f), ~0, QueryTriggerInteraction.Ignore);
		}
		catch
		{
			return false;
		}

		if (hitCount <= 0)
		{
			return false;
		}

		for (int i = 0; i < hitCount; i++)
		{
			RaycastHit hit = ClimbProbeHitsBuffer[i];
			if (hit.collider == null || hit.collider.isTrigger)
			{
				continue;
			}
			Character hitCharacter = ResolveHitCharacter(hit);
			if (hitCharacter == _character || hitCharacter == _sourceCharacter)
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

	private void EnsureClimbSurfaceStillValid()
	{
		if (!IsCharacterClimbing() || _character.refs?.climbing == null)
		{
			return;
		}
		if (!IsClimbHeld())
		{
			Plugin.StopControlledScoutmasterClimb(_character.refs.climbing, _character, 0f);
			return;
		}
		if (_character.data.isRopeClimbing || _character.data.isVineClimbing)
		{
			return;
		}

		Vector3 origin = _character.Center;
		if (TryFindCurrentClimbSurface(origin, out _))
		{
			_character.data.sinceCanClimb = 0f;
			return;
		}

		Plugin.StopControlledScoutmasterClimb(_character.refs.climbing, _character, 0f);
	}

	private bool TryFindCurrentClimbSurface(Vector3 origin, out RaycastHit climbHit)
	{
		climbHit = default;
		Vector3[] directions =
		{
			-_character.data.climbNormal,
			_character.data.lookDirection,
			_character.data.lookDirection_Flat,
			((Component)_character).transform.forward
		};

		foreach (Vector3 rawDirection in directions)
		{
			Vector3 direction = rawDirection;
			if (direction.sqrMagnitude < 0.0001f)
			{
				continue;
			}

			direction.Normalize();
			if (TryFindLocalClimbHit(origin, direction, ClimbSurfaceProbeDistance, out climbHit))
			{
				return true;
			}
		}

		return false;
	}

	private Character ResolveHitCharacter(RaycastHit hit)
	{
		if (hit.collider == null)
		{
			return null;
		}

		Character character = hit.collider.GetComponentInParent<Character>();
		if (character != null)
		{
			return character;
		}

		Bodypart bodypart = hit.collider.GetComponentInParent<Bodypart>();
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

	private bool IsCharacterClimbing()
	{
		return _character != null
			&& _character.data != null
			&& (_character.data.isClimbing || _character.data.isRopeClimbing || _character.data.isVineClimbing);
	}

	private void KeepGroundedFeetAligned()
	{
		if (_lastGroundingFrame == Time.frameCount)
		{
			return;
		}
		_lastGroundingFrame = Time.frameCount;

		if (_character == null || _character.data == null)
		{
			return;
		}
		if (IsCharacterClimbing() || IsControlPausedByIncapacitation() || !IsStableGrounded())
		{
			return;
		}
		if (!TryResolveControlledGroundY(out float groundY) || !TryGetControlledFootBottomY(out float footBottomY))
		{
			return;
		}

		bool crouching = _character.data.isCrouching;
		float targetFootY = groundY + GroundedFootGroundClearance;
		float correction = targetFootY - footBottomY;
		float floatingTolerance = crouching ? GroundingCrouchFloatingTolerance : GroundingStandingFloatingTolerance;
		if (correction >= -floatingTolerance)
		{
			return;
		}

		// 防御：探测到的地面远低于脚（> 0.6m）时，说明脚下可能是悬崖/虚空，
		// 不要强制把领队拉向"探测到的地面"，否则领队会持续下坠掉入虚空。
		const float MaxPullDownCorrection = -0.6f;
		if (correction < MaxPullDownCorrection)
		{
			return;
		}

		float maxStep = crouching ? GroundingCrouchMaxStep : GroundingStandingMaxStep;
		float step = Mathf.Max(correction, -maxStep);
		if (Time.deltaTime > 0f)
		{
			step *= Mathf.Clamp01(Time.deltaTime * GroundingSharpness);
		}

		OffsetControlledBody(Vector3.up * step);
		if (crouching)
		{
			_character.data.currentHeadHeight = Mathf.Max(0f, _character.data.currentHeadHeight + step);
		}
	}

	private bool IsStableGrounded()
	{
		return _character != null
			&& _character.data != null
			&& (_character.data.isGrounded || _character.data.sinceGrounded <= 0.12f || _character.data.groundedFor > 0f);
	}

	private bool TryResolveControlledGroundY(out float groundY)
	{
		groundY = 0f;
		if (_character == null)
		{
			return false;
		}

		Vector3 center = _character.Center;
		if (TryRaycastControlledGroundY(center, out groundY))
		{
			return true;
		}

		foreach (BodypartType partType in GroundedFootParts)
		{
			Bodypart part = Plugin.GetBodypart(_character, partType);
			Vector3 origin = ResolveBodypartProbeOrigin(part);
			if (TryRaycastControlledGroundY(origin, out groundY))
			{
				return true;
			}
		}

		if (_character.data != null && IsFiniteVector(_character.data.groundPos))
		{
			Vector3 groundPos = _character.data.groundPos;
			Vector3 flatDelta = Vector3.ProjectOnPlane(groundPos - center, Vector3.up);
			if (flatDelta.sqrMagnitude <= 36f && Mathf.Abs(groundPos.y - center.y) <= 10f)
			{
				groundY = groundPos.y;
				return true;
			}
		}

		groundY = 0f;
		return false;
	}

	private bool TryRaycastControlledGroundY(Vector3 origin, out float groundY)
	{
		groundY = 0f;
		if (!IsFiniteVector(origin))
		{
			return false;
		}

		int hitCount;
		try
		{
			// RaycastNonAlloc 复用静态 buffer（零分配），命中按 distance 升序，首个过滤命中即最近。
			hitCount = Physics.RaycastNonAlloc(origin + Vector3.up * GroundProbeUp, Vector3.down, GroundProbeHitsBuffer, GroundProbeDistance, ~0, QueryTriggerInteraction.Ignore);
		}
		catch
		{
			return false;
		}

		if (hitCount <= 0)
		{
			return false;
		}

		for (int i = 0; i < hitCount; i++)
		{
			RaycastHit hit = GroundProbeHitsBuffer[i];
			if (hit.collider == null || hit.collider.isTrigger)
			{
				continue;
			}
			if (hit.point.y > origin.y + GroundProbeMaxAboveOrigin)
			{
				continue;
			}
			if (Vector3.Dot(hit.normal, Vector3.up) < 0.2f)
			{
				continue;
			}

			Character hitCharacter = ResolveHitCharacter(hit);
			if (hitCharacter == _character || hitCharacter == _sourceCharacter)
			{
				continue;
			}

			groundY = hit.point.y;
			return true;
		}

		return false;
	}

	private static Vector3 ResolveBodypartProbeOrigin(Bodypart part)
	{
		if (part == null)
		{
			return Vector3.zero;
		}

		if (part.Rig != null && IsFiniteVector(part.Rig.worldCenterOfMass))
		{
			return part.Rig.worldCenterOfMass;
		}

		Vector3 position = part.transform.position;
		return IsFiniteVector(position) ? position : Vector3.zero;
	}

	private bool TryGetControlledFootBottomY(out float footBottomY)
	{
		footBottomY = 0f;
		bool found = false;

		foreach (BodypartType partType in GroundedFootParts)
		{
			Bodypart part = Plugin.GetBodypart(_character, partType);
			if (part == null)
			{
				continue;
			}

			if (TryGetBodypartBottomY(part, out float partBottomY) && (!found || partBottomY < footBottomY))
			{
				footBottomY = partBottomY;
				found = true;
			}
		}

		return found;
	}

	private void ApplyLowObstacleStepAssist()
	{
		if (_lastLowObstacleStepFrame == Time.frameCount)
		{
			return;
		}
		_lastLowObstacleStepFrame = Time.frameCount;

		if (_character == null || _character.data == null)
		{
			return;
		}
		if (IsCharacterClimbing() || IsControlPausedByIncapacitation() || !IsStableGrounded())
		{
			return;
		}

		Vector2 movementInput = ReadCurrentMovementInput();
		if (movementInput.sqrMagnitude <= MovementInputDeadzone * MovementInputDeadzone)
		{
			return;
		}
		if (!TryResolveControlledGroundY(out float groundY) || !TryGetControlledFootBottomY(out float footBottomY))
		{
			return;
		}

		Vector3 moveDirection = ResolveWorldMoveDirection(movementInput);
		if (!IsFiniteVector(moveDirection) || moveDirection.sqrMagnitude < 0.0001f)
		{
			return;
		}
		moveDirection.Normalize();

		if (!TryFindLowObstacleAhead(moveDirection, groundY, footBottomY, out RaycastHit obstacleHit))
		{
			return;
		}
		if (!TryFindLowObstacleTop(obstacleHit, moveDirection, groundY, out float stepTopY))
		{
			return;
		}

		float stepHeight = stepTopY - groundY;
		if (stepHeight < LowObstacleStepMinHeight || stepHeight > LowObstacleStepMaxHeight)
		{
			return;
		}

		float targetFootY = stepTopY + LowObstacleStepClearance;
		float correction = targetFootY - footBottomY;
		if (correction <= LowObstacleStepMinHeight)
		{
			return;
		}

		float step = Mathf.Min(correction, LowObstacleMaxStepPerFrame);
		if (Time.deltaTime > 0f)
		{
			step *= Mathf.Clamp01(Time.deltaTime * LowObstacleStepSharpness);
		}
		if (step <= 0.001f)
		{
			return;
		}

		OffsetControlledBody(Vector3.up * step);
		if (_character.data != null)
		{
			_character.data.isGrounded = false;
			_character.data.sinceGrounded = 0.04f;
			_character.data.groundPos = new Vector3(_character.Center.x, stepTopY, _character.Center.z);
		}
	}

	private Vector3 ResolveWorldMoveDirection(Vector2 movementInput)
	{
		Vector3 forward = _character?.data != null ? _character.data.lookDirection_Flat : Vector3.zero;
		if (!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f)
		{
			forward = _character?.data != null ? Vector3.ProjectOnPlane(_character.data.lookDirection, Vector3.up) : Vector3.zero;
		}
		if ((!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f) && Camera.main != null)
		{
			forward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
		}
		if (!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.ProjectOnPlane(((Component)_character).transform.forward, Vector3.up);
		}
		if (!IsFiniteVector(forward) || forward.sqrMagnitude < 0.0001f)
		{
			forward = Vector3.forward;
		}
		forward.Normalize();

		Vector3 right = _character?.data != null ? Vector3.ProjectOnPlane(_character.data.lookDirection_Right, Vector3.up) : Vector3.zero;
		if (!IsFiniteVector(right) || right.sqrMagnitude < 0.0001f)
		{
			right = Vector3.Cross(Vector3.up, forward);
		}
		if (!IsFiniteVector(right) || right.sqrMagnitude < 0.0001f)
		{
			right = Vector3.right;
		}
		right.Normalize();

		Vector3 direction = forward * movementInput.y + right * movementInput.x;
		if (!IsFiniteVector(direction) || direction.sqrMagnitude < 0.0001f)
		{
			return Vector3.zero;
		}
		return direction.normalized;
	}

	private bool TryFindLowObstacleAhead(Vector3 moveDirection, float groundY, float footBottomY, out RaycastHit obstacleHit)
	{
		obstacleHit = default;
		Vector3 center = _character.Center;
		float probeY = Mathf.Max(groundY, footBottomY) + LowObstacleProbeHeight;
		Vector3 origin = new Vector3(center.x, probeY, center.z) - moveDirection * LowObstacleProbeBackoff;

		int hitCount;
		try
		{
			hitCount = Physics.RaycastNonAlloc(origin, moveDirection, ClimbProbeHitsBuffer, LowObstacleProbeForward, ~0, QueryTriggerInteraction.Ignore);
		}
		catch
		{
			return false;
		}
		if (hitCount <= 0)
		{
			return false;
		}

		bool found = false;
		float nearestDistance = float.MaxValue;
		for (int i = 0; i < hitCount; i++)
		{
			RaycastHit hit = ClimbProbeHitsBuffer[i];
			if (hit.collider == null || hit.collider.isTrigger)
			{
				continue;
			}
			Character hitCharacter = ResolveHitCharacter(hit);
			if (hitCharacter == _character || hitCharacter == _sourceCharacter)
			{
				continue;
			}
			if (Vector3.Dot(hit.normal, Vector3.up) > 0.45f)
			{
				continue;
			}

			if (hit.distance < nearestDistance)
			{
				nearestDistance = hit.distance;
				obstacleHit = hit;
				found = true;
			}
		}

		return found;
	}

	private bool TryFindLowObstacleTop(RaycastHit obstacleHit, Vector3 moveDirection, float groundY, out float topY)
	{
		topY = 0f;
		Vector3 topProbe = obstacleHit.point + moveDirection * LowObstacleTopProbeForward + Vector3.up * LowObstacleTopProbeUp;
		float distance = LowObstacleTopProbeUp + LowObstacleStepMaxHeight + 0.25f;

		int hitCount;
		try
		{
			hitCount = Physics.RaycastNonAlloc(topProbe, Vector3.down, GroundProbeHitsBuffer, distance, ~0, QueryTriggerInteraction.Ignore);
		}
		catch
		{
			return false;
		}
		if (hitCount <= 0)
		{
			return false;
		}

		bool found = false;
		float nearestDistance = float.MaxValue;
		for (int i = 0; i < hitCount; i++)
		{
			RaycastHit hit = GroundProbeHitsBuffer[i];
			if (hit.collider == null || hit.collider.isTrigger)
			{
				continue;
			}
			if (Vector3.Dot(hit.normal, Vector3.up) < 0.55f)
			{
				continue;
			}
			Character hitCharacter = ResolveHitCharacter(hit);
			if (hitCharacter == _character || hitCharacter == _sourceCharacter)
			{
				continue;
			}

			float stepHeight = hit.point.y - groundY;
			if (stepHeight < LowObstacleStepMinHeight || stepHeight > LowObstacleStepMaxHeight)
			{
				continue;
			}

			if (hit.distance < nearestDistance)
			{
				nearestDistance = hit.distance;
				topY = hit.point.y;
				found = true;
			}
		}

		return found;
	}

	private static bool TryGetBodypartBottomY(Bodypart part, out float bottomY)
	{
		bottomY = 0f;
		if (part == null)
		{
			return false;
		}

		bool found = false;
		Collider[] colliders = null;
		try
		{
			colliders = part.GetComponentsInChildren<Collider>(true);
		}
		catch
		{
		}

		if (colliders != null)
		{
			foreach (Collider collider in colliders)
			{
				if (collider == null || !collider.enabled)
				{
					continue;
				}

				Vector3 min = collider.bounds.min;
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

		Vector3 position = part.Rig != null ? part.Rig.worldCenterOfMass : part.transform.position;
		if (IsFiniteVector(position) && (!found || position.y < bottomY))
		{
			bottomY = position.y;
			found = true;
		}

		return found;
	}

	private void OffsetControlledBody(Vector3 delta)
	{
		if (_character == null || !IsFiniteVector(delta) || delta.sqrMagnitude < 0.000001f)
		{
			return;
		}

		try
		{
			UnityEngine.Transform characterTransform = ((Component)_character).transform;
			characterTransform.position += delta;
			if (_character.refs?.ragdoll?.partList != null)
			{
				foreach (Bodypart part in _character.refs.ragdoll.partList)
				{
					if (part == null)
					{
						continue;
					}

					Rigidbody rig = part.Rig;
					if (rig != null)
					{
						rig.position += delta;
						if (!rig.isKinematic)
						{
							Vector3 velocity = rig.linearVelocity;
							if (IsFiniteVector(velocity) && velocity.y > 0f)
							{
								velocity.y = 0f;
								rig.linearVelocity = velocity;
							}
						}
						rig.WakeUp();
					}
					else
					{
						part.transform.position += delta;
					}
				}
			}
		}
		catch
		{
		}
	}

	private void ClearPrimaryClimbInput(bool released)
	{
		if (_character?.input == null)
		{
			return;
		}

		_character.input.usePrimaryIsPressed = false;
		_character.input.usePrimaryWasPressed = false;
		if (released)
		{
			_character.input.usePrimaryWasReleased = true;
		}
	}

	private static bool IsClimbHeld()
	{
		return Transform.Core.GameInput.UsePrimaryHeld(KeyCode.Mouse0);
	}

	private static bool IsClimbPressed()
	{
		return Transform.Core.GameInput.UsePrimaryPressed(KeyCode.Mouse0);
	}

	private static bool IsClimbReleased()
	{
		return Transform.Core.GameInput.UsePrimaryReleased(KeyCode.Mouse0);
	}

	private static bool IsReachHeld()
	{
		return Transform.Core.GameInput.UseSecondaryHeld(KeyCode.Mouse1);
	}

	private static bool IsReachPressed()
	{
		return Transform.Core.GameInput.UseSecondaryPressed(KeyCode.Mouse1);
	}

	private static bool IsReachReleased()
	{
		return Transform.Core.GameInput.UseSecondaryReleased(KeyCode.Mouse1);
	}

	private static bool IsSprintHeld()
	{
		return Transform.Core.GameInput.SprintHeld(KeyCode.LeftShift);
	}

	private static bool IsSprintPressed()
	{
		return Transform.Core.GameInput.SprintPressed(KeyCode.LeftShift);
	}

	private void ThrowHeldPlayer()
	{
		if (IsControlPausedByIncapacitation())
		{
			return;
		}
		if (Time.time - _lastThrowTime < 0.25f)
		{
			return;
		}

		_lastThrowTime = Time.time;
		Character target = GetGrabbedPlayer(_character);
		if (!HasHeldTarget() || _character.refs?.grabbing == null)
		{
			return;
		}

		Vector3 direction = _character.data != null ? _character.data.lookDirection : Vector3.zero;
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = _character.data != null ? _character.data.lookDirection_Flat : Vector3.zero;
		}
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = ((Component)_character).transform.forward;
		}

		direction = Vector3.ProjectOnPlane(direction, Vector3.up);
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = Vector3.ProjectOnPlane(((Component)_character).transform.forward, Vector3.up);
		}
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = Vector3.forward;
		}
		direction.Normalize();
		direction.y = Plugin.ThrowUpBias.Value;
		// Original Scoutmaster.IThrow uses a fixed 1500 force. Player-controlled throws need
		// more travel distance, so the config default is stronger and we add a smaller networked
		// supplemental impulse below after the vanilla grabbing throw releases the victim.
		Vector3 force = direction * Plugin.ThrowForce.Value;
		try
		{
			// 原版通过 RPCA_Throw 广播，所有客户端各自执行 Throw（内部硬编码 RPCA_Fall(1,0)）。
			// 这里补齐受害者跌倒的广播，远程客户端才能看到布娃娃效果。
			if (target != null && target.refs != null && target.refs.view != null)
			{
				target.refs.view.RPC("RPCA_Fall", RpcTarget.All, 1f, 0f);
			}

			// 即使下面的本地 Throw 失败也先安排远程解抓，避免受害者被永久卡在手上。
			_pendingRemoteGrabUnattachFrame = Time.frameCount + 1;

			GrabbingThrowMethod?.Invoke(_character.refs.grabbing, new object[] { force, Plugin.ThrowFallSeconds.Value });

			// CharacterGrabbing.Throw's AddForce is local physics only. Broadcast the same force
			// to other clients, then add a smaller supplemental impulse to every client so the
			// throw has satisfying range on the thrower, victim, and spectators.
			if (target != null && target.refs != null && target.refs.view != null)
			{
				target.refs.view.RPC("RPCA_AddForceToBodyPart", RpcTarget.Others,
					BodypartType.Torso, Vector3.zero, force);
				target.refs.view.RPC("RPCA_AddForceToBodyPart", RpcTarget.All,
					BodypartType.Torso, Vector3.zero, force * ThrowSupplementalForceMultiplier);
			}

			_character.data.isReaching = false;
			_reachHoldStartTime = 0f;

			// 原版投掷后给受害者施加 0.3 Injury：AddStatus(Injury, 0.3, fromRPC, playEffects, notify, !ignoreInvincibility)
			if (target != null && target.refs != null && target.refs.afflictions != null)
			{
				target.refs.afflictions.AddStatus(CharacterAfflictions.STATUSTYPE.Injury, 0.3f, true, true, true, false);
			}

			// 原版投掷后 chillForSeconds = 2。
			ScoutmasterChillForSecondsField?.SetValue(_scoutmaster, 2f);
			string targetName = target != null ? target.characterName : "held player";
			Plugin.Log?.LogInfo("[I'm Scoutmaster] Threw " + targetName + " with force " + Plugin.ThrowForce.Value.ToString("0") + ".");
		}
		catch (Exception ex)
		{
			Plugin.Log?.LogWarning("[I'm Scoutmaster] Throw failed: " + ex.Message);
		}
	}

	private void FlushPendingRemoteGrabUnattach()
	{
		if (_pendingRemoteGrabUnattachFrame < 0 || Time.frameCount < _pendingRemoteGrabUnattachFrame)
		{
			return;
		}

		_pendingRemoteGrabUnattachFrame = -1;
		Plugin.BroadcastControlledScoutmasterGrabUnattach(_character);
		Plugin.BroadcastControlledScoutmasterStopReaching(_character);
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

	private bool HasHeldTarget()
	{
		return _character != null
			&& _character.data != null
			&& (_character.data.grabJoint != null || GetGrabbedPlayer(_character) != null);
	}
}
