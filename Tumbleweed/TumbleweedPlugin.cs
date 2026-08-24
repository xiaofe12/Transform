using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;
using Transform.Core;

namespace ImTumbleweed;

/// <summary>
/// Tumbleweed form module, adapted from the standalone "I'm a Tumbleweed" BepInEx plugin into a
/// static module driven by the unified Transform plugin. All controller/patch code is unchanged;
/// the module owns config binding, Harmony patch installation and form enter/exit orchestration.
/// The toggle key / hold-to-transform logic moved to the unified TransformPlugin menu.
/// </summary>
internal static class TumbleweedPlugin
{
	public const string Id = "com.github.Thanks.ImTumbleweed";
	public const string Name = "I'm a Tumbleweed";
	public const string Version = "0.1.0";

	internal static ManualLogSource Log;

	// Kept (controller reads them)
	internal static ConfigEntry<float> AutoRevertSeconds;
	internal static ConfigEntry<float> WeedRenewInterval;
	internal static ConfigEntry<KeyCode> JumpKey;
	internal static ConfigEntry<KeyCode> SprintKey;
	internal static ConfigEntry<float> MovementForce;
	internal static ConfigEntry<float> SprintMultiplier;
	internal static ConfigEntry<float> MaxSpeed;
	internal static ConfigEntry<float> JumpSpeed;
	internal static ConfigEntry<float> DashForce;
	internal static ConfigEntry<float> DashCooldown;
	internal static ConfigEntry<float> CameraDistance;
	internal static ConfigEntry<float> CameraHeight;
	internal static ConfigEntry<float> CameraFov;
	internal static ConfigEntry<bool> ShowWeedVisual;

	private static Harmony _harmony;
	private static TumbleweedController _controller;
	private static bool _switching;
	private static bool _initialized;

	/// <summary>True while the local player is in tumbleweed form.</summary>
	internal static bool IsActive => _controller != null && _controller.Active;

	internal static void Initialize(ConfigFile config, ManualLogSource log)
	{
		if (_initialized) return;
		_initialized = true;
		Log = log;
		_harmony = new Harmony(Id);

		_harmony.PatchAll(typeof(TumbleweedHarmonyPatches));
		ConfigureOptionalCharacterPatches();
		ConfigureOptionalEndgamePatches();
		ConfigureCameraFallbackPatch();
		BindConfig(config);

		// Scene switch (e.g. the ending loads the Airport scene) destroys the networked weed
		// along with the old scene. Revert before the new scene initializes its player,
		// otherwise the player would stay stuck in a form that no longer exists.
		SceneManager.sceneLoaded += OnSceneLoaded;
		PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;

		Log.LogInfo("[I'm a Tumbleweed] Module loaded (integrated into Transform).");
	}

