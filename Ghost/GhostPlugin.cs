using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Transform.Core;

namespace ImGhost;

/// <summary>
/// Ghost form module, adapted from the standalone "I'm a Ghost" BepInEx plugin into a static
/// module driven by the unified Transform plugin. All controller/patch code is unchanged.
/// </summary>
internal static class GhostPlugin
{
	public const string Id = "com.github.Thanks.ImGhost";
	public const string Name = "I'm a Ghost";
	public const string Version = "0.1.0";

	internal static ManualLogSource Log;

	internal static ConfigEntry<KeyCode> AttackKey;
	internal static ConfigEntry<KeyCode> UpKey;
	internal static ConfigEntry<KeyCode> DownKey;
	internal static ConfigEntry<KeyCode> SprintKey;
	internal static ConfigEntry<float> MovementSpeed;
	internal static ConfigEntry<float> VerticalSpeed;
	internal static ConfigEntry<float> SprintMultiplier;
	internal static ConfigEntry<float> AttackChargeSeconds;
	internal static ConfigEntry<float> AttackRevertSeconds;
	internal static ConfigEntry<float> AttackRadius;
	internal static ConfigEntry<float> KnockbackForce;
	internal static ConfigEntry<float> CameraDistance;
	internal static ConfigEntry<float> CameraHeight;
	internal static ConfigEntry<float> CameraFov;

	private static Harmony _harmony;
	private static GhostController _controller;
	private static bool _initialized;

	/// <summary>True while the local player is in ghost form.</summary>
	internal static bool IsActive => _controller != null && _controller.Active;

	internal static void Initialize(ConfigFile config, ManualLogSource log)
	{
		if (_initialized) return;
		_initialized = true;
		Log = log;
		_harmony = new Harmony(Id);

		_harmony.PatchAll(typeof(GhostHarmonyPatches));
		ConfigureOptionalCharacterPatches();
		ConfigureCameraFallbackPatch();

		AttackKey = config.Bind("Ghost Controls", "AttackKey", KeyCode.Mouse1,
			"Right-click while in ghost form to trigger the ghost explosion attack. The player reverts after the attack.");
		UpKey = config.Bind("Ghost Controls", "UpKey", KeyCode.Space,
			"Hold Space to fly up while in ghost form.");
		DownKey = config.Bind("Ghost Controls", "DownKey", KeyCode.LeftControl,
			"Hold Ctrl to fly down while in ghost form.");
		SprintKey = config.Bind("Ghost Controls", "SprintKey", KeyCode.LeftShift,
			"Hold Shift to sprint (move faster) while in ghost form.");
		MovementSpeed = config.Bind("Ghost", "MovementSpeed", 14f, new ConfigDescription(
			"How fast the ghost glides with WASD.",
			new AcceptableValueRange<float>(0f, 40f)));
		VerticalSpeed = config.Bind("Ghost", "VerticalSpeed", 12f, new ConfigDescription(
			"How fast the ghost rises and sinks.",
			new AcceptableValueRange<float>(0f, 30f)));
		SprintMultiplier = config.Bind("Ghost", "SprintMultiplier", 2f, new ConfigDescription(
			"Speed multiplier while holding the sprint key.",
			new AcceptableValueRange<float>(1f, 5f)));
		AttackChargeSeconds = config.Bind("Ghost", "AttackChargeSeconds", 0.8f, new ConfigDescription(
			"How long the ghost glows before exploding after the attack is triggered.",
			new AcceptableValueRange<float>(0.1f, 3f)));
		AttackRevertSeconds = config.Bind("Ghost", "AttackRevertSeconds", 1f, new ConfigDescription(
			"Seconds after the explosion until the player automatically reverts to normal.",
			new AcceptableValueRange<float>(0.3f, 6f)));
		AttackRadius = config.Bind("Ghost", "AttackRadius", 10f, new ConfigDescription(
			"Radius in which nearby players are knocked down by the ghost explosion.",
			new AcceptableValueRange<float>(2f, 30f)));
		KnockbackForce = config.Bind("Ghost", "KnockbackForce", 15f, new ConfigDescription(
			"How strongly nearby players are launched by the ghost explosion.",
			new AcceptableValueRange<float>(0f, 40f)));
		CameraDistance = config.Bind("Ghost Camera", "Distance", 16f, new ConfigDescription(
			"Third-person ghost camera distance.",
			new AcceptableValueRange<float>(8f, 30f)));
		CameraHeight = config.Bind("Ghost Camera", "Height", 5f, new ConfigDescription(
			"Third-person ghost camera height above the player.",
			new AcceptableValueRange<float>(2f, 14f)));
		CameraFov = config.Bind("Ghost Camera", "Fov", 80f, new ConfigDescription(
			"Field of view while in ghost form.",
			new AcceptableValueRange<float>(60f, 110f)));

		Log.LogInfo("[I'm a Ghost] Module loaded (integrated into Transform).");
	}

