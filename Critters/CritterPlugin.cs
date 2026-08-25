using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Transform.Core;
using PhotonPlayer = Photon.Realtime.Player;

namespace ImCritter;

/// <summary>
/// Critter form module (frog / beetle / scorpion / coconut / bomb), driven by the unified Transform plugin.
/// Binds per-kind config sections, installs the Harmony patches (AI suppression, buried-body
/// broadcast, remote rider hiding, endgame safety nets) and orchestrates form enter/exit on
/// the shared CritterController. The menu key handling lives in the unified TransformPlugin.
///
/// Unmodded-room support: like the tornado module, we advertise our presence on the Photon
/// player so modded clients can tell modded from unmodded players. When unmodded players are
/// in the room, the controller pins the critter to the real position on every remote client
/// through the vanilla Item RPCs and broadcasts the vanilla mob state — see
/// CritterController.UpdateUnmoddedSync.
/// </summary>
internal static class CritterPlugin
{
	public const string Id = "com.github.Thanks.Transform.Critter";
	public const string Name = "Critter Forms";
	public const string Version = "0.9.8";
	private const float OldFrogJumpPowerDefault = 1.6f;
	private const float PreviousFrogJumpPowerDefault = 0.85f;
	private const float NewFrogJumpPowerDefault = 1.2f;

	/// <summary>Photon player custom-property key used to advertise that this player runs the mod.</summary>
	internal const string ModPlayerProperty = "ImCritter.Mod";

	internal static ManualLogSource Log;

	private static Harmony _harmony;
	private static CritterController _controller;
	private static bool _switching;
	private static bool _initialized;
	private static bool _advertised;

	/// <summary>
	/// In rooms with unmodded players: pin the critter to the rider's real position on every
	/// remote client (vanilla Item.SetKinematicAndResetSyncData RPC) and broadcast the vanilla
	/// mob state (Mob.RPC_SyncMobState) so their copies stay controllable and never run the
	/// wildlife AI. The owner's local physics feel is unchanged.
	/// </summary>
	internal static ConfigEntry<bool> UnmoddedRoomSupport;

	// Control bindings (gamepad-friendly: bind to JoystickButtonN in the cfg)
	internal static ConfigEntry<KeyCode> JumpKey;
	internal static ConfigEntry<KeyCode> AttackKey;

	// ---- per-kind config (arrays indexed by CritterKind) ----
	private static ConfigEntry<float>[] _movementForce = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _sprintMultiplier = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _maxSpeed = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _jumpSpeed = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _jumpPower = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _attackCooldown = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _cameraDistance = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _cameraHeight = new ConfigEntry<float>[6];
	private static ConfigEntry<float>[] _cameraFov = new ConfigEntry<float>[6];

	/// <summary>True while the local player is in any critter form.</summary>
	internal static bool IsActive => _controller != null && _controller.Active;

	internal static CritterKind ActiveKind => _controller != null && _controller.Active ? _controller.Kind : (CritterKind)(-1);

	// Config accessors used by the controller — never null after Initialize.
	internal static ConfigEntry<float> MovementForce(CritterKind kind) => _movementForce[(int)kind];
	internal static ConfigEntry<float> SprintMultiplier(CritterKind kind) => _sprintMultiplier[(int)kind];
	internal static ConfigEntry<float> MaxSpeed(CritterKind kind) => _maxSpeed[(int)kind];
	internal static ConfigEntry<float> JumpSpeed(CritterKind kind) => _jumpSpeed[(int)kind];
	internal static ConfigEntry<float> JumpPower(CritterKind kind) => _jumpPower[(int)kind];
	internal static ConfigEntry<float> AttackCooldown(CritterKind kind) => _attackCooldown[(int)kind];
	internal static ConfigEntry<float> CameraDistance(CritterKind kind) => _cameraDistance[(int)kind];
	internal static ConfigEntry<float> CameraHeight(CritterKind kind) => _cameraHeight[(int)kind];
	internal static ConfigEntry<float> CameraFov(CritterKind kind) => _cameraFov[(int)kind];

	private static string Section(CritterKind kind) => kind switch
	{
		CritterKind.Frog => "Frog",
		CritterKind.Beetle => "Beetle",
		CritterKind.Scorpion => "Scorpion",
		CritterKind.Coconut => "Coconut",
		CritterKind.Bomb => "Bomb",
		CritterKind.Cactus => "Cactus",
		_ => "Critter"
	};