	private static void BindConfig(ConfigFile config)
	{
		AutoRevertSeconds = config.Bind("Tumbleweed", "AutoRevertSeconds", 0f, new ConfigDescription(
			"How long the tumbleweed form lasts before the player automatically reverts. " +
			"0 = unlimited (the form lasts until the player reverts via the menu).",
			new AcceptableValueRange<float>(0f, 300f)));
		WeedRenewInterval = config.Bind("Tumbleweed", "WeedRenewInterval", 10f, new ConfigDescription(
			"How often the owner destroys and respawns the networked tumbleweed so unmodded " +
			"clients never lose it to their vanilla RemoveAfterSeconds self-destruct (~15s). " +
			"Must stay below 15. 0 = disabled (only matters in rooms with unmodded clients; " +
			"modded clients keep the weed alive through Harmony patches anyway).",
			new AcceptableValueRange<float>(0f, 14f)));
		JumpKey = config.Bind("Tumbleweed Controls", "JumpKey", KeyCode.Space,
			"Press Space while in tumbleweed form to hop. Only works when the weed is on the ground.");
		SprintKey = config.Bind("Tumbleweed Controls", "SprintKey", KeyCode.LeftShift,
			"Hold Shift to roll faster while in tumbleweed form.");
		MovementForce = config.Bind("Tumbleweed", "MovementForce", 22f, new ConfigDescription(
			"How strongly WASD pushes the tumbleweed (acceleration, mass-independent).",
			new AcceptableValueRange<float>(0f, 60f)));
		SprintMultiplier = config.Bind("Tumbleweed", "SprintMultiplier", 2f, new ConfigDescription(
			"Force/speed multiplier while holding the sprint key.",
			new AcceptableValueRange<float>(1f, 5f)));
		MaxSpeed = config.Bind("Tumbleweed", "MaxSpeed", 18f, new ConfigDescription(
			"Horizontal speed cap for WASD rolling (physics like rolling downhill can exceed it).",
			new AcceptableValueRange<float>(2f, 60f)));
		JumpSpeed = config.Bind("Tumbleweed", "JumpSpeed", 15.65f, new ConfigDescription(
			"Upward speed applied when hopping (about 5x the default jump height).",
			new AcceptableValueRange<float>(1f, 30f)));
		CameraDistance = config.Bind("Tumbleweed Camera", "Distance", 12f, new ConfigDescription(
			"Third-person tumbleweed camera distance.",
			new AcceptableValueRange<float>(6f, 30f)));
		CameraHeight = config.Bind("Tumbleweed Camera", "Height", 4f, new ConfigDescription(
			"Third-person tumbleweed camera height above the weed.",
			new AcceptableValueRange<float>(1f, 14f)));
		CameraFov = config.Bind("Tumbleweed Camera", "Fov", 80f, new ConfigDescription(
			"Field of view while in tumbleweed form.",
			new AcceptableValueRange<float>(60f, 110f)));
		ShowWeedVisual = config.Bind("Tumbleweed", "ShowWeedVisual", true,
			"Show the tumbleweed ball while transformed (the physics body stays active either way).");
		DashForce = config.Bind("Tumbleweed", "DashForce", 30f, new ConfigDescription(
			"Impulse applied toward the view direction on right-click dash.",
			new AcceptableValueRange<float>(5f, 80f)));
		DashCooldown = config.Bind("Tumbleweed", "DashCooldown", 1.2f, new ConfigDescription(
			"Seconds between right-click dashes.",
			new AcceptableValueRange<float>(0.2f, 10f)));
	}

