using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Transform.Core;

namespace Transform.Statue;

/// <summary>
/// Petrified-scout statue form module. Follows the proven Tumbleweed architecture: the player
/// Character stays as Character.localCharacter (so the vanilla stamina bar keeps working —
/// sprint drains it, jumps consume it), its ragdoll is parked in no-clip while the networked
/// vanilla "PetrifiedScout" prefab is spawned and driven with physics forces. The rider's
/// network broadcast is buried 30 m underground (see StatueHarmonyPatches), matching the
/// best-performing "hide the player underground" approach used by the other forms.
/// </summary>
internal static class StatuePlugin
{
	public const string Id = "com.github.Thanks.Transform.Statue";
	public const string Name = "Petrified Scout Form";
	public const string Version = "0.9.7";

	internal static ManualLogSource Log;

	internal static ConfigEntry<float> MovementForce;
	internal static ConfigEntry<float> SprintMultiplier;
	internal static ConfigEntry<float> JumpSpeed;
	internal static ConfigEntry<float> StaminaDrainPerSecond;
	internal static ConfigEntry<float> StaminaRegenPerSecond;
	internal static ConfigEntry<float> CameraDistance;
	internal static ConfigEntry<float> CameraHeight;
	internal static ConfigEntry<float> CameraFov;

	// Control bindings (gamepad-friendly: bind to JoystickButtonN in the cfg)
	internal static ConfigEntry<KeyCode> JumpKey;
	internal static ConfigEntry<KeyCode> SprintKey;

	private static Harmony _harmony;
	private static StatueController _controller;
	private static bool _switching;
	private static bool _initialized;

	/// <summary>True while the local player is in statue form.</summary>
	internal static bool IsActive => _controller != null && _controller.Active;

	internal static void Initialize(ConfigFile config, ManualLogSource log)
	{
		if (_initialized) return;
		_initialized = true;
		Log = log;
		_harmony = new Harmony(Id);

		_harmony.PatchAll(typeof(StatueHarmonyPatches));
		ConfigureOptionalEndgamePatches();
		ConfigureCameraFallbackPatch();
		BindConfig(config);

		// Scene switch (e.g. the ending loads the Airport scene) destroys the networked statue
		// along with the old scene. Revert before the new scene initializes its player.
		SceneManager.sceneLoaded += OnSceneLoaded;

		Log.LogInfo("[Statue] Module loaded (integrated into Transform).");
	}

	private static void BindConfig(ConfigFile config)
	{
		MovementForce = config.Bind("Statue", "MovementForce", 26f, new ConfigDescription(
			"How strongly WASD pushes the statue (acceleration, mass-independent).",
			new AcceptableValueRange<float>(0f, 60f)));
		SprintMultiplier = config.Bind("Statue", "SprintMultiplier", 1.8f, new ConfigDescription(
			"Force multiplier while holding Shift. Sprinting drains the shared stamina bar; when stamina runs out, sprinting stops.",
			new AcceptableValueRange<float>(1f, 5f)));
		JumpSpeed = config.Bind("Statue", "JumpSpeed", 11f, new ConfigDescription(
			"Upward speed applied when hopping. Each hop consumes stamina like a vanilla jump.",
			new AcceptableValueRange<float>(1f, 30f)));
		StaminaDrainPerSecond = config.Bind("Statue", "StaminaDrainPerSecond", 0.12f, new ConfigDescription(
			"How much of the shared stamina bar sprinting drains per second (the vanilla sprint drain is ~0.11).",
			new AcceptableValueRange<float>(0.01f, 1f)));
		StaminaRegenPerSecond = config.Bind("Statue", "StaminaRegenPerSecond", 0.18f, new ConfigDescription(
			"How much stamina regenerates per second while not sprinting.",
			new AcceptableValueRange<float>(0.01f, 1f)));
		CameraDistance = config.Bind("Statue Camera", "Distance", 6f, new ConfigDescription(
			"Third-person statue camera distance.",
			new AcceptableValueRange<float>(3f, 15f)));
		CameraHeight = config.Bind("Statue Camera", "Height", 2.5f, new ConfigDescription(
			"Third-person statue camera height above the statue.",
			new AcceptableValueRange<float>(1f, 8f)));
		CameraFov = config.Bind("Statue Camera", "Fov", 78f, new ConfigDescription(
			"Field of view while in statue form.",
			new AcceptableValueRange<float>(60f, 110f)));

		JumpKey = config.Bind("Statue Controls", "JumpKey", KeyCode.Space,
			"Press to hop while in statue form. Bind to a controller button (JoystickButtonN) for gamepad.");
		SprintKey = config.Bind("Statue Controls", "SprintKey", KeyCode.LeftShift,
			"Hold to sprint (drains stamina) while in statue form. Bind to a controller button (JoystickButtonN) for gamepad.");
	}

