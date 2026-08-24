using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using UnityEngine;
using UnityEngine.SceneManagement;
using Transform.Core;

namespace ImZombie;

/// <summary>
/// Zombie form module, adapted from the standalone "I'm a Zombie" BepInEx plugin into a static
/// module driven by the unified Transform plugin. All controller/patch code is unchanged; the
/// module owns config binding, Harmony patch installation and form enter/exit orchestration.
/// </summary>
internal static class ZombiePlugin
{
    public const string Id = "com.github.Thanks.ImZombie";
    public const string Name = "I'm a Zombie";
    public const string Version = "1.0.2";

    /// <summary>Custom Photon event code for hiding/showing the original player body on remote clients.</summary>
    /// <remarks>
    /// Photon's RaiseEvent rejects any code >= 200 ("must be less than 200 (0..199)"), so the old
    /// value 215 was silently dropped — the log shows "RaiseEvent(215) failed". PEAK registers its
    /// own packages on codes 1, 2 and 4, so we use a high unused value in the valid user range.
    /// </remarks>
    private const byte BodyVisibilityEventCode = 199;
    // Distinctive tag so we only handle OUR events on code 199. PEAK (and other
    // mods) also raise events on 199; without this tag we'd mis-cast their payload
    // and spam "Specified cast is not valid" (seen during endgame / Bot warping).
    private const int BodyVisibilityMagic = 0x5A210319;

    internal static ManualLogSource Log;
    private static readonly Dictionary<int, Renderer[]> RendererCache = new Dictionary<int, Renderer[]>();

    // Control bindings
    internal static ConfigEntry<KeyCode> AttackKey;
    internal static ConfigEntry<KeyCode> SprintKey;
    internal static ConfigEntry<KeyCode> JumpKey;
    internal static ConfigEntry<KeyCode> CrouchKey;
    internal static ConfigEntry<KeyCode> ClimbKey;

    // Movement config
    internal static ConfigEntry<float> MovementSpeed;
    internal static ConfigEntry<float> SprintMultiplier;
    internal static ConfigEntry<float> CrouchMultiplier;
    internal static ConfigEntry<float> JumpForce;
    internal static ConfigEntry<float> MaxVelocity;

    // Attack config
    internal static ConfigEntry<float> AttackDuration;
    internal static ConfigEntry<float> AttackCooldown;
    internal static ConfigEntry<float> AttackRevertSeconds;

    // Camera config
    internal static ConfigEntry<float> CameraDistance;
    internal static ConfigEntry<float> CameraHeight;
    internal static ConfigEntry<float> CameraFov;

    // Misc
    internal static ConfigEntry<bool> HidePlayerBody;
    internal static ConfigEntry<bool> ShowZombieName;
    internal static ConfigEntry<float> HiddenBodyDepth;
    internal static ConfigEntry<ZombieAppearanceOption> ZombieAppearance;

    /// <summary>Zombie appearance style: Player wears the player outfit; Mushroom is the normal zombie; MushroomMan is the phobia mushroom-man mesh.</summary>
    public enum ZombieAppearanceOption
    {
        Player = 0,
        Mushroom = 1,
        MushroomMan = 2
    }

    private static Harmony _harmony;
    private static ZombieController _controller;
    private static bool _switching;
    private static float _nextStatusGuardRetry;
    private static int _statusGuardRetryAttempts;
    private static bool _statusGuardsGaveUp;
    private static bool _initialized;

    /// <summary>Appearance override requested by the menu (player zombie vs mushroom zombie).
    /// Set right before Enter; consumed by ZombieController.SpawnZombiePrefab.</summary>
    internal static ZombieAppearanceOption? PendingAppearance;

    /// <summary>True while the local player is in zombie form.</summary>
    internal static bool IsActive => _controller != null && _controller.Active;