	private static void ConfigureOptionalCharacterPatches()
	{
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "RPCA_Die", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterRpcDiePrefix), "Character.RPCA_Die()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "RPCA_SetDead", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterRpcSetDeadPrefix), "Character.RPCA_SetDead()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "RPCA_PassOut", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterRpcPassOutPrefix), "Character.RPCA_PassOut()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "DieInstantly", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterDieInstantlyPrefix), "Character.DieInstantly()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "HandleDeath", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterHandleDeathPrefix), "Character.HandleDeath()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "WarpPlayerRPC", new[] { typeof(Vector3), typeof(bool) },
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterWarpPlayerRpcPrefix), "Character.WarpPlayerRPC(Vector3,bool)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "WarpPlayer", new[] { typeof(Vector3), typeof(bool) },
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterWarpPlayerPrefix), "Character.WarpPlayer(Vector3,bool)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "Fall", new[] { typeof(float), typeof(float) },
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterFallPrefix), "Character.Fall(float,float)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "RPCA_Fall", new[] { typeof(float), typeof(float) },
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterRpcFallPrefix), "Character.RPCA_Fall(float,float)");
	}

	/// <summary>
	/// Patches the end-game path so the tumbleweed form is always reverted BEFORE the
	/// end-game iterates characters or the Airport scene loads. Registered as optional
	/// patches: a missing/renamed method in some game build only skips its patch instead
	/// of failing the load. The RPCEndGame hook covers the normal win/lose flow; the two
	/// airport-load hooks (GameOverHandler.BeginAirportLoadRPC, which runs on every client
	/// right when the airport load starts, and EndScreen.ReturnToAirport, the direct
	/// scene-load call) cover the win flow that never re-calls RPCEndGame before the scene
	/// swap - the critical safety net. SceneManager.sceneLoaded in Initialize is the last
	/// line of defense.
	/// </summary>
	private static void ConfigureOptionalEndgamePatches()
	{
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(Character), "RPCEndGame", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterRPCEndGamePrefix), "Character.RPCEndGame()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(CharacterStats), "Win", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterStatsWinPrefix), "CharacterStats.Win()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(CharacterStats), "GetFinalTimelineInfo", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterStatsGetFinalTimelineInfoPrefix), "CharacterStats.GetFinalTimelineInfo()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(CharacterStats), "GetFirstTimelineInfo", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.CharacterStatsGetFirstTimelineInfoPrefix), "CharacterStats.GetFirstTimelineInfo()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(PeakHandler), "EndCutscene", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.PeakHandlerEndCutscenePrefix), "PeakHandler.EndCutscene()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(GameOverHandler), "BeginAirportLoadRPC", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.GameOverHandlerBeginAirportLoadRPCPrefix), "GameOverHandler.BeginAirportLoadRPC()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tumbleweed", typeof(EndScreen), "ReturnToAirport", Type.EmptyTypes,
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.EndScreenReturnToAirportPrefix), "EndScreen.ReturnToAirport()");
	}

	/// <summary>
	/// Applies the camera fallback: a Harmony postfix on MainCameraMovement.LateUpdate that
	/// re-points the camera at the tumbleweed after the vanilla camera code runs (same
	/// defensive approach as the I'm a Ghost mod). On success the controller stops
	/// double-driving the camera from its own LateUpdate.
	/// </summary>
	private static void ConfigureCameraFallbackPatch()
	{
		if (PatchUtility.TryPatchCameraFallback(_harmony, Log, "I'm a Tumbleweed",
			typeof(TumbleweedHarmonyPatches), nameof(TumbleweedHarmonyPatches.MainCameraMovementLateUpdatePostfix)))
		{
			TumbleweedController.CameraOverridePatchActive = true;
			Log.LogInfo("[I'm a Tumbleweed] Camera Harmony fallback active (MainCameraMovement.LateUpdate postfix).");
		}
	}

	/// <summary>
	/// Final safety net for scene switches while transformed (most importantly the ending's
	/// Airport load). Runs when the new scene is already active - the old weed is destroyed,
	/// so ExitWeed skips the position restore and simply clears state / restores the local
	/// renderers, letting the new scene assign its own player.
	/// </summary>
	private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		try
		{
			TumbleweedController.ForceExitForEndGame();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Tumbleweed] Exit during scene load failed: " + ex.Message);
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
			Log?.LogError("[I'm a Tumbleweed] Module.Tick: " + ex);
		}
	}

	internal static void Shutdown()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEvent;
		try { ForceExit(); } catch (Exception ex) { Log?.LogWarning("[I'm a Tumbleweed] Exit during cleanup failed: " + ex.Message); }
		try { _harmony?.UnpatchSelf(); } catch (Exception ex) { Log?.LogWarning("[I'm a Tumbleweed] Harmony unpatch failed: " + ex.Message); }
	}

	private static void OnPhotonEvent(EventData photonEvent)
	{
		TumbleweedController.ApplyNetworkSyncEvent(photonEvent);
	}

	/// <summary>State gate shared with the unified menu: may the local player enter tumbleweed form now?</summary>
	internal static bool CanEnter(Character character)
	{
		return CanTransform(character);
	}

	private static bool CanTransform(Character character)
	{
		if (TumbleweedController.IsLocalWeedCharacter(character))
		{
			Log?.LogWarning("[I'm a Tumbleweed] Already in tumbleweed form.");
			return false;
		}
		return FormValidation.IsValid(Log, "I'm a Tumbleweed", FormValidation.ValidateTransformable(character));
	}

	/// <summary>Enters tumbleweed form. Returns true when the controller accepted the request.</summary>
	internal static bool Enter(Character character)
	{
		if (_switching) return false;
		if (!CanTransform(character)) return false;
		_switching = true;
		try
		{
			_controller = character.gameObject.GetComponent<TumbleweedController>();
			if (_controller == null)
			{
				_controller = character.gameObject.AddComponent<TumbleweedController>();
			}
			_controller.EnterWeed(character);
			return _controller.Active;
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Tumbleweed] Failed to enter tumbleweed form:\n" + ex);
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
			_controller?.ExitWeed();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Tumbleweed] Failed to exit tumbleweed form: " + ex);
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
			_controller.ExitWeed();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Tumbleweed] ForceExit failed: " + ex.Message);
		}
		_controller = null;
	}
}
