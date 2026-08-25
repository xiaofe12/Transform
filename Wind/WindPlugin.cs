using System;
using System.Collections;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Transform.Core;
using PhotonPlayer = Photon.Realtime.Player;

namespace ImTornado;

/// <summary>
/// Tornado form module, adapted from the standalone "I'm a Tornado" BepInEx plugin into a static
/// module driven by the unified Transform plugin. All controller/patch code is unchanged.
/// </summary>
internal static class WindPlugin
{
	public const string Id = "com.github.Thanks.ImTornado";
	public const string Name = "I'm a Tornado";
	public const string Version = "0.1.0";

	internal static ManualLogSource Log;

	/// <summary>Photon player custom-property key used to advertise that this player runs the mod.</summary>
	internal const string ModPlayerProperty = "ImTornado.Mod";

	internal static ConfigEntry<float> MovementSpeed;
	internal static ConfigEntry<float> HoverHeight;
	internal static ConfigEntry<float> CameraDistance;
	internal static ConfigEntry<float> CameraHeight;
	internal static ConfigEntry<float> CameraFov;
	internal static ConfigEntry<bool> UnmoddedRoomSupport;
	internal static ConfigEntry<float> PushForce;

	private static Harmony _harmony;
	private static TornadoController _controller;
	private static bool _switching;
	private static bool _advertised;
	private static bool _initialized;

	/// <summary>True while the local player is in tornado form.</summary>
	internal static bool IsActive => _controller != null && _controller.Active;

	internal static void Initialize(ConfigFile config, ManualLogSource log)
	{
		if (_initialized) return;
		_initialized = true;
		Log = log;
		_harmony = new Harmony(Id);

		_harmony.PatchAll(typeof(WindHarmonyPatches));
		ConfigureOptionalCharacterPatches();

		MovementSpeed = config.Bind("Tornado", "MovementSpeed", 12f, new ConfigDescription(
			"How fast the tornado moves with WASD.",
			new AcceptableValueRange<float>(0f, 40f)));
		HoverHeight = config.Bind("Tornado", "HoverHeight", 7f, new ConfigDescription(
			"How high the tornado hovers above the ground.",
			new AcceptableValueRange<float>(1f, 25f)));
		CameraDistance = config.Bind("Tornado Camera", "Distance", 18f, new ConfigDescription(
			"Third-person tornado camera distance.",
			new AcceptableValueRange<float>(8f, 30f)));
		CameraHeight = config.Bind("Tornado Camera", "Height", 6f, new ConfigDescription(
			"Third-person tornado camera height above the player.",
			new AcceptableValueRange<float>(2f, 14f)));
		CameraFov = config.Bind("Tornado Camera", "Fov", 82f, new ConfigDescription(
			"Field of view while in tornado form.",
			new AcceptableValueRange<float>(60f, 110f)));
		UnmoddedRoomSupport = config.Bind("Tornado", "UnmoddedRoomSupport", true,
			"In rooms with unmodded players: make their tornado visual chase waypoints near you (vanilla 15 m/s tornado AI) and push them with real force RPCs.");
		PushForce = config.Bind("Tornado", "PushForce", 20f, new ConfigDescription(
			"Push acceleration (m/s^2) applied to unmodded players caught in the funnel.",
			new AcceptableValueRange<float>(0f, 60f)));

		Log.LogInfo("[I'm a Tornado] Module loaded (integrated into Transform).");
	}

	private static void ConfigureOptionalCharacterPatches()
	{
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "RPCA_Die", Type.EmptyTypes,
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterRpcDiePrefix), "Character.RPCA_Die()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "RPCA_SetDead", Type.EmptyTypes,
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterRpcSetDeadPrefix), "Character.RPCA_SetDead()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "RPCA_PassOut", Type.EmptyTypes,
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterRpcPassOutPrefix), "Character.RPCA_PassOut()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "DieInstantly", Type.EmptyTypes,
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterDieInstantlyPrefix), "Character.DieInstantly()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "HandleDeath", Type.EmptyTypes,
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterHandleDeathPrefix), "Character.HandleDeath()");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "WarpPlayerRPC", new[] { typeof(Vector3), typeof(bool) },
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterWarpPlayerRpcPrefix), "Character.WarpPlayerRPC(Vector3,bool)");
		PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Tornado", typeof(Character), "WarpPlayer", new[] { typeof(Vector3), typeof(bool) },
			typeof(WindHarmonyPatches), nameof(WindHarmonyPatches.CharacterWarpPlayerPrefix), "Character.WarpPlayer(Vector3,bool)");
	}

	// ------------------------------------------------------------------
	// Mod presence advertising: while in a room we stamp a custom property
	// onto our Photon player so other modded clients can tell us apart
	// from unmodded players. The unmodded-room fallbacks (waypoint chase
	// + real push RPCs) key off exactly this marker.
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
			Log?.LogError("[I'm a Tornado] Module.Tick: " + ex);
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
			Log?.LogWarning("[I'm a Tornado] Exit during cleanup failed: " + ex.Message);
		}

		try
		{
			_harmony?.UnpatchSelf();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Tornado] Harmony unpatch failed (non-fatal): " + ex.Message);
		}
	}

	/// <summary>State gate shared with the unified menu: may the local player enter tornado form now?</summary>
	internal static bool CanEnter(Character character)
	{
		if (IsActive) return false;
		return CanTransform(character);
	}

	private static bool CanTransform(Character character)
	{
		if (TornadoController.IsLocalTornadoCharacter(character))
		{
			FormValidation.ReportFailure(Log, "I'm a Tornado", "[I'm a Tornado] Already in tornado form.");
			return false;
		}
		return FormValidation.IsValid(Log, "I'm a Tornado", FormValidation.ValidateTransformable(character));
	}

	/// <summary>Enters tornado form. Returns true when the controller accepted the request.</summary>
	internal static bool Enter(Character character)
	{
		if (_switching) return false;
		if (!CanTransform(character)) return false;
		_switching = true;
		try
		{
			_controller = character.gameObject.GetComponent<TornadoController>();
			if (_controller == null)
			{
				_controller = character.gameObject.AddComponent<TornadoController>();
			}
			_controller.EnterTornado(character);
			return _controller.Active;
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Tornado] Failed to enter tornado form:\n" + ex);
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
			_controller?.ExitTornado();
			_controller = null;
		}
		catch (Exception ex)
		{
			Log?.LogError("[I'm a Tornado] Failed to exit tornado form:\n" + ex);
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
			_controller.ExitTornado();
		}
		catch (Exception ex)
		{
			Log?.LogWarning("[I'm a Tornado] ForceExit failed: " + ex.Message);
		}
		_controller = null;
	}
}