    internal static void Initialize(ConfigFile config, ManualLogSource log)
    {
        if (_initialized) return;
        _initialized = true;
        Log = log;
        _harmony = new Harmony(Id);

        BindConfig(config);
        _harmony.PatchAll(typeof(ZombieHarmonyPatches));
        ConfigureOptionalCharacterPatches();
        ConfigureOptionalEndgamePatches();

        // Scene switch (e.g. the ending loads the Airport scene) destroys the networked zombie and
        // the stashed player body along with the old scene. Revert before the new scene
        // initializes its player, otherwise Character.localCharacter still points at the destroyed
        // zombie and the player can't move or board the plane in the new scene.
        SceneManager.sceneLoaded += OnSceneLoaded;

        PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;

        Log.LogInfo("[I'm a Zombie] Module loaded (integrated into Transform).");
    }

    private static void BindConfig(ConfigFile config)
    {
        AttackKey = config.Bind("Zombie Controls", "AttackKey", KeyCode.Mouse1,
            "Right-click while in zombie form to lunge forward and bite.");
        SprintKey = config.Bind("Zombie Controls", "SprintKey", KeyCode.LeftShift,
            "Hold to sprint (move faster) while in zombie form.");
        JumpKey = config.Bind("Zombie Controls", "JumpKey", KeyCode.Space,
            "Press to jump while in zombie form.");
        CrouchKey = config.Bind("Zombie Controls", "CrouchKey", KeyCode.LeftControl,
            "Hold to crouch (move slower, lower camera) while in zombie form.");
        ClimbKey = config.Bind("Zombie Controls", "ClimbKey", KeyCode.Mouse0,
            "Hold to climb (stick to walls / ladders) while in zombie form. Bind to a controller "
            + "button (JoystickButtonN) for gamepad; the game's unified Use button also triggers "
            + "climbing on a controller, matching the Scoutmaster form.");

        MovementSpeed = config.Bind("Zombie", "MovementSpeed", 12f, new ConfigDescription(
            "How fast the zombie walks with WASD.", new AcceptableValueRange<float>(0f, 40f)));
        SprintMultiplier = config.Bind("Zombie", "SprintMultiplier", 2f, new ConfigDescription(
            "Speed multiplier while holding the sprint key.", new AcceptableValueRange<float>(1f, 5f)));
        CrouchMultiplier = config.Bind("Zombie", "CrouchMultiplier", 0.5f, new ConfigDescription(
            "Speed multiplier while holding the crouch key.", new AcceptableValueRange<float>(0.1f, 1f)));
        JumpForce = config.Bind("Zombie", "JumpForce", 10f, new ConfigDescription(
            "Upward impulse when jumping.", new AcceptableValueRange<float>(0f, 25f)));
        MaxVelocity = config.Bind("Zombie", "MaxVelocity", 50f, new ConfigDescription(
            "Maximum velocity cap for the zombie ragdoll.", new AcceptableValueRange<float>(10f, 100f)));

        AttackDuration = config.Bind("Zombie Attack", "Duration", 1.5f, new ConfigDescription(
            "How long the vanilla-style lunge charge lasts (seconds). Fallback when the zombie prefab's own lungeTime is unavailable.",
            new AcceptableValueRange<float>(0.1f, 2f)));
        AttackCooldown = config.Bind("Zombie Attack", "Cooldown", 1.0f, new ConfigDescription(
            "Minimum time between attacks (seconds).", new AcceptableValueRange<float>(0.1f, 5f)));
        AttackRevertSeconds = config.Bind("Zombie Attack", "AutoRevertSeconds", 0f, new ConfigDescription(
            "If > 0, auto-revert to normal form this many seconds after attacking. 0 = no auto-revert.",
            new AcceptableValueRange<float>(0f, 10f)));

        CameraDistance = config.Bind("Zombie Camera", "Distance", 2.8f, new ConfigDescription(
            "Third-person zombie camera distance.", new AcceptableValueRange<float>(1.5f, 6f)));
        CameraHeight = config.Bind("Zombie Camera", "Height", 0.85f, new ConfigDescription(
            "Third-person camera height offset above the zombie's center.", new AcceptableValueRange<float>(0.3f, 1.8f)));
        CameraFov = config.Bind("Zombie Camera", "Fov", 80f, new ConfigDescription(
            "Field of view while in zombie form (game FOV is preserved; this is a fallback only).",
            new AcceptableValueRange<float>(60f, 110f)));

        HidePlayerBody = config.Bind("Zombie Misc", "HidePlayerBody", true,
            "Hide the player's original body while in zombie form (synced to all clients).");
        ShowZombieName = config.Bind("Zombie Misc", "ShowZombieName", false,
            "Show the player's name on the zombie's nameplate.");
        HiddenBodyDepth = config.Bind("Zombie Misc", "HiddenBodyDepth", 30f, new ConfigDescription(
            "How far below the player's feet to stash the hidden body while transformed. The body is " +
            "moved underground and its position keeps being broadcast, so un-modded clients (incl. " +
            "late joiners) can't see it; lighting stays correct because the local character is " +
            "swapped to the zombie (on the ground).",
            new AcceptableValueRange<float>(5f, 200f)));
        ZombieAppearance = config.Bind("Zombie Misc", "ZombieAppearance", ZombieAppearanceOption.Player,
            "Zombie appearance: Player = the zombie hides its own lower garments and wears the player's " +
            "clothes/cosmetics (synced to all clients, incl. un-modded, via PhotonView ownership); " +
            "Mushroom = pure mushroom-man appearance (un-modded clients will still see it with the " +
            "player's outfit applied, since they run the vanilla CharacterCustomization");

    }