	private static void ConfigureOptionalCharacterPatches()
	{
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "RPCA_Die", Type.EmptyTypes,
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterRpcDiePrefix), "Character.RPCA_Die()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "RPCA_SetDead", Type.EmptyTypes,
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterRpcSetDeadPrefix), "Character.RPCA_SetDead()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "RPCA_PassOut", Type.EmptyTypes,
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterRpcPassOutPrefix), "Character.RPCA_PassOut()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "DieInstantly", Type.EmptyTypes,
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterDieInstantlyPrefix), "Character.DieInstantly()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "HandleDeath", Type.EmptyTypes,
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterHandleDeathPrefix), "Character.HandleDeath()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "WarpPlayerRPC", new[] { typeof(Vector3), typeof(bool) },
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterWarpPlayerRpcPrefix), "Character.WarpPlayerRPC(Vector3,bool)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "WarpPlayer", new[] { typeof(Vector3), typeof(bool) },
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterWarpPlayerPrefix), "Character.WarpPlayer(Vector3,bool)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "Fall", new[] { typeof(float), typeof(float) },
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterFallPrefix), "Character.Fall(float,float)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Ghost", typeof(Character), "RPCA_Fall", new[] { typeof(float), typeof(float) },
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.CharacterRpcFallPrefix), "Character.RPCA_Fall(float,float)");
	}

	/// <summary>
	/// Applies the camera fallback: a Harmony postfix on MainCameraMovement.LateUpdate that
	/// re-points the camera at the ghost after the vanilla camera code runs. This makes the
	/// override independent of the hard-coded DefaultExecutionOrder (600 &gt; 500), which would
	/// silently break if the game changed MainCameraMovement's order or added a higher-order
	/// camera controller. The type is resolved by name at runtime (it lives in the global
	/// namespace), and on success GhostController.CameraOverridePatchActive is set so the
	/// controller stops double-driving the camera from its own LateUpdate.
	/// </summary>
	private static void ConfigureCameraFallbackPatch()
	{
		if (PatchUtility.TryPatchCameraFallback(_harmony, Log, "I'm a Ghost",
			typeof(GhostHarmonyPatches), nameof(GhostHarmonyPatches.MainCameraMovementLateUpdatePostfix)))
		{
			GhostController.CameraOverridePatchActive = true;
			Log.LogInfo("[I'm a Ghost] Camera Harmony fallback active (MainCameraMovement.LateUpdate postfix).");
		}
	}

	/// <summary>Per-frame module maintenance, driven by the unified Transform plugin.</summary>
	internal static void Tick()
	{
		try
		{
			if (_controller != null && _controller.Active && !_controller.IsValid())
			{
				ForceExit();
			}
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Ghost] Module.Tick: " + ex);
		}
	}

	internal static void Shutdown()
	{
		try
		{
			ForceExit();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Ghost] Exit during cleanup failed: " + ex.Message);
		}

		try
		{
			_harmony?.UnpatchSelf();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Ghost] Harmony unpatch failed (non-fatal): " + ex.Message);
		}
	}

	/// <summary>State gate shared with the unified menu: may the local player enter ghost form now?</summary>
	internal static bool CanEnter(Character character)
	{
		if (IsActive) return false;
		return CanTransform(character);
	}

	private static bool CanTransform(Character character)
	{
		if (GhostController.IsLocalGhostCharacter(character))
		{
			Log?.LogWarning("[I'm a Ghost] Already in ghost form.");
			return false;
		}
		return FormValidation.IsValid(Log, "I'm a Ghost", FormValidation.ValidateTransformable(character));
	}

	/// <summary>Enters ghost form. Returns true when the controller accepted the request.</summary>
	internal static bool Enter(Character character)
	{
		if (_controller != null && _controller.Active) return false;
		try
		{
			_controller = character.gameObject.GetComponent<GhostController>();
			if (_controller == null)
			{
				_controller = character.gameObject.AddComponent<GhostController>();
			}
			_controller.EnterGhost(character);
			return _controller.Active;
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Ghost] Failed to enter ghost form:\n" + ex);
			_controller = null;
			return false;
		}
	}

	internal static void Exit()
	{
		try
		{
			_controller?.ExitGhost();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Ghost] Failed to exit ghost form:\n" + ex);
		}
	}

	internal static void ForceExit()
	{
		if (_controller == null)
		{
			return;
		}
		try
		{
			_controller.ExitGhost();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Ghost] ForceExit failed: " + ex.Message);
		}
		_controller = null;
	}
}
