using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Transform.Core;

namespace ImRaven;

/// <summary>
/// Shared Condor/Raven form module, driven by the unified Transform plugin. Binds the config sections,
/// installs the Harmony patches (vanilla grab auto-release guard + camera fallback), keeps the
/// custom network sync event registered and orchestrates form enter/exit on RavenController.
/// </summary>
internal static class RavenPlugin
{
	public const string Id = "com.github.Thanks.Transform.Raven";

	internal static ManualLogSource Log;

	internal static ConfigEntry<KeyCode> UpKey;
	internal static ConfigEntry<KeyCode> DownKey;
	internal static ConfigEntry<KeyCode> SprintKey;
	internal static ConfigEntry<float> MovementSpeed;
	internal static ConfigEntry<float> VerticalSpeed;
	internal static ConfigEntry<float> SprintMultiplier;
	internal static ConfigEntry<float> CameraDistance;
	internal static ConfigEntry<float> CameraHeight;
	internal static ConfigEntry<float> CameraFov;

	private static Harmony _harmony;
	private static RavenController _controller;
	private static bool _initialized;

	/// <summary>True while the local player is in Condor form.</summary>
	internal static bool IsActive => _controller != null && _controller.Active && !_controller.IsCrow;
	internal static bool IsCrowActive => _controller != null && _controller.Active && _controller.IsCrow;
	internal static BirdFlightSettings CondorSettings, CrowSettings;

	internal static void Initialize(ConfigFile config, ManualLogSource log)
	{
		if (_initialized) return;
		_initialized = true;
		Log = log;
		_harmony = new Harmony(Id);

		_harmony.PatchAll(typeof(RavenHarmonyPatches));
		ConfigureCameraFallbackPatch();

		UpKey = config.Bind("Raven Controls", "UpKey", KeyCode.Space,
			"Hold Space to fly up while in Condor form.");
		DownKey = config.Bind("Raven Controls", "DownKey", KeyCode.LeftControl,
			"Hold Ctrl to fly down while in Condor form.");
		SprintKey = config.Bind("Raven Controls", "SprintKey", KeyCode.LeftShift,
			"Hold Shift to sprint (move faster) while in Condor form.");
		MovementSpeed = config.Bind("Raven", "MovementSpeed", 12f, new ConfigDescription(
			"How fast the Condor glides with WASD.",
			new AcceptableValueRange<float>(0f, 40f)));
		VerticalSpeed = config.Bind("Raven", "VerticalSpeed", 9f, new ConfigDescription(
			"How fast the Condor rises and sinks.",
			new AcceptableValueRange<float>(0f, 30f)));
		SprintMultiplier = config.Bind("Raven", "SprintMultiplier", 1.8f, new ConfigDescription(
			"Speed multiplier while holding the sprint key.",
			new AcceptableValueRange<float>(1f, 5f)));
		CameraDistance = config.Bind("Raven Camera", "Distance", 8f, new ConfigDescription(
			"Third-person Condor camera distance.",
			new AcceptableValueRange<float>(3f, 15f)));
		CameraHeight = config.Bind("Raven Camera", "Height", 2.5f, new ConfigDescription(
			"Third-person Condor camera height above the player.",
			new AcceptableValueRange<float>(0.3f, 8f)));
		CameraFov = config.Bind("Raven Camera", "Fov", 80f, new ConfigDescription(
			"Field of view while in Condor form.",
			new AcceptableValueRange<float>(60f, 110f)));

		CondorSettings = new BirdFlightSettings {
			UpKey = UpKey, DownKey = DownKey, SprintKey = SprintKey, MovementSpeed = MovementSpeed,
			VerticalSpeed = VerticalSpeed, SprintMultiplier = SprintMultiplier,
			CameraDistance = CameraDistance, CameraHeight = CameraHeight, CameraFov = CameraFov };
		CrowSettings = BirdFlightSettings.CreateCrow(config);

		// Shared flight sync, with separate Condor/Raven visual tags (modded clients only).
		PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;

		Log.LogInfo("[Birds] Condor and Raven modules loaded (integrated into Transform).");
	}

	private static void ConfigureCameraFallbackPatch()
	{
		if (PatchUtility.TryPatchCameraFallback(_harmony, Log, "Raven",
			typeof(RavenHarmonyPatches), nameof(RavenHarmonyPatches.MainCameraMovementLateUpdatePostfix)))
		{
			RavenController.CameraOverridePatchActive = true;
			Log.LogInfo("[Condor] Camera Harmony fallback active (MainCameraMovement.LateUpdate postfix).");
		}
	}

	private static void OnPhotonEvent(EventData photonEvent)
	{
		RavenController.ApplyNetworkSyncEvent(photonEvent);
	}

	/// <summary>Per-frame module maintenance, driven by the unified Transform plugin.</summary>
	internal static void Tick()
	{
		try
		{
			RavenController.TickRemoteVisuals();
			if (_controller != null && _controller.Active && !_controller.IsValid())
			{
				ForceExit();
			}
		}
		catch (Exception ex)
		{
			Log?.LogError("[Condor] Module.Tick: " + ex);
		}
	}

	internal static void Shutdown()
	{
		try
		{
			PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEvent;
		}
		catch
		{
		}
		try
		{
			ForceExit();
			RavenController.ClearRemoteVisuals();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Condor] Exit during cleanup failed: " + ex.Message);
		}

		try
		{
			_harmony?.UnpatchSelf();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Condor] Harmony unpatch failed (non-fatal): " + ex.Message);
		}
	}

	/// <summary>State gate shared with the unified menu for either bird.</summary>
	internal static bool CanEnter(Character character, bool crow = false)
	{
		if (_controller != null && _controller.Active) return false;
		if (RavenController.IsLocalRavenCharacter(character))
		{
			FormValidation.ReportFailure(Log, "Raven", "[Condor] Already in Condor form.");
			return false;
		}
		if (!FormValidation.IsValid(Log, "Raven", FormValidation.ValidateTransformable(character))) return false;
		if (!RavenController.EnsureVisualTemplate(crow))
		{
			FormValidation.ReportFailure(Log, crow ? "CrowVisual" : "CondorVisual", crow ? "[Raven] Could not load native Raven resources." : "[Condor] Could not load Condor from the scene or installed game resources; check for missing/changed game files in the error above.");
			return false;
		}
		FormValidation.ClearFailure(crow ? "CrowVisual" : "CondorVisual");
		return true;
	}

	/// <summary>Enters the requested bird. Returns true when the controller accepted the request.</summary>
	internal static bool Enter(Character character, bool crow = false)
	{
		if (_controller != null && _controller.Active) return false;
		if (!CanEnter(character, crow)) return false;
		try
		{
			_controller = character.gameObject.GetComponent<RavenController>();
			if (_controller == null)
			{
				_controller = character.gameObject.AddComponent<RavenController>();
			}
			_controller.EnterRaven(character, crow);
			return _controller.Active;
		}
		catch (Exception ex)
		{
			Log?.LogError((crow ? "[Raven]" : "[Condor]") + " Failed to enter form:\n" + ex);
			_controller?.ExitRaven();
			_controller = null;
			return false;
		}
	}

	internal static void Exit()
	{
		try
		{
			_controller?.ExitRaven();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogError("[Condor] Failed to exit Condor form:\n" + ex);
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
			_controller.ExitRaven();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Condor] ForceExit failed: " + ex.Message);
		}
		_controller = null;
	}
}