    private static void ConfigureOptionalCharacterPatches()
    {
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "RPCA_Die", Type.EmptyTypes, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterRpcDiePrefix), "Character.RPCA_Die()");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "RPCA_SetDead", Type.EmptyTypes, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterRpcSetDeadPrefix), "Character.RPCA_SetDead()");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "RPCA_PassOut", Type.EmptyTypes, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterRpcPassOutPrefix), "Character.RPCA_PassOut()");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "DieInstantly", Type.EmptyTypes, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterDieInstantlyPrefix), "Character.DieInstantly()");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "HandleDeath", Type.EmptyTypes, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterHandleDeathPrefix), "Character.HandleDeath()");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "WarpPlayer", new[] { typeof(Vector3), typeof(bool) }, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterWarpPlayerPrefix), "Character.WarpPlayer(Vector3,bool)");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "Fall", new[] { typeof(float), typeof(float) }, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterFallPrefix), "Character.Fall(float,float)");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(Character), "RPCA_Fall", new[] { typeof(float), typeof(float) }, typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.CharacterRpcFallPrefix), "Character.RPCA_Fall(float,float)");
    }

    /// <summary>
    /// Patches the end-game airport-load path so the zombie form is always reverted BEFORE the
    /// Airport scene loads. PEAK's win flow (peak -> end screen -> close -> load Airport) does not
    /// call Character.RPCEndGame before the scene swap, so the RPCEndGame hook alone would fire too
    /// late (or never) and Character.localCharacter would still point at our networked zombie when
    /// the Airport scene initializes — breaking the player's ability to board the plane. These
    /// hooks (GameOverHandler.BeginAirportLoadRPC, which runs on every client right when the
    /// airport load starts, and EndScreen.ReturnToAirport, the direct scene-load call) are the two
    /// entry points that do run. Registered as optional patches: a missing method is skipped
    /// gracefully. SceneManager.sceneLoaded in Initialize is the final safety net.
    /// </summary>
    private static void ConfigureOptionalEndgamePatches()
    {
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(GameOverHandler), "BeginAirportLoadRPC", Type.EmptyTypes,
            typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.GameOverHandlerBeginAirportLoadRPCPrefix), "GameOverHandler.BeginAirportLoadRPC()");
        PatchUtility.TryPatchOptionalMethod(_harmony, Log, "I'm a Zombie", typeof(EndScreen), "ReturnToAirport", Type.EmptyTypes,
            typeof(ZombieHarmonyPatches), nameof(ZombieHarmonyPatches.EndScreenReturnToAirportPrefix), "EndScreen.ReturnToAirport()");
    }

    /// <summary>
    /// Final safety net for scene switches while transformed (most importantly the ending's Airport
    /// load). Runs when the new scene is already active — the old zombie/body are destroyed, so
    /// ExitZombie skips the position restore and (thanks to the RestoreLocalCharacter guard) never
    /// clobbers the localCharacter the new scene assigns.
    /// </summary>
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            if (_controller == null || !_controller.Active) return;
            Log?.LogInfo("[I'm a Zombie] Scene switched to '" + scene.name + "' while transformed; force-exiting so the new scene gets a clean player.");
            ForceExit();
        }
        catch (Exception ex) { Log?.LogWarning("[I'm a Zombie] OnSceneLoaded: " + ex.Message); }
    }

    /// <summary>Per-frame module maintenance, driven by the unified Transform plugin.</summary>
    internal static void Tick()
    {
        try
        {
            TryInstallStatusGuards();
            if (_controller != null && _controller.Active && !_controller.IsValid()) ForceExit();
        }
        catch (Exception ex) { Log?.LogError("[I'm a Zombie] Module.Tick: " + ex); }
    }

    /// <summary>
    /// Lazily installs the status-bar compatibility guard (PeakStats / PeakStamina / PlayersInfo).
    /// Their assemblies may load after ours, so we retry every couple of seconds until the guard
    /// finds at least one of them (the install itself is idempotent).
    /// </summary>
    private static void TryInstallStatusGuards()
    {
        if (_statusGuardsGaveUp || StatusBarGuard.IsInstalled) return;
        if (Time.unscaledTime < _nextStatusGuardRetry) return;
        _nextStatusGuardRetry = Time.unscaledTime + 2f;
        StatusBarGuard.Install(_harmony);
        // All BepInEx plugins load at startup; if no status-bar mod types are resolvable after
        // ~30s of retries none is installed — stop retrying instead of logging forever.
        if (!StatusBarGuard.IsInstalled && ++_statusGuardRetryAttempts > 15)
        {
            _statusGuardsGaveUp = true;
        }
    }

    internal static void Shutdown()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEvent;
        try { ForceExit(); } catch (Exception ex) { Log?.LogWarning("[I'm a Zombie] Exit cleanup failed: " + ex.Message); }
        try { _harmony?.UnpatchSelf(); } catch (Exception ex) { Log?.LogWarning("[I'm a Zombie] Harmony unpatch failed: " + ex.Message); }
    }

    // ---------------------------------------------------------------
    // Network body visibility — sends a reliable event to all other
    // clients so they hide/show the transforming player's original
    // body at the same time as the owner.
    // ---------------------------------------------------------------

    private static void OnPhotonEvent(EventData photonEvent)
    {
        if (photonEvent.Code != BodyVisibilityEventCode) return;
        try
        {
            // Only handle events tagged by THIS mod. PEAK (and other mods) also
            // raise events on code 199; without a tag we'd mis-cast their payload
            // and spam "Specified cast is not valid" (seen during endgame / Bot warp).
            if (photonEvent.CustomData is not object[] data || data.Length < 3) return;
            if (data[0] is not int tag || tag != BodyVisibilityMagic) return;
            int viewId = (int)data[1];
            bool hide = data[2] is bool b && b;
            PhotonView view = PhotonView.Find(viewId);
            if (view == null) return;
            SetRenderersVisible(view.gameObject, !hide);
        }
        catch (Exception ex) { Log?.LogWarning("[I'm a Zombie] Body visibility event failed: " + ex.Message); }
    }

    internal static void SendBodyVisibility(int viewId, bool hide)
    {
        try
        {
            if (!PhotonNetwork.IsConnected) return;
            PhotonNetwork.RaiseEvent(BodyVisibilityEventCode, new object[] { BodyVisibilityMagic, viewId, hide },
                new RaiseEventOptions { Receivers = ReceiverGroup.Others },
                new SendOptions { Reliability = true });
        }
        catch (Exception ex) { Log?.LogWarning("[I'm a Zombie] Failed to send body visibility event: " + ex.Message); }
    }

    internal static void SetRenderersVisible(GameObject root, bool visible)
    {
        if (root == null) return;
        Renderer[] renderers = GetCachedRenderers(root);
        foreach (Renderer r in renderers)
        {
            if (r == null) continue;
            r.enabled = visible;
            r.forceRenderingOff = !visible;
        }
    }

    private static Renderer[] GetCachedRenderers(GameObject root)
    {
        int id = root.GetInstanceID();
        if (!RendererCache.TryGetValue(id, out Renderer[] renderers) || renderers == null)
        {
            renderers = root.GetComponentsInChildren<Renderer>(true);
            RendererCache[id] = renderers;
        }
        return renderers;
    }

    /// <summary>State gate shared with the unified menu: may the local player enter zombie form now?</summary>
    internal static bool CanEnter(Character character)
    {
        if (IsActive) return false;
        return CanTransform(character);
    }

    private static bool CanTransform(Character character)
    {
        // Another transform form (Scoutmaster / Ghost / Tornado…) is active — they stash the same
        // source character and swap Character.localCharacter, so running both at once corrupts each
        // other. The unified orchestrator exits the active form first, but keep this as a guard.
        if (ZombieController.IsOtherTransformModActive())
        {
            Log?.LogWarning("[I'm a Zombie] Another transform form is active — exit it first.");
            return false;
        }
        return FormValidation.IsValid(Log, "I'm a Zombie", FormValidation.ValidateTransformable(character, checkSpecialForm: false));
    }

    /// <summary>Enters zombie form. Returns true when the controller accepted the request.</summary>
    internal static bool Enter(Character character)
    {
        if (_switching) return false;
        if (!CanTransform(character)) return false;
        _switching = true;
        try
        {
            _controller = character.gameObject.GetComponent<ZombieController>();
            if (_controller == null) _controller = character.gameObject.AddComponent<ZombieController>();
            _controller.EnterZombie(character);
            return _controller.Active;
        }
        catch (Exception ex) { Log?.LogError("[I'm a Zombie] Failed to enter zombie form:\n" + ex); _controller = null; return false; }
        finally { _switching = false; }
    }

    /// <summary>Enters zombie form with an explicit appearance (player-styled or pure mushroom),
    /// overriding the ZombieAppearance config for this one transform. Called by the menu's two
    /// zombie cards.</summary>
    internal static bool Enter(Character character, ZombieAppearanceOption appearance)
    {
        PendingAppearance = appearance;
        try
        {
            return Enter(character);
        }
        finally
        {
            PendingAppearance = null;
        }
    }

    /// <summary>Appearance of the active zombie form, or null while not zombified — lets the
    /// menu highlight the right zombie card.</summary>
    internal static ZombieAppearanceOption? ActiveAppearance
        => _controller != null && _controller.Active ? _controller.CurrentAppearance : null;

    internal static void Exit()
    {
        _switching = true;
        try { _controller?.ExitZombie(); _controller = null; }
        catch (Exception ex) { Log?.LogError("[I'm a Zombie] Failed to exit zombie form:\n" + ex); }
        finally { _switching = false; }
    }

    internal static void ForceExit()
    {
        if (_controller == null) return;
        try { _controller.ExitZombie(); } catch (Exception ex) { Log?.LogWarning("[I'm a Zombie] ForceExit: " + ex.Message); }
        _controller = null;
    }
}