	internal static void Initialize(ConfigFile config, ManualLogSource log)
	{
		if (_initialized) return;
		_initialized = true;
		Log = log;
		_harmony = new Harmony(Id);
		_harmony.PatchAll(typeof(CritterHarmonyPatches));
		TryPatchFrogActionGuard();
		TryPatchBombFlareGuard();
		ConfigureOptionalEndgamePatches();
		BindConfig(config);

		// Scene switch (ending loads the Airport scene) destroys the networked critter along
		// with the old scene — revert before the new scene initializes its player.
		SceneManager.sceneLoaded += OnSceneLoaded;

		Log.LogInfo("[Critter] Module loaded (frog, beetle, scorpion, coconut and bomb forms).");
	}

	/// <summary>
	/// Installs the frog AI-tongue guard adaptively. The game assembly's RPCA_FrogAction has
	/// signature (PhotonView, FrogActionType, Vector3) — no sender info — but PEAKER's
	/// PEAKERRpcInfo patcher appends a PhotonMessageInfo parameter at runtime. We reflect the
	/// LIVE MethodInfo (AccessTools.Method by name) and pick the matching prefix:
	/// with info → exact sender check; without → movement-only block.
	/// </summary>
	private static void TryPatchFrogActionGuard()
	{
		try
		{
			// PEAKERRpcInfo may have already rewritten the method; look it up by name only.
			System.Reflection.MethodInfo target = AccessTools.Method(typeof(FrogTongue), "RPCA_FrogAction");
			if (target == null)
			{
				Log?.LogWarning("[Critter] RPCA_FrogAction not found; frog AI guard skipped.");
				return;
			}
			bool hasInfo = target.GetParameters().Any(p => p.ParameterType == typeof(PhotonMessageInfo));
			string prefixName = hasInfo ? nameof(CritterHarmonyPatches.FrogActionGuardWithInfo)
			                            : nameof(CritterHarmonyPatches.FrogActionGuard);
			System.Reflection.MethodInfo prefix = AccessTools.Method(typeof(CritterHarmonyPatches), prefixName);
			if (prefix == null)
			{
				Log?.LogWarning("[Critter] Prefix " + prefixName + " not found; frog AI guard skipped.");
				return;
			}
			_harmony.Patch(target, prefix: new HarmonyMethod(prefix));
			Log?.LogInfo("[Critter] FrogActionGuard installed (" + prefixName + ", PhotonMessageInfo=" + hasInfo + ").");
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Critter] FrogActionGuard patch failed: " + ex.Message);
		}
	}

	/// <summary>
	/// Installs the bomb fuse guard adaptively. PEAKERRpcInfo may append PhotonMessageInfo to
	/// SetFlareLitRPC; when present we allow only the bomb owner's RMB RPC and reject proximity
	/// auto-light RPCs from an unmodded master.
	/// </summary>
	private static void TryPatchBombFlareGuard()
	{
		try
		{
			System.Reflection.MethodInfo target = AccessTools.Method(typeof(Dynamite), "SetFlareLitRPC");
			if (target == null)
			{
				Log?.LogWarning("[Critter] Dynamite.SetFlareLitRPC not found; bomb fuse guard skipped.");
				return;
			}
			bool hasInfo = target.GetParameters().Any(p => p.ParameterType == typeof(PhotonMessageInfo));
			string prefixName = hasInfo ? nameof(CritterHarmonyPatches.BombFlareLitGuardWithInfo)
			                            : nameof(CritterHarmonyPatches.BombFlareLitGuard);
			System.Reflection.MethodInfo prefix = AccessTools.Method(typeof(CritterHarmonyPatches), prefixName);
			if (prefix == null)
			{
				Log?.LogWarning("[Critter] Prefix " + prefixName + " not found; bomb fuse guard skipped.");
				return;
			}
			_harmony.Patch(target, prefix: new HarmonyMethod(prefix));
			Log?.LogInfo("[Critter] Bomb flare guard installed (" + prefixName + ", PhotonMessageInfo=" + hasInfo + ").");
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Critter] Bomb flare guard patch failed: " + ex.Message);
		}
	}

	/// <summary>
	/// Installs the end-game safety net patches so the critter form is reverted before the
	/// end screen iterates characters or the Airport scene loads (same set as the other forms).
	/// Registered as optional patches: a missing/renamed method in some game build only skips
	/// its patch instead of failing the load.
	/// </summary>
	private static void ConfigureOptionalEndgamePatches()
	{
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Critter", typeof(Character), "RPCEndGame", Type.EmptyTypes,
			typeof(CritterHarmonyPatches), nameof(CritterHarmonyPatches.CharacterRPCEndGamePrefix), "Character.RPCEndGame()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Critter", typeof(GameOverHandler), "BeginAirportLoadRPC", Type.EmptyTypes,
			typeof(CritterHarmonyPatches), nameof(CritterHarmonyPatches.GameOverHandlerBeginAirportLoadRPCPrefix), "GameOverHandler.BeginAirportLoadRPC()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "Critter", typeof(EndScreen), "ReturnToAirport", Type.EmptyTypes,
			typeof(CritterHarmonyPatches), nameof(CritterHarmonyPatches.EndScreenReturnToAirportPrefix), "EndScreen.ReturnToAirport()");
	}

	private static void BindConfig(ConfigFile config)
	{
		BindKind(config, CritterKind.Frog, 14f, 14f, 12f, 9f, 6f, NewFrogJumpPowerDefault, 2f);
		// Direct velocity control: keep the beetle deliberate, with a modest sprint.
		BindKind(config, CritterKind.Beetle, 18f, 4.5f, 7f, 6f, 7f, 1f, 1.35f);
		// Scorpion uses conservative direct velocity control; Shift is a small free speed-up.
		BindKind(config, CritterKind.Scorpion, 7.5f, 4.2f, 5f, 6f, 7f, 1f, 1.1f);
		// Coconut is an item-form: WASD moves it, Space hops, RMB charges a slam.
		BindKind(config, CritterKind.Coconut, 10f, 8f, 8f, 6f, 5f, 1f, 1.7f);
		// Bomb rolls like the tumbleweed by default; RMB is the only fuse igniter.
		BindKind(config, CritterKind.Bomb, 22f, 18f, 15.65f, 6f, 4f, 1f, 2f);
		// Cactus is bound only so old configs remain readable; the form is not exposed or enterable.
		BindKind(config, CritterKind.Cactus, 18f, 14f, 12f, 6f, 4.5f, 1f, 1.8f);
		MigrateCritterDefaults();
		UnmoddedRoomSupport = config.Bind("Critter", "UnmoddedRoomSupport", true,
			"In rooms with unmodded players: pin the critter to the rider's real position on every "
			+ "remote client (vanilla Item.SetKinematicAndResetSyncData RPC) and broadcast the vanilla "
			+ "mob state (Mob.RPC_SyncMobState) so their copies stay controllable and never run the "
			+ "wildlife AI. The owner's local physics feel is unchanged. An unmodded MASTER client's "
			+ "own copy can still fire one AI tongue action — that cannot be patched remotely; the "
			+ "owner's client ignores it, and the pin pulls the visual back within a tick.");

		JumpKey = config.Bind("Critter Controls", "JumpKey", KeyCode.Space,
			"Press to hop/jump while in a critter form (frog leap, beetle/scorpion hop). "
			+ "Bind to a controller button (JoystickButtonN) for gamepad play.");
		AttackKey = config.Bind("Critter Controls", "AttackKey", KeyCode.Mouse1,
			"Press to attack/charge while in a critter form (beetle ram, scorpion sting, coconut "
			+ "slam). Defaults to right mouse; bind to a controller button (JoystickButtonN) for gamepad.");
	}

	private static void MigrateCritterDefaults()
	{
		ConfigEntry<float> frogJump = _jumpPower[(int)CritterKind.Frog];
		if (frogJump != null
		    && (Mathf.Abs(frogJump.Value - OldFrogJumpPowerDefault) <= 0.001f
		        || Mathf.Abs(frogJump.Value - PreviousFrogJumpPowerDefault) <= 0.001f))
		{
			frogJump.Value = NewFrogJumpPowerDefault;
			Log?.LogInfo("[Critter] Migrated frog JumpPower to 1.2 for direct launch-speed frog jumping.");
		}

		MigrateIfDefault(_jumpSpeed[(int)CritterKind.Frog], 6f, 12f, "Frog JumpSpeed");
		MigrateIfDefault(_sprintMultiplier[(int)CritterKind.Beetle], 2f, 1.5f, "Beetle SprintMultiplier");
		MigrateIfDefault(_movementForce[(int)CritterKind.Scorpion], 14f, 10f, "Scorpion MovementForce");
		MigrateIfDefault(_maxSpeed[(int)CritterKind.Scorpion], 13f, 5.5f, "Scorpion MaxSpeed");
		MigrateIfDefault(_jumpSpeed[(int)CritterKind.Scorpion], 6f, 5f, "Scorpion JumpSpeed");
		MigrateIfDefault(_sprintMultiplier[(int)CritterKind.Scorpion], 2f, 1.15f, "Scorpion SprintMultiplier");
		MigrateIfDefault(_movementForce[(int)CritterKind.Scorpion], 10f, 7.5f, "Scorpion MovementForce");
		MigrateIfDefault(_maxSpeed[(int)CritterKind.Scorpion], 5.5f, 4.2f, "Scorpion MaxSpeed");
		MigrateIfDefault(_sprintMultiplier[(int)CritterKind.Scorpion], 1.15f, 1.1f, "Scorpion SprintMultiplier");
		MigrateIfDefault(_maxSpeed[(int)CritterKind.Beetle], 5f, 4.5f, "Beetle MaxSpeed");
		MigrateIfDefault(_sprintMultiplier[(int)CritterKind.Beetle], 1.5f, 1.35f, "Beetle SprintMultiplier");
		MigrateIfDefault(_movementForce[(int)CritterKind.Bomb], 10f, 22f, "Bomb MovementForce");
		MigrateIfDefault(_maxSpeed[(int)CritterKind.Bomb], 7f, 18f, "Bomb MaxSpeed");
		MigrateIfDefault(_jumpSpeed[(int)CritterKind.Bomb], 7f, 15.65f, "Bomb JumpSpeed");
		MigrateIfDefault(_sprintMultiplier[(int)CritterKind.Bomb], 1.7f, 2f, "Bomb SprintMultiplier");
	}

	private static void MigrateIfDefault(ConfigEntry<float> entry, float oldDefault, float newDefault, string label)
	{
		if (entry == null) return;
		if (Mathf.Abs(entry.Value - oldDefault) > 0.001f) return;
		entry.Value = newDefault;
		Log?.LogInfo("[Critter] Migrated " + label + " to " + newDefault.ToString("0.##") + ".");
	}

	private static void BindKind(ConfigFile config, CritterKind kind,
		float movementForce, float maxSpeed, float jumpSpeed,
		float cameraDistance, float cameraHeight, float jumpPower = 1f, float sprintMultiplier = 2f)
	{
		string section = Section(kind);
		int i = (int)kind;

		_movementForce[i] = config.Bind(section, "MovementForce", movementForce, new ConfigDescription(
			"How strongly WASD pushes the critter (acceleration, mass-independent). Not used by the "
			+ "frog (hop-only movement); beetle/scorpion use MaxSpeed for direct player control.",
			new AcceptableValueRange<float>(0f, 60f)));
		_sprintMultiplier[i] = config.Bind(section, "SprintMultiplier", sprintMultiplier, new ConfigDescription(
			"Force/speed multiplier while holding the sprint key (drains the stamina bar).",
			new AcceptableValueRange<float>(1f, 5f)));
		_maxSpeed[i] = config.Bind(section, "MaxSpeed", maxSpeed, new ConfigDescription(
			"Horizontal speed cap for WASD movement. For the frog this is the horizontal leap distance/speed.",
			new AcceptableValueRange<float>(2f, 60f)));
		_jumpSpeed[i] = config.Bind(section, "JumpSpeed", jumpSpeed, new ConfigDescription(
			"Upward hop speed for Space. For the frog this is the direct vertical launch speed.",
			new AcceptableValueRange<float>(1f, 30f)));
		_jumpPower[i] = config.Bind(section, "JumpPower", jumpPower, new ConfigDescription(
			"Multiplier on frog direct launch speeds or hop speed for beetle/scorpion.",
			new AcceptableValueRange<float>(0.1f, 3f)));
		_attackCooldown[i] = config.Bind(section, "AttackCooldown", 1.5f, new ConfigDescription(
			"Seconds between attacks.",
			new AcceptableValueRange<float>(0.1f, 10f)));
		_cameraDistance[i] = config.Bind(section + " Camera", "Distance", cameraDistance, new ConfigDescription(
			"Third-person critter camera distance.",
			new AcceptableValueRange<float>(2f, 20f)));
		_cameraHeight[i] = config.Bind(section + " Camera", "Height", cameraHeight, new ConfigDescription(
			"Third-person critter camera height above the critter.",
			new AcceptableValueRange<float>(0.3f, 10f)));
		_cameraFov[i] = config.Bind(section + " Camera", "Fov", 78f, new ConfigDescription(
			"Field of view while in this critter form.",
			new AcceptableValueRange<float>(60f, 110f)));
	}

	private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		try
		{
			if (IsActive)
			{
				Log?.LogInfo("[Critter] Scene loaded while transformed; exiting critter form.");
				ForceExit();
			}
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Critter] Exit during scene load failed: " + ex.Message);
		}
	}

	/// <summary>Per-frame module maintenance, driven by the unified Transform plugin.</summary>
	internal static void Tick()
	{
		try
		{
			AdvertiseModPresence();

			if (_controller != null && _controller.Active && !_controller.IsValid())
			{
				ForceExit();
			}
		}
		catch (Exception ex)
		{
			Log?.LogError("[Critter] Module.Tick: " + ex);
		}
	}

	// ------------------------------------------------------------------
	// Mod presence advertising: while in a room we stamp a custom property
	// onto our Photon player so other modded clients can tell us apart from
	// unmodded players. The unmodded-room fallback (kinematic pin + mob
	// state broadcast) keys off exactly this marker.
	// ------------------------------------------------------------------

	private static void AdvertiseModPresence()
	{
		ModPresence.Advertise(ModPlayerProperty, Version, ref _advertised);
	}

	internal static bool PlayerHasMod(PhotonPlayer player)
	{
		return ModPresence.PlayerHasMod(player, ModPlayerProperty);
	}

	internal static bool RoomHasUnmoddedPlayers()
	{
		return ModPresence.RoomHasUnmoddedPlayers(ModPlayerProperty);
	}

	internal static IEnumerable<PhotonPlayer> UnmoddedRemotePlayers()
	{
		return ModPresence.UnmoddedRemotePlayers(ModPlayerProperty);
	}

	internal static bool MasterHasMod()
	{
		return ModPresence.MasterHasMod(ModPlayerProperty);
	}

	internal static void Shutdown()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		try { ForceExit(); } catch (Exception ex) { Log?.LogWarning("[Critter] Exit during cleanup failed: " + ex.Message); }
		try { _harmony?.UnpatchSelf(); } catch (Exception ex) { Log?.LogWarning("[Critter] Harmony unpatch failed: " + ex.Message); }
	}

	/// <summary>State gate shared with the unified menu: may the local player enter this form now?</summary>
	internal static bool CanEnter(Character character)
	{
		return CanTransform(character);
	}

	private static bool CanTransform(Character character)
	{
		if (IsActive)
		{
			return false;
		}
		if (!FormValidation.IsValid(Log, "Critter", FormValidation.ValidateTransformable(character)))
		{
			return false;
		}
		// 离线/单机也允许变身：SpawnCritter 会走本地实例化回退。仅当完全未连接（如主菜单，
		// 无本地角色可依附）时拒绝。
		if (!Photon.Pun.PhotonNetwork.IsConnected && !Photon.Pun.PhotonNetwork.OfflineMode)
		{
			FormValidation.ReportFailure(Log, "Critter", "[Critter] Critter forms require a Photon room or offline mode.");
			return false;
		}
		return true;
	}

	/// <summary>Enters the given critter form. Returns true when the controller accepted it.</summary>
	internal static bool Enter(Character character, CritterKind kind)
	{
		if (kind == CritterKind.Cactus)
		{
			Log?.LogWarning("[Critter] Cactus form is disabled.");
			return false;
		}
		if (_switching) return false;
		if (!CanTransform(character)) return false;
		_switching = true;
		try
		{
			_controller = character.gameObject.GetComponent<CritterController>();
			if (_controller == null)
			{
				_controller = character.gameObject.AddComponent<CritterController>();
			}
			return _controller.EnterCritter(character, kind);
		}
		catch (Exception ex)
		{
			Log?.LogError("[Critter] Failed to enter " + kind + " form:\n" + ex);
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
			_controller?.ExitCritter();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogError("[Critter] Failed to exit critter form: " + ex);
		}
		finally
		{
			_switching = false;
		}
	}

	internal static void ForceExit()
	{
		if (_controller == null) return;
		try
		{
			_controller.ExitCritter();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[Critter] ForceExit failed: " + ex.Message);
		}
		_controller = null;
	}
}