	/// <summary>
	/// End-game safety net: revert the statue form before the end screen iterates characters or
	/// the Airport scene loads. Registered as optional patches; a missing method in some game
	/// build only skips its patch.
	/// </summary>
	private static void ConfigureOptionalEndgamePatches()
	{
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Statue", typeof(Character), "RPCEndGame", Type.EmptyTypes,
			typeof(StatueHarmonyPatches), nameof(StatueHarmonyPatches.CharacterRPCEndGamePrefix), "Character.RPCEndGame()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Statue", typeof(CharacterStats), "Win", Type.EmptyTypes,
			typeof(StatueHarmonyPatches), nameof(StatueHarmonyPatches.CharacterStatsWinPrefix), "CharacterStats.Win()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Statue", typeof(GameOverHandler), "BeginAirportLoadRPC", Type.EmptyTypes,
			typeof(StatueHarmonyPatches), nameof(StatueHarmonyPatches.GameOverHandlerBeginAirportLoadRPCPrefix), "GameOverHandler.BeginAirportLoadRPC()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Statue", typeof(EndScreen), "ReturnToAirport", Type.EmptyTypes,
			typeof(StatueHarmonyPatches), nameof(StatueHarmonyPatches.EndScreenReturnToAirportPrefix), "EndScreen.ReturnToAirport()");
	}

	/// <summary>
	/// Camera fallback: a Harmony postfix on MainCameraMovement.LateUpdate that re-points the
	/// camera at the statue after the vanilla camera code runs (same defensive approach as the
	/// Tumbleweed/Ghost modules). On success the controller stops double-driving the camera from
	/// its own LateUpdate.
	/// </summary>
	private static void ConfigureCameraFallbackPatch()
	{
		if (PatchUtility.TryPatchCameraFallback(_harmony, Log, "Statue",
			typeof(StatueHarmonyPatches), nameof(StatueHarmonyPatches.MainCameraMovementLateUpdatePostfix)))
		{
			StatueController.CameraOverridePatchActive = true;
			Log.LogInfo("[Statue] Camera Harmony fallback active (MainCameraMovement.LateUpdate postfix).");
		}
	}

	/// <summary>
	/// Final safety net for scene switches while transformed (most importantly the ending's
	/// Airport load). Runs when the new scene is already active — the old statue is destroyed,
	/// so ExitStatue skips the position restore and simply clears state.
	/// </summary>
	private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		try
		{
			StatueController.ForceExitForEndGame();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Statue] Exit during scene load failed: " + ex.Message);
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
			Log?.LogError("[Statue] Module.Tick: " + ex);
		}
	}

	internal static void Shutdown()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		try { ForceExit(); } catch (Exception ex) { Log?.LogWarning("[Statue] Exit during cleanup failed: " + ex.Message); }
		try { _harmony?.UnpatchSelf(); } catch (Exception ex) { Log?.LogWarning("[Statue] Harmony unpatch failed: " + ex.Message); }
	}

	/// <summary>State gate shared with the unified menu: may the local player enter statue form now?</summary>
	internal static bool CanEnter(Character character)
	{
		return CanTransform(character);
	}

	private static bool CanTransform(Character character)
	{
		if (StatueController.IsLocalStatueCharacter(character))
		{
			Log?.LogWarning("[Statue] Already in statue form.");
			return false;
		}
		return FormValidation.IsValid(Log, "Statue", FormValidation.ValidateTransformable(character));
	}

	/// <summary>Enters statue form. Returns true when the controller accepted the request.</summary>
	internal static bool Enter(Character character)
	{
		if (_switching) return false;
		if (!CanTransform(character)) return false;
		_switching = true;
		try
		{
			_controller = character.gameObject.GetComponent<StatueController>();
			if (_controller == null)
			{
				_controller = character.gameObject.AddComponent<StatueController>();
			}
			_controller.EnterStatue(character);
			return _controller.Active;
		}
		catch (Exception ex)
		{
			Log?.LogError("[Statue] Failed to enter statue form:\n" + ex);
			_controller = null;
			return false;
		}
		finally
		{
			_switching = false;
		}
	}

	internal static void Exit()
	{
		_switching = true;
		try
		{
			_controller?.ExitStatue();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogError("[Statue] Failed to exit statue form: " + ex);
		}
		finally
		{
			_switching = false;
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
			_controller.ExitStatue();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Statue] ForceExit failed: " + ex.Message);
		}
		_controller = null;
	}
}
