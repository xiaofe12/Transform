using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Peak;
using Photon.Pun;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ImZombie;

// Run before CharacterMovement (default order 0) so our input is sampled the same frame.
[DefaultExecutionOrder(-100)]
public sealed class ZombieController : MonoBehaviour
{
    // --- Third-person camera constants (match the reference "I'm Zombie" mod) ---
    private const float ThirdPersonCameraFollowSharpness = 18f;   // look-target smoothing
    private const float ThirdPersonCameraPositionSharpness = 16f; // position smoothing
    private const float ThirdPersonCameraRotationSharpness = 22f; // rotation smoothing
    private const float ThirdPersonCameraSnapDistance = 8f;       // teleport camera if movement exceeds this
    private const float ThirdPersonAttackHeightOffset = 0.45f;    // look-target lowered during lunge
    private const float DefaultCameraDistance = 2.8f;
    private const float DefaultCameraHeight = 0.85f;
    private const float DefaultCameraFov = 80f;

    // --- Lunge constants ---
    private const float AngularVelocityDamping = 0.92f;
    private const float MaxAngularVelocity = 10f;

    // --- Climb constants (mirror the reference "I'm Zombie" mod's climbing) ---
    // Zombie climb key is configurable (ZombiePlugin.ClimbKey, default Mouse0). The game's
    // unified Use action (CharacterInput.action_usePrimary) also triggers climbing on a
    // controller, mirroring the Scoutmaster form.
    private const float ClimbRagdollControl = 0.95f;
    private const float ClimbMaxLinearVelocity = 9f;
    private const float ClimbMaxAngularVelocity = 8f;
    private const float ClimbSurfaceProbeDistance = 2.25f;
    private const float ClimbStartRayDistance = 1.65f;
    private const float ClimbAttemptCooldownSeconds = 0.08f;
    private const float ClimbReleaseFallWindowSeconds = 0.7f;
    private const float ClimbReleaseFallSeconds = 1.25f;         // manual-fall window after release
    private const float ClimbRepeatedHitDistance = 0.28f;
    private const float ClimbRepeatedNormalDot = 0.94f;

    // Cached reflection for the vanilla CharacterClimbing internals (resolved once at load).
    private static readonly MethodInfo ClimbStartClimbRpcMethod = FindClimbStartClimbRpcMethod();
    private static readonly FieldInfo ClimbClimbToggledOnField = typeof(CharacterClimbing).GetField("climbToggledOn", ClimbReflectFlags);
    private static readonly FieldInfo ClimbSinceLastClimbStartedField = typeof(CharacterClimbing).GetField("sinceLastClimbStarted", ClimbReflectFlags);
    private static readonly FieldInfo ClimbPlayerSlideField = typeof(CharacterClimbing).GetField("playerSlide", ClimbReflectFlags);
    private static readonly MethodInfo ClimbCanClimbMethod = typeof(CharacterClimbing).GetMethod("CanClimb", ClimbReflectFlags, null, Type.EmptyTypes, null);
    private const System.Reflection.BindingFlags ClimbReflectFlags =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    private static readonly MethodInfo MushroomFadeInRenderersMethod =
        typeof(MushroomZombie).GetMethod("FadeInRenderers", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo MushroomSetZombieEyesMethod =
        typeof(MushroomZombie).GetMethod("SetZombieEyes", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo MushroomClearMushroomVisualsMethod =
        AccessTools.Method(typeof(MushroomZombie), "ClearMushroomVisuals");
    private static readonly MethodInfo CustomizationOnPlayerDataChangeMethod =
        AccessTools.Method(typeof(CharacterCustomization), "OnPlayerDataChange");
    // Climb runtime state
    private float _nextClimbAttemptTime;
    private float _climbReleaseFallUntil;
    private bool _hasLastClimbStart;
    private Vector3 _lastClimbStartPoint;
    private Vector3 _lastClimbStartNormal = Vector3.forward;

    private static MethodInfo FindClimbStartClimbRpcMethod()
    {
        MethodInfo fallback = null;
        foreach (MethodInfo method in typeof(CharacterClimbing).GetMethods(ClimbReflectFlags))
        {
            if (method == null || method.Name != "StartClimbRpc") continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length < 2) continue;
            if (parameters[0].ParameterType != typeof(Vector3) || parameters[1].ParameterType != typeof(Vector3)) continue;
            if (parameters.Length == 2) return method;
            fallback ??= method;
        }
        return fallback;
    }

    private static object[] BuildClimbRpcArguments(MethodInfo method, Vector3 climbPos, Vector3 climbNormal)
    {
        if (method == null) return null;
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length < 2
            || parameters[0].ParameterType != typeof(Vector3)
            || parameters[1].ParameterType != typeof(Vector3))
            return null;
        object[] args = new object[parameters.Length];
        args[0] = climbPos;
        args[1] = climbNormal;
        for (int i = 2; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
        }
        return args;
    }

    /// <summary>Default depth (metres) below the player's feet to stash the parked body while
    /// transformed. A shallow depth keeps it inside the normal coordinate range (so 32-bit float
    /// precision stays intact) while hiding it below the terrain from un-modded clients. Lighting
    /// stays correct because we swap Character.localCharacter to the zombie (which stays on the
    /// ground) — the parked body's own depth no longer drives the fog/lighting.</summary>
    private const float DefaultHiddenBodyDepth = 30f;

    private Character _zombieCharacter;
    private Character _playerCharacter;
    private Vector3 _prevCenter;

    // Camera smoothing state (same scheme as the reference mod)
    private bool _hasSmoothedCameraPose;
    private Vector3 _smoothedCameraPosition;
    private Quaternion _smoothedCameraRotation = Quaternion.identity;
    private bool _hasSmoothedCameraTarget;
    private Vector3 _smoothedCameraTarget;

    // Exit camera blend. The vanilla MainCameraMovement clamps the camera to within 0.1 m of the
    // head (characterPovMaxDistance) the instant it is re-enabled, so the revert is otherwise a
    // single-frame hard cut from the third-person zombie pose back into first person.
    // ExitCameraBlend (added to the camera on exit, execution order 600 — after MainCameraMovement's
    // 500) eases that handover instead.
    private ExitCameraBlend _exitCameraBlend;
    private const float ExitCameraBlendSeconds = 0.55f;
    private const float NetworkZombiePoolDelaySeconds = 1.25f;
    private const float ZombiePoolDepth = 80f;

    // Input edge detection
    private bool _lastJumpPressed;
    private bool _lastAttackPressed;
    private bool _jumpQueued;

    // Attack state
    private bool _attacking;
    private float _attackStartedAt;
    private float _lastAttackTime;

    private GameObject _zombieRoot;
    private MushroomZombie _mushroomZombie;
    private PhotonView _zombiePhotonView;
    private bool _zombieIsNetworked;
    private bool _playerBodyHidden;
    private float _mushroomManVisualGuardUntil;

    // Revert work is split across frames so ragdolls, HUD and pooled zombies do not all update at once.
    private Coroutine _deferredExitRestore;
    private GameObject _pendingDestroyRoot;
    private bool _pendingDestroyNetworked;
    private ZombiePlugin.ZombieAppearanceOption _pendingDestroyAppearance;

    /// <summary>
    /// Revert frame used by the RPC_PlaySFX prefix to silence stale grunts during teardown.
    /// </summary>
    internal static int ExitSfxSuppressedFrame = int.MinValue;

    // Hidden-body pinning: without this the kinematic parked body can be flung to extreme
    // coordinates (the log shows it reaching ±128,000 m), which destroys 32-bit float precision
    // and the shadow-cascade / fog / lighting work that references the player transform.
    private Vector3 _hiddenBodyPos;
    private bool _hiddenBodyPosSet;

    // Player body state
    private readonly List<MonoBehaviour> _disabledCameraScripts = new List<MonoBehaviour>();
    private readonly List<bool> _cameraScriptStates = new List<bool>();    private CharacterMovement _disabledPlayerMovement;

    // Saved ragdoll physics state so we can freeze the parked body and restore it exactly on exit.
    private readonly List<Rigidbody> _frozenBodyparts = new List<Rigidbody>();
    private readonly List<bool> _frozenKinematic = new List<bool>();
    private readonly List<bool> _frozenDetectCollisions = new List<bool>();
    private readonly List<bool> _frozenUseGravity = new List<bool>();

    // Player's own light sources (hidden together with the body, restored on exit). The character
    // carries a small point light; leaving it on while the body is parked underground leaks light.
    private readonly List<Light> _playerLights = new List<Light>();
    private readonly List<bool> _playerLightStates = new List<bool>();

    // Periodic re-broadcast of the "hide body" Photon event (199) while transformed. Reliable
    // RaiseEvents are NOT replayed to clients that join mid-transform, so a periodic re-send
    // covers late joiners on modded clients. Un-modded clients are covered by the continuously
    // running position sync: the body is pinned underground and keeps being broadcast at ~20 Hz,
    // so even a client that joins late (PUN hands it the spawn-time buffered position first)
    // pulls the body underground within one or two sync ticks.
    private float _nextBodyVisibilityRebroadcast;
    private const float BodyVisibilityRebroadcastSeconds = 5f;
    private float _nextZombieStateRebroadcast;
    private const float ZombieStateRebroadcastSeconds = 2f;

    // Whether we swapped Character.localCharacter to the zombie (so the game's lighting / fog /
    // shadow cascade follow the zombie on the ground instead of the parked body underground).
    private bool _localCharacterSwapped;

    // The object we pointed Character.localCharacter at (the zombie). Saved separately from
    // _zombieCharacter because DestroyZombie() nulls that field BEFORE RestoreLocalCharacter()
    // runs — comparing against the nulled field would make the restore guard always fail and leave
    // Character.localCharacter pointing at the (soon destroyed) zombie, breaking the next transform
    // ("No local character" when pressing the toggle key again).
    private Character _localCharacterSwapTarget;

    internal const string NetworkVisualMarker = "ImZombie.Visual";
    private static GameObject _cachedPlayerZombiePrefab;
    private static GameObject _cachedMushroomZombiePrefab;
    private static GameObject _pooledPlayerZombieRoot;
    private static GameObject _pooledMushroomZombieRoot;
    private static GameObject _pooledMushroomManZombieRoot;
    private static RuntimeAnimatorController _cachedNpcZombieAnimator;
    private float _nextPlayerOutfitRefreshTime;
    private const float PlayerOutfitRefreshIntervalSeconds = 1.5f;
    private readonly List<Renderer> _playerRenderers = new List<Renderer>();
    private readonly List<bool> _playerRendererStates = new List<bool>();

    // Appearance style chosen at enter time (Player wears the player's clothes, Mushroom stays a
    // pure mushroom-man). Broadcast to every client through the Photon instantiation data.
    private ZombiePlugin.ZombieAppearanceOption _zombieAppearance = ZombiePlugin.ZombieAppearanceOption.Player;

    public static Character ActiveZombieCharacter { get; private set; }

    /// <summary>The original player body parked while transformed (null when not in zombie form).
    /// Used by the PeakStats compatibility guard to skip third-party stamina-bar UI for it.</summary>
    internal static Character ParkedPlayerCharacter { get; private set; }

    public bool Active { get; private set; }

    public bool IsValid()
    {        if (!Active || _zombieCharacter == null || _zombieCharacter.data == null) return false;
        return !_zombieCharacter.data.dead && !_zombieCharacter.data.fullyPassedOut;
    }

    /// <summary>
    /// Detects whether another "transform / possession" mod (I'm Scoutmaster, I'm a Ghost, I'm a
    /// Tornado, …) currently has a controlled transform active. Those mods stash the same source
    /// character and swap Character.localCharacter, so running two at once corrupts each other.
    /// We refuse to transform while one is active (and defend our own swap while transformed).
    /// </summary>
    internal static bool IsOtherTransformModActive()
    {
        try
        {
            foreach (Character c in Character.AllCharacters)
            {
                if (c == null || c.data == null) continue;
                if (c.data.isScoutmaster) return true;
                string name = c.name ?? string.Empty;
                if (name.IndexOf("Scoutmaster", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Tornado", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch (Exception ex) { LogError(nameof(IsOtherTransformModActive), ex); }
        return false;
    }

    /// <summary>
    /// Called from the vanilla Character.RPCEndGame / GameOverHandler.BeginAirportLoadRPC /
    /// EndScreen.ReturnToAirport prefixes BEFORE the end-game iterates characters or the Airport
    /// scene loads. If we are transformed, revert to the player first so the win/lose stats and
    /// badge unlocking apply to the real source character (which has valid badgeStatus/timelineInfo)
    /// instead of the controlled zombie (whose empty badgeStatus / never-recorded timelineInfo would
    /// crash EndCutscene / Win). ExitZombie parks the controlled zombie in the local pool so the
    /// restore frame does not tear down the whole object tree.
    /// </summary>
    internal static void ForceExitForEndGame()
    {
        try
        {
            // NOTE: the ZombieController component is added to the PLAYER's GameObject
            // (EnterZombieRoutine in ZombiePlugin), NOT to the zombie. ActiveZombieCharacter and
            // Character.localCharacter both point at the ZOMBIE while transformed, so
            // GetComponent<ZombieController>() on them returns null and the old code silently did
            // nothing — the player stayed a zombie through the end screen and into the Airport
            // scene, where localCharacter pointed at a destroyed zombie and boarding the plane
            // failed. Look the controller up on the parked player body instead.
            if (ParkedPlayerCharacter != null)
            {
                ZombieController ctrl = ParkedPlayerCharacter.GetComponent<ZombieController>();
                if (ctrl != null && ctrl.Active) { ctrl.ExitZombie(); return; }
            }
            // Fallback: any active controller in the scene (covers a mangled ParkedPlayerCharacter).
            foreach (ZombieController ctrl in UnityEngine.Object.FindObjectsByType<ZombieController>(FindObjectsSortMode.None))
            {
                if (ctrl != null && ctrl.Active) { ctrl.ExitZombie(); return; }
            }
        }
        catch (Exception ex) { LogError("ForceExitForEndGame", ex); }
    }

    private static void LogInfo(string m) { ZombiePlugin.Log.LogInfo("[ImZombie] " + m); }
    private static void LogError(string c, Exception ex) { ZombiePlugin.Log.LogError("[ImZombie] " + c + ": " + ex); }

    // ===================================================================
    // Enter / Exit
    // ===================================================================

    public void EnterZombie(Character playerCharacter)
    {
        if (Active) { LogInfo("Already in zombie form."); return; }
        // If the previous exit's deferred restore is still pending, finish it NOW — otherwise
        // HidePlayerBody/HideHud below would capture the still-hidden states as the "original"
        // ones and the body/HUD would stay hidden after the NEXT exit.
        FlushDeferredExitRestore();
        // Duplicate guard: sweep rejected leftovers, but keep the two intentional cached roots.
        CleanupStaleZombies();
        // A still-running exit blend (fast revert → re-transform) would keep overriding the zombie
        // camera from its later execution order — kill it before the new transform starts.
        StopExitCameraBlend();
        _playerCharacter = playerCharacter;
        FreezePlayerPhysics();
        HidePlayerBody();
        SendNetworkBodyVisibility(true);
        GameObject zombieObj = SpawnZombiePrefab(playerCharacter);
        if (zombieObj == null) { LogInfo("Failed to spawn zombie prefab."); AbortEnterZombie(); return; }

        _zombieRoot = zombieObj;
        _zombieCharacter = zombieObj.GetComponent<Character>();
        _mushroomZombie = zombieObj.GetComponent<MushroomZombie>();
        _zombiePhotonView = zombieObj.GetComponent<PhotonView>();
        SetZombieAudioMuted(_zombieRoot, false);

        if (_zombieCharacter == null) { LogInfo("No Character component on zombie! Aborting."); DestroyZombie(true); AbortEnterZombie(); return; }
        // Keep the just-spawned local replica hidden while we apply animator/outfit/pose. This
        // avoids a one-frame render spike and prevents half-configured player zombies from flashing.
        SetLocalZombieRendering(false);

        // Keep the vanilla component enabled: the Harmony prefix on MushroomZombie.Update/FixedUpdate
        // suppresses the AI for our zombie while still running the visual update (mouth, mushroom
        // growth, bite collider) on every frame. Disabling the component would skip the patch entirely.
        SetupZombieVisuals();
        ClearPlayerInput();
        InitZombieState();
        SetLocalZombieRendering(true);

        _attacking = false; _attackStartedAt = 0f; _lastAttackTime = -10f;
        _lastJumpPressed = false; _lastAttackPressed = false;
        ResetCameraSmoothing();
        Active = true;
        ActiveZombieCharacter = _zombieCharacter;
        ParkedPlayerCharacter = _playerCharacter;
        enabled = true;
        // Make the game's lighting / fog / shadow cascade follow the zombie (which stays on the
        // ground) instead of the parked body underground. This is what keeps the scene lighting
        // correct while the body is stashed below the terrain.
        try { Character.localCharacter = _zombieCharacter; _localCharacterSwapTarget = _zombieCharacter; _localCharacterSwapped = true; }
        catch (Exception ex) { LogError("EnterZombie localCharacter swap", ex); }
        DisableOriginalCameraControl();
        HideHud();
        WarpPlayerToHiddenPosition();
        StartCoroutine(ReassertOutfitAfterEnter());
        StartCoroutine(PushIdleStateAfterEnter());
        _nextPlayerOutfitRefreshTime = Time.unscaledTime + PlayerOutfitRefreshIntervalSeconds;
        _nextBodyVisibilityRebroadcast = Time.unscaledTime + BodyVisibilityRebroadcastSeconds;
        _nextZombieStateRebroadcast = Time.unscaledTime + ZombieStateRebroadcastSeconds;
        LogInfo("Entered zombie form.");
    }

    /// <summary>
    /// The un-modded / remote client's vanilla MushroomZombie.Start() runs one frame after the Photon
    /// instantiate and calls StartSleeping() (because isNPCZombie is still true there), which puts the
    /// zombie in the fallen "sleeping" pose. Re-push the current (Idle) state a few times afterwards
    /// via the vanilla RPC_SyncState RPC so other players see the zombie stand up instead of sleeping.
    /// </summary>
    private IEnumerator PushIdleStateAfterEnter()
    {
        for (int i = 0; i < 4; i++)
        {
            yield return new WaitForSeconds(0.2f);
            if (!Active) yield break;
            PushZombieStateToRemote();
        }
    }

    private void PushZombieStateToRemote()
    {
        if (_zombiePhotonView == null || !_zombiePhotonView.IsMine || _zombieCharacter?.data == null) return;
        try
        {
            MushroomZombie.State state = _mushroomZombie != null ? _mushroomZombie.currentState : MushroomZombie.State.Idle;
            _zombiePhotonView.RPC("RPC_SyncState", RpcTarget.Others,
                (int)state, _zombieCharacter.data.isSprinting, _zombieCharacter.data.fallSeconds, _zombieCharacter.data.passedOut);
        }
        catch (Exception ex) { LogError("PushZombieStateToRemote", ex); }
    }

    /// <summary>
    /// Late re-assertion of player-zombie outfit AFTER vanilla AwakeRoutine / Coinflip noise.
    /// Mushroom zombies are intentionally static and never mirror player cosmetics; player zombies
    /// re-send only the small lower-garment RPC so room clients converge after the local setup is done.
    /// </summary>
    private IEnumerator ReassertOutfitAfterEnter()
    {
        yield return new WaitForSeconds(1.2f);
        if (!Active || _mushroomZombie == null || _playerCharacter == null) yield break;
        if (_zombieAppearance != ZombiePlugin.ZombieAppearanceOption.Player) yield break;
        SyncPlayerZombieOutfitToRoom();
    }

    /// <summary>Whether the transforming player is currently wearing the skirt (vs shorts),
    /// used only for Player Zombie lower-garment mirroring.</summary>
    private bool GetPlayerWearingSkirt()
    {
        CustomizationRefs pRefs = _playerCharacter?.refs?.customization?.refs;
        if (pRefs != null && pRefs.skirt != null && pRefs.shorts != null)
            return pRefs.skirt.gameObject.activeSelf && !pRefs.shorts.gameObject.activeSelf;
        return false;
    }
    private void RefreshPlayerZombieOutfitLocal()
    {
        if (_zombieAppearance != ZombiePlugin.ZombieAppearanceOption.Player) return;
        if (_mushroomZombie == null || _playerCharacter == null) return;
        try
        {
            if (_zombieCharacter?.refs?.customization != null)
            {
                _zombieCharacter.refs.customization.overridePhotonPlayer = _playerCharacter.photonView?.Owner;
                _zombieCharacter.refs.customization.ignorePlayerCosmetics = false;
                if (_zombieCharacter.refs.customization.refs != null)
                    _zombieCharacter.refs.customization.refs.SetMushroomMan(false);
            }
            HideMushroomParts();
            ApplyPlayerFitLowerGarments(_mushroomZombie);
            ReassertPlayerAccessory();
        }
        catch (Exception ex) { LogError("RefreshPlayerZombieOutfitLocal", ex); }
    }

    private void SyncPlayerZombieOutfitToRoom()
    {
        if (_zombieAppearance != ZombiePlugin.ZombieAppearanceOption.Player) return;
        RefreshPlayerZombieOutfitLocal();
        if (_zombiePhotonView == null || !_zombiePhotonView.IsMine) return;
        try
        {
            _zombiePhotonView.RPC("RPC_SetOutfit", Photon.Pun.RpcTarget.All, GetPlayerWearingSkirt());
        }
        catch (Exception ex) { LogError("SyncPlayerZombieOutfitToRoom", ex); }
    }

    /// <summary>Restores Character.localCharacter back to the original player (undoing the swap done
    /// on enter). The game's lighting / fog / camera follow Character.localCharacter.</summary>
    private void RestoreLocalCharacter()
    {
        if (!_localCharacterSwapped) return;
        _localCharacterSwapped = false;
        try
        {
            // Only hand control back if the original player is still alive AND localCharacter is
            // STILL pointing at the object we swapped to (the zombie). Compare against the saved
            // _localCharacterSwapTarget, NOT _zombieCharacter — DestroyZombie() runs before this
            // and nulls that field, which would make the comparison fail and leave
            // Character.localCharacter on the destroyed zombie ("can only transform once"). After a
            // scene switch (e.g. the ending loads the Airport scene) the game re-assigns
            // Character.localCharacter itself — if it already did, don't clobber it.
            if (_playerCharacter != null && Character.localCharacter == _localCharacterSwapTarget)
                Character.localCharacter = _playerCharacter;
            _localCharacterSwapTarget = null;
        }
        catch (Exception ex) { LogError("RestoreLocalCharacter", ex); }
    }

    /// <summary>Rolls back all EnterZombie side effects when zombie spawn/setup fails.</summary>
    private void AbortEnterZombie()
    {
        RestoreLocalCharacter();
        RestorePlayerPhysics();
        RestorePlayerBody();
        SendNetworkBodyVisibility(false);
        RestoreOriginalCameraControl();
        RestoreHud();
        _playerCharacter = null;
        _playerBodyHidden = false;
        _hiddenBodyPosSet = false;
        ParkedPlayerCharacter = null;
    }

    /// <summary>Reverts to the player. The player's statuses are preserved on revert (only the
    /// zombie-form residue is cleared — see ResetPlayerState); stamina stays shared with the
    /// zombie. A passed-out player cannot transform (handled by CanTransform).</summary>
    public void ExitZombie()
    {
        if (!Active && _zombieRoot == null) return;
        // Suppress zombie SFX that race the revert on this frame (see ExitSfxSuppressedFrame).
        ExitSfxSuppressedFrame = Time.frameCount;
        // Remember where the zombie stands so the player resumes control there.
        Vector3 restorePos = Vector3.zero;
        Vector2 restoreLook = Vector2.zero;
        bool restore = false;
        if (_zombieCharacter != null && _zombieCharacter.data != null)
        {
            restorePos = _zombieCharacter.Center + Vector3.up * 0.25f;
            restoreLook = _zombieCharacter.data.lookValues;
            restore = true;
        }
        Active = false; ActiveZombieCharacter = null; enabled = false;
        _hiddenBodyPosSet = false;
        _attacking = false; _attackStartedAt = 0f;
        // Park the zombie; pool cleanup runs after the player is back in control.
        DeactivateZombieForDeferredDestroy();
        // Move back while the body is still kinematic, then restore movement.
        if (restore && _playerCharacter != null)
        {
            _playerCharacter.data.lookValues = restoreLook;
            RecalculateLookDirections(_playerCharacter);
            ResetPlayerState(_playerCharacter, restorePos);
            // Snap the root and ragdoll parts immediately; the normal sync path settles remotes.
            SetCharacterPositionImmediate(_playerCharacter, restorePos, _playerCharacter.transform.rotation);
        }
        // Physics wake-up is deferred to keep the revert frame light.
        RestoreLocalCharacter();
        SendNetworkBodyVisibility(false);
        // Blend back to the vanilla first-person camera.
        StartExitCameraBlend();
        ParkedPlayerCharacter = null;
        // Presentation restore and pooling continue on later frames.
        if (_deferredExitRestore != null) { try { StopCoroutine(_deferredExitRestore); } catch { } }
        _deferredExitRestore = StartCoroutine(DeferredExitRestoreRoutine());
        LogInfo("Exited zombie form.");
    }

    /// <summary>Restores camera, HUD, body and physics after the player has control again.</summary>
    private IEnumerator DeferredExitRestoreRoutine()
    {
        yield return null;

        // Let the blend run for one frame before vanilla camera scripts resume.
        RestoreOriginalCameraControl();
        if (_pendingDestroyRoot != null)
        {
            PrepareZombieForPool(_pendingDestroyRoot);
        }

        yield return null;
        RestoreHud();

        yield return null;
        RestorePlayerBody();

        yield return null;
        RestorePlayerPhysics();

        if (_pendingDestroyRoot != null)
        {
            yield return new WaitForSeconds(NetworkZombiePoolDelaySeconds);
            PoolDeactivatedZombie();
        }
        _deferredExitRestore = null;
    }

    /// <summary>Completes any pending restore work before a new transform or teardown.</summary>
    internal void FlushDeferredExitRestore()
    {
        if (_deferredExitRestore != null)
        {
            try { StopCoroutine(_deferredExitRestore); } catch { }
            _deferredExitRestore = null;
        }
        if (_pendingDestroyRoot != null)
        {
            PrepareZombieForPool(_pendingDestroyRoot);
        }
        PoolDeactivatedZombie();
        RestorePlayerPhysics();
        RestorePlayerBody();
        RestoreHud();
    }

    private void OnDestroy()
    {
        // Finish any pending deferred exit work (pending zombie pool / body / HUD restore)
        // before teardown — covers a shutdown one frame after a revert.
        FlushDeferredExitRestore();
        StopExitCameraBlend();
        // Even when not Active anymore, a stale cached root must not leak (duplicate guard).
        if (_zombieRoot != null)
        {
            ForceDestroyZombieRoot(_zombieRoot, _zombieIsNetworked);
            _zombieRoot = null; _zombieCharacter = null; _mushroomZombie = null;
            _zombiePhotonView = null; _zombieIsNetworked = false;
        }
        if (Active)
        {
            Active = false; ActiveZombieCharacter = null;
            _hiddenBodyPosSet = false;
            DestroyZombie(true);
            RestoreOriginalCameraControl();
            RestorePlayerPhysics();
            RestorePlayerBody();
            RestoreLocalCharacter();
            SendNetworkBodyVisibility(false);
            RestoreHud();
            ParkedPlayerCharacter = null;
        }
    }

    private void InitZombieState()
    {
        try
        {
            // Ensure the zombie is fully under animation/ragdoll control (not ragdolled)
            _zombieCharacter.data.currentRagdollControll = 1f;
            _zombieCharacter.data.dead = false;
            _zombieCharacter.data.passedOut = false;
            _zombieCharacter.data.fullyPassedOut = false;
            _zombieCharacter.data.fallSeconds = 0f;
            _zombieCharacter.data.zombified = false;
            _zombieCharacter.data.isSprinting = false;
            _zombieCharacter.data.isCrouching = false;
            _zombieCharacter.data.currentStamina = _playerCharacter?.data?.currentStamina ?? 1f;
            _zombieCharacter.data.extraStamina = 0f;

            // Use the player's stamina rates so zombie sprint/jump drain the shared bar normally.
            try
            {
                CharacterMovement zombieMovement = _zombieCharacter.GetComponent<CharacterMovement>();
                CharacterMovement playerMovement = _playerCharacter != null ? _playerCharacter.GetComponent<CharacterMovement>() : null;
                if (zombieMovement != null && playerMovement != null)
                {
                    zombieMovement.sprintStaminaUsage = playerMovement.sprintStaminaUsage;
                    zombieMovement.jumpStaminaUsage = playerMovement.jumpStaminaUsage;
                    zombieMovement.jumpStaminaUsageSprinting = playerMovement.jumpStaminaUsageSprinting;
                }
            }
            catch (Exception ex) { LogError("InitZombieState stamina rates", ex); }

            // Ensure Character refs are ready before other mods inspect the local character.
            try
            {
                _zombieCharacter.InitializeRefs();
                if (_zombieCharacter.refs != null && _zombieCharacter.refs.customization == null)
                    _zombieCharacter.refs.customization = _zombieCharacter.GetComponentInChildren<CharacterCustomization>(true);
                _zombieCharacter.isBot = false;
            }
            catch (Exception ex) { LogError("InitZombieState refs", ex); }

            // Sync look values from player so the zombie faces the same direction
            if (_playerCharacter != null && _playerCharacter.data != null)
            {
                _zombieCharacter.data.lookValues = _playerCharacter.data.lookValues;
                RecalculateLookDirections(_zombieCharacter);
            }

            // Wake the rigidbodies and force the zombie to stand upright. The prefab spawns in its
            // dormant/lying pose and its rigidbodies are asleep, so simply setting
            // currentRagdollControll = 1 is not enough — the bodyparts never respond and the zombie
            // looks "asleep". Copy the player's standing pose onto the zombie and wake every
            // bodypart (mirrors the reference mod's AlignZombieBodyToSource + EnableZombiePhysics).
            if (_zombieCharacter.refs != null && _zombieCharacter.refs.ragdoll != null)
            {
                _zombieCharacter.refs.ragdoll.ToggleKinematic(false);
                CopyPlayerPoseToZombie();
                WakeZombieRagdoll();
            }
        }
        catch (Exception ex) { LogError("InitZombieState", ex); }
    }

    /// <summary>Copies the player's current (standing) bodypart pose onto the zombie so it starts
    /// upright instead of in the prefab's lying/dormant pose.</summary>
    private void CopyPlayerPoseToZombie()
    {
        if (_playerCharacter?.refs?.ragdoll?.partList == null || _zombieCharacter?.refs?.ragdoll?.partList == null) return;
        foreach (Bodypart zombiePart in _zombieCharacter.refs.ragdoll.partList)
        {
            if (zombiePart == null || zombiePart.Rig == null) continue;
            Bodypart playerPart = FindBodypart(_playerCharacter, zombiePart.partType);
            if (playerPart == null || playerPart.Rig == null) continue;
            zombiePart.Rig.MovePosition(playerPart.Rig.position);
            zombiePart.Rig.MoveRotation(playerPart.Rig.rotation);
        }
    }

    private static Bodypart FindBodypart(Character character, BodypartType type)
    {
        if (character?.refs?.ragdoll?.partList == null) return null;
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (part != null && part.partType == type) return part;
        }
        return null;
    }

    /// <summary>Wakes every bodypart rigidbody so the ragdoll actually responds to animation
    /// control (Unity puts freshly-spawned, unmoving rigidbodies to sleep).</summary>
    private void WakeZombieRagdoll()
    {
        if (_zombieCharacter?.refs?.ragdoll?.partList == null) return;
        foreach (Bodypart part in _zombieCharacter.refs.ragdoll.partList)
        {
            Rigidbody rig = part?.Rig;
            if (rig == null) continue;
            rig.isKinematic = false;
            rig.detectCollisions = true;
            rig.useGravity = true;
            rig.WakeUp();
        }
    }

    private GameObject TryReusePooledZombie(Character playerCharacter, Vector3 pos, Quaternion rot)
    {
        ref GameObject slot = ref GetZombiePoolSlot(_zombieAppearance);
        GameObject root = slot;
        if (root == null) return null;

        bool networked = IsNetworkedZombieRoot(root);
        PhotonView view = root.GetComponent<PhotonView>();
        if (networked && (!PhotonNetwork.InRoom || view == null || !view.IsMine))
        {
            slot = null;
            ForceDestroyZombieRoot(root, networked);
            return null;
        }

        slot = null;
        try
        {
            if (!root.activeSelf) root.SetActive(true);
            Character zombieCharacter = root.GetComponent<Character>();
            if (zombieCharacter != null)
            {
                SetCharacterPositionImmediate(zombieCharacter, pos, rot);
                if (zombieCharacter.input != null)
                {
                    zombieCharacter.input.movementInput = Vector2.zero;
                    zombieCharacter.input.jumpWasPressed = false;
                    zombieCharacter.input.jumpIsPressed = false;
                    zombieCharacter.input.sprintIsPressed = false;
                    zombieCharacter.input.crouchIsPressed = false;
                    zombieCharacter.input.usePrimaryWasPressed = false;
                    zombieCharacter.input.usePrimaryIsPressed = false;
                    zombieCharacter.input.useSecondaryWasPressed = false;
                    zombieCharacter.input.useSecondaryIsPressed = false;
                }
            }
            else
            {
                root.transform.SetPositionAndRotation(pos, rot);
            }
            ZombiePlugin.SetRenderersVisible(root, false);
            SetZombieAudioMuted(root, false);
            _zombieIsNetworked = networked;
            root.name = networked ? "ImZombieNetworked_PooledActive" : "ImZombieLocal_PooledActive";
            LogInfo("Reused pooled " + _zombieAppearance + " zombie.");
            return root;
        }
        catch (Exception ex)
        {
            LogError("TryReusePooledZombie", ex);
            ForceDestroyZombieRoot(root, networked);
            return null;
        }
    }

    private void PrepareZombieForPool(GameObject root)
    {
        if (root == null) return;
        try
        {
            MushroomZombie zombie = root.GetComponent<MushroomZombie>();
            if (zombie != null)
            {
                ZombieHarmonyPatches.ClearAttackMouth(zombie);
                zombie.currentState = MushroomZombie.State.Idle;
            }
            SetZombieAudioMuted(root, true);

            Character character = root.GetComponent<Character>();
            Vector3 basePos = _playerCharacter != null ? _playerCharacter.Center : root.transform.position;
            Vector3 poolPos = basePos + Vector3.down * (GetHiddenBodyDepth() + ZombiePoolDepth);
            if (character != null)
            {
                if (character.data != null)
                {
                    character.data.dead = false;
                    character.data.passedOut = false;
                    character.data.fullyPassedOut = false;
                    character.data.fallSeconds = 0f;
                    character.data.isSprinting = false;
                    character.data.isCrouching = false;
                    character.data.isJumping = false;
                }
                if (character.input != null)
                {
                    character.input.movementInput = Vector2.zero;
                    character.input.jumpWasPressed = false;
                    character.input.jumpIsPressed = false;
                    character.input.sprintIsPressed = false;
                    character.input.crouchIsPressed = false;
                    character.input.usePrimaryWasPressed = false;
                    character.input.usePrimaryIsPressed = false;
                    character.input.useSecondaryWasPressed = false;
                    character.input.useSecondaryIsPressed = false;
                }
                SetCharacterPositionImmediate(character, poolPos, root.transform.rotation);
                if (character.refs?.ragdoll?.partList != null)
                {
                    foreach (Bodypart part in character.refs.ragdoll.partList)
                    {
                        Rigidbody rig = part?.Rig;
                        if (rig == null) continue;
                        if (!rig.isKinematic)
                        {
                            rig.linearVelocity = Vector3.zero;
                            rig.angularVelocity = Vector3.zero;
                        }
                        rig.detectCollisions = false;
                        rig.useGravity = false;
                        rig.isKinematic = true;
                    }
                }
            }
            else
            {
                root.transform.position = poolPos;
            }

            ZombiePlugin.SetRenderersVisible(root, false);
            if (!IsNetworkedZombieRoot(root)) root.SetActive(false);
        }
        catch (Exception ex) { LogError("PrepareZombieForPool", ex); }
    }

    private static void SetZombieAudioMuted(GameObject root, bool muted)
    {
        if (root == null) return;
        try
        {
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null) continue;
                if (muted && source.isPlaying) source.Stop();
                if (!muted && !source.enabled) source.enabled = true;
                source.mute = muted;
            }
        }
        catch (Exception ex) { LogError(nameof(SetZombieAudioMuted), ex); }
    }

    // ===================================================================
    // Spawn
    // ===================================================================

    /// <summary>
    /// Duplicate guard: removes rejected leftovers before a new transform while preserving the
    /// intentional local zombie pool.
    /// </summary>
    private void CleanupStaleZombies()
    {
        // 1) Stale cached root from a previous (possibly failed) exit.
        if (_zombieRoot != null)
        {
            LogInfo("Clearing stale zombie root before entering (duplicate guard).");
            GameObject stale = _zombieRoot;
            _zombieRoot = null; _zombieCharacter = null; _mushroomZombie = null;
            _zombiePhotonView = null; _zombieIsNetworked = false;
            try
            {
                ForceDestroyZombieRoot(stale, IsNetworkedZombieRoot(stale));
            }
            catch (Exception ex)
            {
                LogError("CleanupStaleZombies root", ex);
                try { Destroy(stale); } catch { }
            }
        }

        // 2) Orphan controlled zombies in the scene owned by this client (network leftovers).
        try
        {
            MushroomZombie[] zombies = UnityObject.FindObjectsByType<MushroomZombie>(FindObjectsSortMode.None);
            foreach (MushroomZombie zombie in zombies)
            {
                if (zombie == null) continue;
                PhotonView view = zombie.GetComponent<PhotonView>();
                if (view == null || !view.IsMine) continue;
                object[] data = view.InstantiationData;
                if (data == null || data.Length == 0 || !(data[0] is string marker) || marker != NetworkVisualMarker) continue;
                if (IsPooledZombieRoot(zombie.gameObject)) continue;
                LogInfo("Destroying orphan controlled zombie (duplicate guard) view=" + view.ViewID);
                try { ForceDestroyZombieRoot(zombie.gameObject, true); }
                catch (Exception ex)
                {
                    LogError("CleanupStaleZombies orphan", ex);
                    try { Destroy(zombie.gameObject); } catch { }
                }
            }
        }
        catch (Exception ex) { LogError("CleanupStaleZombies scan", ex); }
    }

    private GameObject SpawnZombiePrefab(Character playerCharacter)
    {
        Vector3 pos = playerCharacter.Center;
        Quaternion rot = playerCharacter.transform.rotation;
        _zombieAppearance = GetAppearance();
        // Player style: the "player" mushroom-zombie prefab (wears the player's outfit via PhotonView
        // ownership). Mushroom style: the NPC mushroom-zombie prefab — it has no player-customization
        // bindings, so un-modded clients see a pure mushroom-man too (the vanilla
        // MushroomZombieSpawner instantiates it over Photon, so it is in the prefab pool).
        bool playerStyle = _zombieAppearance == ZombiePlugin.ZombieAppearanceOption.Player;
        string prefabName = playerStyle ? "MushroomZombie_Player" : "MushroomZombie";
        GameObject pooled = TryReusePooledZombie(playerCharacter, pos, rot);
        if (pooled != null) return pooled;
        if (PhotonNetwork.InRoom && playerCharacter.photonView != null && playerCharacter.photonView.ViewID > 0)
        {
            try
            {
                GameObject inst = PhotonNetwork.Instantiate(prefabName, pos, rot, 0, new object[] { NetworkVisualMarker, playerCharacter.photonView.ViewID, (int)_zombieAppearance });
                if (inst != null) { inst.name = "ImZombieNetworked"; _zombieIsNetworked = true; LogInfo("Spawned networked zombie (" + prefabName + ")."); return inst; }
            }
            catch (Exception ex) { LogError("PhotonNetwork.Instantiate " + prefabName, ex); }
        }
        try
        {
            GameObject prefab = LoadZombiePrefabCached(prefabName, playerStyle);
            if (prefab == null) { LogInfo("Could not find zombie prefab via cache/resources (" + prefabName + ")."); return null; }
            GameObject inst = UnityObject.Instantiate(prefab, pos, rot);
            inst.name = "ImZombieLocal"; _zombieIsNetworked = false;
            LogInfo("Spawned local zombie prefab (" + prefab.name + ")."); return inst;
        }
        catch (Exception ex) { LogError("Resources.Load zombie prefab", ex); return null; }
    }

    private static GameObject LoadZombiePrefabCached(string prefabName, bool playerStyle)
    {
        ref GameObject cache = ref (playerStyle ? ref _cachedPlayerZombiePrefab : ref _cachedMushroomZombiePrefab);
        if (cache != null) return cache;

        GameObject prefab = null;
        try
        {
            object pool = PhotonNetwork.PrefabPool;
            if (pool != null)
            {
                MethodInfo loadMethod = pool.GetType().GetMethod("LoadPrefab",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadMethod != null)
                    prefab = loadMethod.Invoke(pool, new object[] { prefabName }) as GameObject;
            }
        }
        catch { }

        if (prefab == null) prefab = Resources.Load<GameObject>(prefabName);
        if (prefab == null) prefab = Resources.Load<GameObject>("PhotonPrefabs/" + prefabName);
        if (prefab == null && !playerStyle) prefab = Resources.Load<GameObject>("MushroomZombie_Player");
        if (prefab == null) prefab = Resources.Load<GameObject>("PhotonPrefabs/MushroomZombie_Player");
        if (prefab == null) prefab = Resources.Load<GameObject>("MushroomZombie");
        cache = prefab;
        return cache;
    }

    internal static RuntimeAnimatorController GetCachedNpcZombieAnimator()
    {
        if (_cachedNpcZombieAnimator != null) return _cachedNpcZombieAnimator;
        GameObject npcPrefab = LoadZombiePrefabCached("MushroomZombie", false);
        Animator npcAnimator = npcPrefab != null ? npcPrefab.GetComponentInChildren<Animator>(true) : null;
        _cachedNpcZombieAnimator = npcAnimator != null ? npcAnimator.runtimeAnimatorController : null;
        return _cachedNpcZombieAnimator;
    }

    private void SetupZombieVisuals()
    {
        if (_mushroomZombie == null) return;
        try
        {
            _mushroomZombie.isNPCZombie = false;
            // Zero the vanilla "wake up" timer so the zombie never sits in its dormant/limp
            // WakingUp pose (the default is 5 s, which reads as the zombie being "asleep").
            _mushroomZombie.initialWakeUpTime = 0f;
            _mushroomZombie.currentState = MushroomZombie.State.Idle;
            _mushroomZombie.lifetime = 9999f;

            MushroomFadeInRenderersMethod?.Invoke(_mushroomZombie, null);
            MushroomSetZombieEyesMethod?.Invoke(_mushroomZombie, null);
            // Runs AFTER FadeInRenderers so the vanilla SetMushroomMan (which may swap the animator
            // when the "mushroom man" setting is ON) can't overwrite it.
            ApplyZombieAnimator();
            ApplyZombieOutfit();
        }
        catch (Exception ex) { LogError("SetupZombieVisuals", ex); }
    }

    /// <summary>
    /// Applies the animator that belongs to the selected zombie style. Normal/player zombies use the
    /// NPC zombie controller for zombie-style locomotion; MushroomMan keeps the phobia mushroom-man
    /// controller so pooled re-entry cannot blend the two appearances.
    /// </summary>
    private void ApplyZombieAnimator()
    {
        if (_zombieCharacter?.refs?.animator == null) { LogInfo("ApplyZombieAnimator: no zombie animator."); return; }
        try
        {
            if (_zombieAppearance == ZombiePlugin.ZombieAppearanceOption.MushroomMan)
            {
                RuntimeAnimatorController mushroomManController = _mushroomZombie != null
                    ? _mushroomZombie.animatorMushroomMan
                    : null;
                if (mushroomManController == null)
                { LogInfo("ApplyZombieAnimator: MushroomMan animator/controller missing."); return; }

                string beforeMushroomMan = _zombieCharacter.refs.animator.runtimeAnimatorController != null
                    ? _zombieCharacter.refs.animator.runtimeAnimatorController.name : "null";
                _zombieCharacter.refs.animator.runtimeAnimatorController = mushroomManController;
                LogInfo("ApplyZombieAnimator: switched " + beforeMushroomMan + " -> " + mushroomManController.name);
                return;
            }

            RuntimeAnimatorController npcController = GetCachedNpcZombieAnimator();
            if (npcController == null)
            { LogInfo("ApplyZombieAnimator: NPC animator/controller missing."); return; }
            string before = _zombieCharacter.refs.animator.runtimeAnimatorController != null
                ? _zombieCharacter.refs.animator.runtimeAnimatorController.name : "null";
            _zombieCharacter.refs.animator.runtimeAnimatorController = npcController;
            LogInfo("ApplyZombieAnimator: switched " + before + " -> " + npcController.name);
        }
        catch (Exception ex) { LogError("ApplyZombieAnimator", ex); }
    }

    /// <summary>
    /// Applies the chosen zombie appearance. Player mirrors the owner immediately; Mushroom is the
    /// normal NPC zombie; MushroomMan is the forced phobia mushroom-man mesh.
    /// </summary>
    private void ApplyZombieOutfit()
    {
        if (_mushroomZombie == null) return;
        try
        {
            ApplyControlledZombieAppearance(_mushroomZombie, _zombieAppearance, _playerCharacter);
            if (_zombieAppearance == ZombiePlugin.ZombieAppearanceOption.Player)
            {
                ReassertPlayerAccessory();
            }
            else if (_zombieAppearance == ZombiePlugin.ZombieAppearanceOption.MushroomMan)
            {
                _mushroomManVisualGuardUntil = Time.unscaledTime + 2f;
            }
            if (_zombieRoot != null)
            {
                SetZombieAudioMuted(_zombieRoot, false);
            }
        }
        catch (Exception ex) { LogError("ApplyZombieOutfit", ex); }
    }

    internal static void ApplyNetworkedZombieAppearance(MushroomZombie zombie)
    {
        if (zombie == null) return;
        ApplyControlledZombieAppearance(zombie, GetZombieAppearance(zombie), ResolveZombieSourcePlayer(zombie));
    }

    private static void ApplyControlledZombieAppearance(MushroomZombie zombie, ZombiePlugin.ZombieAppearanceOption appearance, Character sourcePlayer)
    {
        if (zombie == null) return;
        try
        {
            Character zCharacter = zombie.GetComponent<Character>();
            CharacterCustomization customization = zCharacter != null ? zCharacter.refs?.customization : null;
            CustomizationRefs refs = customization != null ? customization.refs : null;

            if (appearance == ZombiePlugin.ZombieAppearanceOption.Player)
            {
                if (customization != null)
                {
                    customization.overridePhotonPlayer = sourcePlayer?.photonView?.Owner;
                    customization.ignorePlayerCosmetics = false;
                    try { CustomizationOnPlayerDataChangeMethod?.Invoke(customization, null); } catch { }
                }
                refs?.SetMushroomMan(false);
                HideMushroomParts(zombie);
                ApplyPlayerFitLowerGarments(zombie);
                return;
            }

            if (customization != null)
            {
                customization.overridePhotonPlayer = null;
                customization.ignorePlayerCosmetics = true;
            }

            if (appearance == ZombiePlugin.ZombieAppearanceOption.MushroomMan)
            {
                refs?.SetMushroomMan(true);
                if (zCharacter?.refs?.animator != null && zombie.animatorMushroomMan != null)
                {
                    zCharacter.refs.animator.runtimeAnimatorController = zombie.animatorMushroomMan;
                }
                try { MushroomClearMushroomVisualsMethod?.Invoke(zombie, null); } catch { }
                ForceMushroomManOnly(zombie);
                return;
            }

            // Normal zombie: explicitly undo the phobia mushroom-man mesh. This prevents the
            // local player's ZombiePhobia setting from bleeding into the controlled normal zombie.
            refs?.SetMushroomMan(false);
            if (zCharacter?.refs?.animator != null)
            {
                RuntimeAnimatorController npcController = GetCachedNpcZombieAnimator();
                if (npcController != null) zCharacter.refs.animator.runtimeAnimatorController = npcController;
            }
        }
        catch (Exception ex) { LogError("ApplyControlledZombieAppearance", ex); }
    }

    internal static void ForceMushroomManOnly(MushroomZombie zombie)
    {
        if (zombie == null) return;
        try
        {
            Character zCharacter = zombie.GetComponent<Character>();
            CharacterCustomization customization = zCharacter != null ? zCharacter.refs?.customization : null;
            CustomizationRefs cr = customization != null ? customization.refs : null;
            if (cr != null)
            {
                // Pooled zombies call FadeInRenderers(), which starts opacity tweens on the normal
                // customization renderers. Keep the mushroom-man skeleton as the only visible body.
                if (cr.AllRenderers != null)
                {
                    foreach (Renderer renderer in cr.AllRenderers)
                    {
                        if (renderer == null || renderer == cr.skeletonRenderer) continue;
                        renderer.enabled = false;
                        SetRendererMaterialFloats(renderer, "_VertexGhost", 0f, "_Opacity", 0f, "_Alpha", 0f);
                    }
                }
                if (cr.hatTransform != null) cr.hatTransform.gameObject.SetActive(false);
                if (cr.skeletonRenderer != null)
                {
                    cr.skeletonRenderer.gameObject.SetActive(true);
                    cr.skeletonRenderer.enabled = true;
                    if (cr.skeletonRenderer is SkinnedMeshRenderer skinned && cr.mushroomManMesh != null)
                        skinned.sharedMesh = cr.mushroomManMesh;
                    if (cr.mushroomManMaterial != null)
                        cr.skeletonRenderer.material = cr.mushroomManMaterial;
                    SetRendererMaterialFloats(cr.skeletonRenderer, "_VertexGhost", 0f, "_Opacity", 1f, "_Alpha", 1f);
                }
                if (cr.mainRendererShadow != null) cr.mainRendererShadow.enabled = false;
                if (cr.skirtShadow != null) cr.skirtShadow.enabled = false;
                if (cr.shortsShadow != null) cr.shortsShadow.enabled = false;
                if (cr.headShadow != null) cr.headShadow.enabled = false;
                if (cr.sashRenderer != null) cr.sashRenderer.enabled = false;
                if (cr.medalRenderer != null) cr.medalRenderer.enabled = false;
                if (cr.thirdEye != null)
                {
                    Renderer thirdEyeRenderer = cr.thirdEye.GetComponent<Renderer>();
                    if (thirdEyeRenderer != null) thirdEyeRenderer.enabled = false;
                }
            }

            if (zombie.skirt != null) zombie.skirt.SetActive(false);
            if (zombie.shorts != null) zombie.shorts.SetActive(false);
            if (zombie.mushroomVisuals != null)
            {
                foreach (GameObject visual in zombie.mushroomVisuals)
                {
                    if (visual != null) visual.SetActive(false);
                }
            }
        }
        catch (Exception ex) { LogError("ForceMushroomManOnly", ex); }
    }

    private static void HideMushroomParts(MushroomZombie zombie)
    {
        if (zombie == null) return;
        try
        {
            if (zombie.skirt != null) zombie.skirt.SetActive(false);
            if (zombie.shorts != null) zombie.shorts.SetActive(false);
            if (zombie.mushroomVisuals != null)
            {
                foreach (GameObject visual in zombie.mushroomVisuals)
                {
                    if (visual != null) visual.SetActive(false);
                }
            }
            Character zCharacter = zombie.GetComponent<Character>();
            CustomizationRefs cr = zCharacter != null ? zCharacter.refs?.customization?.refs : null;
            if (cr != null)
            {
                if (cr.skirt != null) cr.skirt.gameObject.SetActive(false);
                if (cr.shorts != null) cr.shorts.gameObject.SetActive(false);
                if (cr.skirtShadow != null) cr.skirtShadow.gameObject.SetActive(false);
                if (cr.shortsShadow != null) cr.shortsShadow.gameObject.SetActive(false);
                if (cr.sashRenderer != null) cr.sashRenderer.enabled = false;
            }
            var pField = typeof(MushroomZombie).GetField("wearingSkirt", BindingFlags.NonPublic | BindingFlags.Instance);
            if (pField != null) pField.SetValue(zombie, false);
        }
        catch (Exception ex) { LogError("HideMushroomParts", ex); }
    }
    /// <summary>
    /// Re-asserts the player's necktie/chest accessory on the controlled zombie (Player style).
    /// The accessory renderer is the shirt's tie slot; the vanilla CharacterCustomization fills
    /// it from the player's accessory data in OnPlayerDataChange (driven by Start), but if the
    /// zombie spawned before that data landed the renderer stays disabled with a stale texture.
    /// This mirrors the vanilla GetAccessoryIndex → accessories[].texture application directly,
    /// fully guarded: any reflection miss just leaves the vanilla-applied state untouched.
    /// </summary>
    private void ReassertPlayerAccessory()
    {
        if (_zombieCharacter?.refs?.customization?.refs == null || _playerCharacter?.photonView?.Owner == null)
        {
            return;
        }
        try
        {
            CharacterCustomization cust = _zombieCharacter.refs.customization;
            CustomizationRefs zcr = cust.refs;
            if (zcr.accessoryRenderer == null) return;

            MethodInfo getData = typeof(CharacterCustomization).GetMethod("GetCustomizationData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Photon.Realtime.Player) }, null);
            if (getData == null) return;
            object data = getData.Invoke(cust, new object[] { _playerCharacter.photonView.Owner });
            if (data == null) return;

            MethodInfo getAccessory = typeof(CharacterCustomization).GetMethod("GetAccessoryIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { data.GetType() }, null);
            int index = getAccessory != null ? (int)getAccessory.Invoke(cust, new[] { data }) : 0;

            var customizationSingleton = Zorro.Core.Singleton<Customization>.Instance;
            if (customizationSingleton == null || customizationSingleton.accessories == null) return;
            if (index < 0 || index >= customizationSingleton.accessories.Length) return;

            UnityEngine.Texture texture = customizationSingleton.accessories[index].texture;
            if (texture != null)
            {
                zcr.accessoryRenderer.material.SetTexture("_MainTex", texture);
            }
            // Mirror the vanilla accessoryEnabled rule: on unless the accessory is the third-eye
            // or the character is a skeleton (a controlled player zombie is neither).
            zcr.accessoryEnabled = true;
        }
        catch { }
    }

    /// <summary>Hides every mushroom-man exclusive part (lower garments + shadows, sash, mushroom
    /// decorations) so a player-styled zombie shows only the player's outfit. The accessory
    /// renderer (the player's tie / neck accessories) is deliberately NOT touched: after
    /// overridePhotonPlayer + ignorePlayerCosmetics=false the customization fills it with the
    /// PLAYER's own accessory, and disabling it (as an earlier build did) made the player's
    /// necktie invisible on the local view.</summary>
    private void HideMushroomParts()
    {
        if (_mushroomZombie == null) return;
        try
        {
            if (_mushroomZombie.skirt != null) _mushroomZombie.skirt.SetActive(false);
            if (_mushroomZombie.shorts != null) _mushroomZombie.shorts.SetActive(false);
            if (_mushroomZombie.mushroomVisuals != null)
            {
                foreach (GameObject visual in _mushroomZombie.mushroomVisuals)
                {
                    if (visual != null) visual.SetActive(false);
                }
            }
            CustomizationRefs cr = _zombieCharacter?.refs?.customization?.refs;
            if (cr != null)
            {
                if (cr.skirt != null) cr.skirt.gameObject.SetActive(false);
                if (cr.shorts != null) cr.shorts.gameObject.SetActive(false);
                if (cr.skirtShadow != null) cr.skirtShadow.gameObject.SetActive(false);
                if (cr.shortsShadow != null) cr.shortsShadow.gameObject.SetActive(false);
                if (cr.sashRenderer != null) cr.sashRenderer.enabled = false;
                // cr.accessoryRenderer (player tie/accessory) stays enabled — see the summary.
            }
        }
        catch (Exception ex) { LogError("HideMushroomParts", ex); }
    }

    // ===================================================================
    // Player body hiding / freezing / restore
    // ===================================================================

    /// <summary>
    /// Freezes the player's ragdoll (kinematic + zero velocity) and disables CharacterMovement
    /// so the hidden body doesn't sample input, run ground checks, or drift while parked high
    /// above the map.
    /// </summary>
    private void FreezePlayerPhysics()
    {
        if (_playerCharacter?.refs?.ragdoll == null) return;
        try
        {
            // Freeze every ragdoll bodypart manually (disable gravity + collisions + make it
            // kinematic). The game's ToggleKinematic(true) alone is NOT enough — the WarpPlayerRPC
            // "MovePlayer" routine re-enables gravity afterwards and the parked body falls through
            // the world (log shows it reaching -20000 m). Save the original state so we can restore
            // it exactly on exit.
            _frozenBodyparts.Clear();
            _frozenKinematic.Clear();
            _frozenDetectCollisions.Clear();
            _frozenUseGravity.Clear();
            foreach (Bodypart part in _playerCharacter.refs.ragdoll.partList)
            {
                Rigidbody rig = part?.Rig;
                if (rig == null) continue;
                _frozenBodyparts.Add(rig);
                _frozenKinematic.Add(rig.isKinematic);
                _frozenDetectCollisions.Add(rig.detectCollisions);
                _frozenUseGravity.Add(rig.useGravity);
                if (!rig.isKinematic)
                {
                    rig.linearVelocity = Vector3.zero;
                    rig.angularVelocity = Vector3.zero;
                }
                rig.detectCollisions = false;
                rig.useGravity = false;
                rig.isKinematic = true;
            }

            // Disable CharacterMovement so Update/FixedUpdate don't sample input, process jumps,
            // run ground checks, or apply animation forces to the parked body.
            _disabledPlayerMovement = _playerCharacter.GetComponent<CharacterMovement>();
            if (_disabledPlayerMovement != null && _disabledPlayerMovement.enabled)
                _disabledPlayerMovement.enabled = false;
        }
        catch (Exception ex) { LogError("FreezePlayerPhysics", ex); }
    }

    private void RestorePlayerPhysics()
    {
        try
        {
            if (_disabledPlayerMovement != null)
            {
                _disabledPlayerMovement.enabled = true;
                _disabledPlayerMovement = null;
            }
            // Restore each bodypart's original physics state (kinematic / collisions / gravity).
            for (int i = 0; i < _frozenBodyparts.Count; i++)
            {
                Rigidbody rig = _frozenBodyparts[i];
                if (rig == null) continue;
                rig.isKinematic = i < _frozenKinematic.Count && _frozenKinematic[i];
                rig.detectCollisions = i < _frozenDetectCollisions.Count && _frozenDetectCollisions[i];
                rig.useGravity = i < _frozenUseGravity.Count && _frozenUseGravity[i];
                if (!rig.isKinematic)
                {
                    rig.linearVelocity = Vector3.zero;
                    rig.angularVelocity = Vector3.zero;
                }
            }
            _frozenBodyparts.Clear();
            _frozenKinematic.Clear();
            _frozenDetectCollisions.Clear();
            _frozenUseGravity.Clear();
        }
        catch (Exception ex) { LogError("RestorePlayerPhysics", ex); }
    }

    /// <summary>
    /// Sends a reliable Photon event to all other clients (modded only) so they hide/show
    /// the transforming player's original body.
    /// </summary>
    private void SendNetworkBodyVisibility(bool hide)
    {
        if (_playerCharacter?.photonView == null) return;
        if (!PhotonNetwork.InRoom && !PhotonNetwork.IsConnected) return;
        ZombiePlugin.SendBodyVisibility(_playerCharacter.photonView.ViewID, hide);
    }

    /// <summary>
    /// Stashes the original player body below the terrain so un-modded clients can't see it. We move
    /// it immediately (no WarpPlayerRPC "MovePlayer" physics routine, which re-enables gravity) and
    /// mark it grounded so the game's Character.Update never applies fall/parachute physics to it.
    /// The body's network sync KEEPS RUNNING while transformed: PinHiddenBody re-asserts the stash
    /// position every frame and the vanilla CharacterSyncer broadcasts it at ~20 Hz, so remote
    /// clients (modded or not) keep their copy underground, packet loss self-heals on the next tick,
    /// and late joiners converge underground within a tick or two after PUN hands them the stale
    /// spawn-time buffered position. Lighting stays correct because Character.localCharacter was
    /// swapped to the zombie (which stays on the ground).
    /// </summary>
    private void WarpPlayerToHiddenPosition()
    {
        if (_playerCharacter == null) return;
        try
        {
            Vector3 hiddenPos = _playerCharacter.Center + Vector3.down * GetHiddenBodyDepth();
            _hiddenBodyPos = hiddenPos;
            _hiddenBodyPosSet = true;

            ResetPlayerBodyFallState(hiddenPos);
            SetCharacterPositionImmediate(_playerCharacter, hiddenPos, _playerCharacter.transform.rotation);

            LogInfo($"Player body stashed underground ({hiddenPos.y:F0}m).");
        }
        catch (Exception ex) { LogError("WarpPlayerToHiddenPosition", ex); }
    }

    /// <summary>
    /// Marks the parked body as grounded and clears its fall/death state so the game's
    /// Character.Update (UpdateHasParachute etc.) does not apply fall physics to a body that has no
    /// ground beneath it. Without this the body keeps being flung downward/upward, breaking float
    /// precision and lighting.
    /// </summary>
    private void ResetPlayerBodyFallState(Vector3 groundPosition)
    {
        if (_playerCharacter?.data == null) return;
        try
        {
            CharacterData d = _playerCharacter.data;
            d.dead = false;
            d.zombified = false;
            d.passedOut = false;
            d.fullyPassedOut = false;
            d.fallSeconds = 0f;
            d.deathTimer = 0f;
            d.currentRagdollControll = 1f;
            d.isGrounded = true;
            d.isJumping = false;
            d.sinceGrounded = 0f;
            d.groundPos = groundPosition;
            d.sinceJump = 0f;
        }
        catch (Exception ex) { LogError("ResetPlayerBodyFallState", ex); }
    }

    /// <summary>Moves a character (root transform + every ragdoll bodypart) to a new pose instantly,
    /// zeroing velocities. Mirrors the reference mod's SetCharacterPositionImmediate.</summary>
    private static void SetCharacterPositionImmediate(Character character, Vector3 position, Quaternion rotation)
    {
        if (character == null || !IsFiniteVector(position)) return;
        try
        {
            UnityEngine.Transform t = character.transform;
            Quaternion oldRotation = t.rotation;
            if (!IsFiniteQuaternion(rotation)) rotation = oldRotation;
            Vector3 oldCenter = character.Center;
            if (!IsFiniteVector(oldCenter)) oldCenter = t.position;
            Quaternion rotationDelta = rotation * Quaternion.Inverse(oldRotation);
            Vector3 delta = position - oldCenter;
            t.SetPositionAndRotation(t.position + delta, rotation);
            if (character.refs?.ragdoll?.partList != null)
            {
                foreach (Bodypart part in character.refs.ragdoll.partList)
                {
                    if (part == null) continue;
                    if (part.Rig != null)
                    {
                        Vector3 oldPartPosition = part.Rig.position;
                        part.Rig.position = position + rotationDelta * (oldPartPosition - oldCenter);
                        part.Rig.rotation = rotationDelta * part.Rig.rotation;
                        if (!part.Rig.isKinematic)
                        {
                            part.Rig.linearVelocity = Vector3.zero;
                            part.Rig.angularVelocity = Vector3.zero;
                        }
                    }
                    else
                    {
                        Vector3 oldPartPosition = part.transform.position;
                        part.transform.position = position + rotationDelta * (oldPartPosition - oldCenter);
                        part.transform.rotation = rotationDelta * part.transform.rotation;
                    }
                }
            }
        }
        catch (Exception ex) { LogError("SetCharacterPositionImmediate", ex); }
    }

    private void HidePlayerBody()
    {
        if (_playerCharacter == null || _playerCharacter.refs == null || !(ZombiePlugin.HidePlayerBody?.Value ?? true)) return;
        _playerRenderers.Clear(); _playerRendererStates.Clear();
        try
        {
            // Hide both renderer state and material opacity so the parked body stays invisible.
            foreach (Renderer r in _playerCharacter.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                _playerRenderers.Add(r);
                _playerRendererStates.Add(r.enabled);
                r.enabled = false;
                r.forceRenderingOff = true;
                SetRendererMaterialFloats(r, "_VertexGhost", 1f, "_Opacity", 0f, "_Alpha", 0f);
            }

            // Also disable any light the character carries so it doesn't leak through the terrain.
            _playerLights.Clear(); _playerLightStates.Clear();
            foreach (Light light in _playerCharacter.GetComponentsInChildren<Light>(true))
            {
                if (light == null) continue;
                _playerLights.Add(light);
                _playerLightStates.Add(light.enabled);
                light.enabled = false;
            }

            _playerBodyHidden = true;
        }
        catch (Exception ex) { LogError("HidePlayerBody", ex); }
    }

    private void RestorePlayerBody()
    {
        if (_playerBodyHidden)
        {
            try
            {
                for (int i = 0; i < _playerRenderers.Count && i < _playerRendererStates.Count; i++)
                {
                    Renderer r = _playerRenderers[i];
                    if (r == null) continue;
                    r.enabled = _playerRendererStates[i];
                    r.forceRenderingOff = false;
                    if (r.enabled)
                    {
                        SetRendererMaterialFloats(r, "_VertexGhost", 0f, "_Opacity", 1f, "_Alpha", 1f);
                    }
                }
            }
            catch (Exception ex) { LogError("RestorePlayerBody", ex); }
            _playerRenderers.Clear(); _playerRendererStates.Clear(); _playerBodyHidden = false;
        }

        // Restore the player's own light sources that we disabled while the body was hidden.
        try
        {
            for (int i = 0; i < _playerLights.Count && i < _playerLightStates.Count; i++)
            {
                Light light = _playerLights[i];
                if (light != null) light.enabled = _playerLightStates[i];
            }
            _playerLights.Clear(); _playerLightStates.Clear();
        }
        catch (Exception ex) { LogError("RestorePlayerBody lights", ex); }

        // Full human presentation: re-enable the vanilla customization renderers and take off the
        // mushroom-man body if it was applied, so the player is never left as a half-visible zombie.
        try
        {
            if (_playerCharacter != null && _playerCharacter.refs != null && _playerCharacter.refs.customization != null)
            {
                _playerCharacter.refs.customization.ShowAllRenderers();
                if (_playerCharacter.refs.customization.refs != null)
                    _playerCharacter.refs.customization.refs.SetMushroomMan(false);
            }
            if (_playerCharacter != null && _playerCharacter.refs != null && _playerCharacter.refs.hideTheBody != null)
                _playerCharacter.refs.hideTheBody.Refresh();
        }
        catch (Exception ex) { LogError("RestorePlayerBodyPresentation", ex); }
    }

    /// <summary>
    /// Batch material-property setter. renderer.materials RE-INSTANTIATES the material array on
    /// every access (a heavy main-thread operation), so the old per-property version called it
    /// three times per renderer (3 × ~20 renderers = ~60 material instantiations when hiding /
    /// restoring the player body — the source of the revert hitch). Read it once and set all
    /// three properties in a single pass.
    /// </summary>
    private static void SetRendererMaterialFloats(Renderer renderer,
        string p1, float v1, string p2, float v2, string p3, float v3)
    {
        if (renderer == null) return;
        try
        {
            Material[] materials = renderer.materials;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                if (materials[i].HasProperty(p1)) materials[i].SetFloat(p1, v1);
                if (materials[i].HasProperty(p2)) materials[i].SetFloat(p2, v2);
                if (materials[i].HasProperty(p3)) materials[i].SetFloat(p3, v3);
            }
        }
        catch { }
    }

    // ===================================================================
    // HideTheBody cooperation — the vanilla HideTheBody component ghosts
    // body/headRend/sash/costumes via _VertexGhost based on per-frame
    // state (the "first-person" hide logic: local & alive => ghosted).
    // Our controlled zombie is the local character on the owner's client
    // (so the game would ghost it on the owner's own screen) and a network
    // zombie on every other client. These helpers force the zombie AND the
    // clothes/costumes it wears ("zombie clothes" invisible) fully visible
    // on every client, mirroring the reference mod's HideTheBody patches.
    // ===================================================================

    internal static bool IsControlledZombieHideTheBody(HideTheBody hideTheBody, Character fallbackCharacter)
    {
        if (hideTheBody == null) return false;
        // 1) The client that transformed: ActiveZombieCharacter is set there.
        Character zombie = ActiveZombieCharacter;
        if (zombie != null)
        {
            if (fallbackCharacter != null && fallbackCharacter == zombie) return true;
            Character parent = hideTheBody.GetComponentInParent<Character>();
            if (parent != null && parent == zombie) return true;
        }
        // 2) Any client (e.g. a modded observer watching someone else
        //    transform): identify the zombie by our network instantiation
        //    marker so its HideTheBody is force-shown here too.
        try
        {
            Character parent2 = hideTheBody.GetComponentInParent<Character>();
            if (parent2 == null) return false;
            if (fallbackCharacter != null && IsNetworkZombieCharacter(fallbackCharacter)) return true;
            return IsNetworkZombieCharacter(parent2);
        }
        catch { return false; }
    }

    internal static bool IsNetworkZombieCharacter(Character character)
    {
        if (character == null) return false;
        try
        {
            PhotonView view = character.GetComponentInParent<PhotonView>();
            object[] data = view != null ? view.InstantiationData : null;
            return data != null && data.Length > 0
                   && data[0] is string marker
                   && marker == NetworkVisualMarker;
        }
        catch { return false; }
    }

    internal static void RevealHideTheBody(HideTheBody hideTheBody)
    {
        if (hideTheBody == null) return;
        try
        {
            RevealRenderer(hideTheBody.body);
            RevealRenderer(hideTheBody.headRend);
            RevealRenderer(hideTheBody.sash);
            if (hideTheBody.costumes != null)
            {
                foreach (SkinnedMeshRenderer costume in hideTheBody.costumes)
                    RevealRenderer(costume);
            }
            // Mirrors HideTheBody.Toggle(true): also reveal face + hat renderers
            // so no costume piece stays ghosted on remote clients.
            if (hideTheBody.face != null)
            {
                foreach (Renderer r in hideTheBody.face.GetComponentsInChildren<Renderer>(true))
                    RevealRenderer(r);
            }
            if (hideTheBody.refs != null && hideTheBody.refs.playerHats != null)
            {
                foreach (Renderer hat in hideTheBody.refs.playerHats)
                    RevealRenderer(hat);
            }
        }
        catch { }
    }

    private static void RevealRenderer(Renderer renderer)
    {
        if (renderer == null) return;
        // Cheap idle check: the HideTheBody.Update postfix calls this EVERY frame
        // on every client rendering the zombie, so the hot path must stay near-zero
        // when nothing is ghosted. renderer.sharedMaterial is allocation-free (unlike
        // renderer.materials, whose getter allocates and whose setter can instantiate
        // material copies). Toggle() sets the same value on all materials, so reading
        // the first one is sufficient.
        try
        {
            Material shared = renderer.sharedMaterial;
            if (shared != null && shared.HasProperty("_VertexGhost") && shared.GetFloat("_VertexGhost") <= 0.01f)
                return; // already fully visible — nothing to do
        }
        catch { return; }
        SetRendererMaterialFloats(renderer, "_VertexGhost", 0f, "_Opacity", 1f, "_Alpha", 1f);
    }

    private static void ResetPlayerState(Character character, Vector3 restorePosition)
    {
        if (character == null || character.data == null) return;
        try
        {
            CharacterData d = character.data;
            d.dead = false;
            d.zombified = false;
            d.passedOut = false;
            d.fullyPassedOut = false;
            d.fallSeconds = 0f;
            d.deathTimer = 0f;

            d.currentRagdollControll = 1f;
            d.isSprinting = false;
            d.isCrouching = false;
            character.isZombie = false;

            // Ground / fall / velocity state. While hidden the parked body was marked grounded
            // (isGrounded=true) with the underground stash point as groundPos, so reset those and let
            // the game re-run its own ground detection at the restore position.
            d.isGrounded = false;
            d.groundedFor = 0f;
            d.sinceGrounded = 0f;
            d.groundPos = restorePosition;
            d.isJumping = false;
            d.sinceJump = 0f;
            d.avarageVelocity = Vector3.zero;
            d.avarageLastFrameVelocity = Vector3.zero;

            // Assist-jump / boost state (mirrors the reference mod's ClearAssistJumpState).
            d.sincePalJump = 10f;
            d.sinceStandOnPlayer = 10f;
            d.lastStoodOnPlayer = null;
            d.launchedByCannon = false;

            // NOTE: do NOT clear the player's statuses / afflictions / thorns here. While
            // transformed the player kept living (body parked underground), so their spores,
            // poison, zombie-bite, thorns and buffs should survive the round-trip — wiping them
            // resets the player's accumulated state ("becoming a zombie resets player state").
            // The only zombie-form residue is d.zombified / character.isZombie, already cleared
            // above.
        }
        catch (Exception ex) { LogError("ResetPlayerState", ex); }
    }

    private void ClearPlayerInput()
    {
        if (_playerCharacter == null || _playerCharacter.input == null) return;
        _playerCharacter.input.movementInput = Vector2.zero;
        _playerCharacter.input.jumpWasPressed = false;
        _playerCharacter.input.jumpIsPressed = false;
        _playerCharacter.input.sprintIsPressed = false;
        _playerCharacter.input.sprintWasPressed = false;
        _playerCharacter.input.sprintToggleWasPressed = false;
        _playerCharacter.input.usePrimaryWasPressed = false;
        _playerCharacter.input.usePrimaryIsPressed = false;
        _playerCharacter.input.useSecondaryWasPressed = false;
        _playerCharacter.input.useSecondaryIsPressed = false;
        _playerCharacter.input.crouchWasPressed = false;
        _playerCharacter.input.crouchIsPressed = false;
        _playerCharacter.input.crouchToggleWasPressed = false;
        _playerCharacter.input.interactWasPressed = false;
        _playerCharacter.input.interactIsPressed = false;
        _playerCharacter.input.lookInput = Vector2.zero;
    }

    private void DestroyZombie(bool forceDestroy = false)
    {
        DeactivateZombieForDeferredDestroy();
        if (forceDestroy) DestroyDeactivatedZombie();
        else PoolDeactivatedZombie();
    }
    private void SetLocalZombieRendering(bool visible)
    {
        if (_zombieRoot == null) return;
        try { ZombiePlugin.SetRenderersVisible(_zombieRoot, visible); }
        catch (Exception ex) { LogError("SetLocalZombieRendering", ex); }
    }

    /// <summary>
    /// Hides the zombie immediately and parks its references so it can be moved into the local pool
    /// after the player restore work has spread across a few frames.
    /// </summary>
    private void DeactivateZombieForDeferredDestroy()
    {
        if (_mushroomZombie != null) ZombieHarmonyPatches.ClearAttackMouth(_mushroomZombie);
        // Stop and mute all local zombie audio immediately. Networked pooled zombies can stay
        // active for reuse/sync, so renderer hiding alone is not enough to silence local SFX.
        SetZombieAudioMuted(_zombieRoot, true);
        if (_zombieRoot != null)
        {
            SetLocalZombieRendering(false);
            _pendingDestroyRoot = _zombieRoot;
            _pendingDestroyNetworked = _zombieIsNetworked;
            _pendingDestroyAppearance = _zombieAppearance;
        }
        _zombieRoot = null; _zombieCharacter = null; _mushroomZombie = null; _zombiePhotonView = null; _zombieIsNetworked = false;
    }

    /// <summary>Destroys the zombie parked by DeactivateZombieForDeferredDestroy (idempotent).</summary>
    private void DestroyDeactivatedZombie()
    {
        GameObject root = _pendingDestroyRoot;
        bool networked = _pendingDestroyNetworked;
        _pendingDestroyRoot = null;
        _pendingDestroyNetworked = false;
        if (root == null) return;
        try
        {
            ForceDestroyZombieRoot(root, networked);
        }
        catch (Exception ex)
        {
            LogError("DestroyDeactivatedZombie", ex);
            try { Destroy(root); } catch { }
        }
    }

    /// <summary>Parks the zombie instead of destroying it so future transforms can reuse it.</summary>
    private void PoolDeactivatedZombie()
    {
        GameObject root = _pendingDestroyRoot;
        bool networked = _pendingDestroyNetworked;
        ZombiePlugin.ZombieAppearanceOption appearance = _pendingDestroyAppearance;
        _pendingDestroyRoot = null;
        _pendingDestroyNetworked = false;
        if (root == null) return;
        if (networked && (!PhotonNetwork.InRoom || root.GetComponent<PhotonView>() == null || !root.GetComponent<PhotonView>().IsMine))
        {
            ForceDestroyZombieRoot(root, networked);
            return;
        }
        PrepareZombieForPool(root);
        ref GameObject slot = ref GetZombiePoolSlot(appearance);
        if (slot != null && slot != root)
        {
            ForceDestroyZombieRoot(slot, IsNetworkedZombieRoot(slot));
        }
        slot = root;
        LogInfo("Pooled " + appearance + " zombie locally.");
    }

    private static ref GameObject GetZombiePoolSlot(ZombiePlugin.ZombieAppearanceOption appearance)
    {
        if (appearance == ZombiePlugin.ZombieAppearanceOption.Player) return ref _pooledPlayerZombieRoot;
        if (appearance == ZombiePlugin.ZombieAppearanceOption.MushroomMan) return ref _pooledMushroomManZombieRoot;
        return ref _pooledMushroomZombieRoot;
    }

    private static bool IsPooledZombieRoot(GameObject root)
    {
        return root != null && (root == _pooledPlayerZombieRoot || root == _pooledMushroomZombieRoot || root == _pooledMushroomManZombieRoot);
    }

    private static bool IsNetworkedZombieRoot(GameObject root)
    {
        PhotonView view = root != null ? root.GetComponent<PhotonView>() : null;
        return view != null && view.ViewID > 0;
    }

    private static void ForceDestroyZombieRoot(GameObject root, bool networked)
    {
        if (root == null) return;
        try
        {
            if (root == _pooledPlayerZombieRoot) _pooledPlayerZombieRoot = null;
            if (root == _pooledMushroomZombieRoot) _pooledMushroomZombieRoot = null;
            if (root == _pooledMushroomManZombieRoot) _pooledMushroomManZombieRoot = null;
            if (networked && PhotonNetwork.InRoom) PhotonNetwork.Destroy(root);
            else Destroy(root);
        }
        catch
        {
            try { Destroy(root); } catch { }
        }
    }

    // ===================================================================
    // Update / FixedUpdate / LateUpdate
    // ===================================================================

    private void Update()
    {
        if (!Active || _zombieCharacter == null) return;
        try
        {
            ClearPlayerInput();
            KeepZombieAlive();
            PinHiddenBody();
            // Reliable events are not replayed to late joiners — periodically re-send the
            // hide-body event so modded clients that join mid-transform also hide the body's
            // renderers (un-modded clients are covered by the continuous position sync).
            if (Time.unscaledTime >= _nextBodyVisibilityRebroadcast)
            {
                _nextBodyVisibilityRebroadcast = Time.unscaledTime + BodyVisibilityRebroadcastSeconds;
                SendNetworkBodyVisibility(true);
            }
            // Periodically re-assert the zombie state on remote clients via the vanilla
            // RPC_SyncState RPC. A player joining mid-transform gets the zombie fresh from the
            // vanilla instantiate — isNPCZombie is still true there, so their copy runs
            // StartSleeping() into state 0 and its non-owner Update forces passedOut=true
            // (the zombie lies on the floor until it is explicitly woken). Re-broadcasting
            // Idle every couple of seconds stands it back up and covers any state drift.
            // An unmodded MASTER's ZombieManager.ReadyToDisable also only disposes zombies in
            // the Sleeping/Dead states, so keeping the state at Idle additionally guarantees
            // their distance-manager never destroys the networked zombie (the zombie's own
            // Character is in AllCharacters, making the 100 m branch self-immune already).
            if (_zombieIsNetworked && Time.unscaledTime >= _nextZombieStateRebroadcast)
            {
                _nextZombieStateRebroadcast = Time.unscaledTime + ZombieStateRebroadcastSeconds;
                PushZombieStateToRemote();
            }            if (_zombieAppearance == ZombiePlugin.ZombieAppearanceOption.Player
                && Time.unscaledTime >= _nextPlayerOutfitRefreshTime)
            {
                _nextPlayerOutfitRefreshTime = Time.unscaledTime + PlayerOutfitRefreshIntervalSeconds;
                RefreshPlayerZombieOutfitLocal();
            }
            // Unified menu open: freeze input-driven behaviour so menu clicks never leak into the
            // form (camera and maintenance above keep running for live page-2 tuning).
            if (!global::TransformState.MenuOpen)
            {
                DriveZombieInput();
                UpdateAttack();
            }
            if (!Active) return;
            // Defend our localCharacter swap: other transform mods also swap Character.localCharacter,
            // which would steal lighting/fog/camera away from the zombie mid-transform.
            if (_localCharacterSwapped && Character.localCharacter != _zombieCharacter)
            {
                try { Character.localCharacter = _zombieCharacter; } catch { }
            }
            if (IsCharacterClimbing()) StabilizeControlledClimb();
            EnsureClimbSurfaceStillValid();
            ReassertMushroomManVisualGuard();
            HideHud();
        }
        catch (Exception ex) { LogError("Update", ex); }
    }

    private void FixedUpdate()
    {
        if (!Active || _zombieCharacter == null || _zombieCharacter.refs == null || _zombieCharacter.refs.ragdoll == null) return;
        try
        {
            // Climbing stabilization lives here (physics step) so bodypart velocities stay clamped
            // while the vanilla climb FSM keeps the zombie glued to the wall.
            if (IsCharacterClimbing()) StabilizeControlledClimb();

            // Local (non-networked) zombie jump — apply impulse directly since TryToJump's RPC would NRE.
            if (_jumpQueued && !ViewIsMine())
            {
                _jumpQueued = false;
                if (_zombieCharacter.data.isGrounded && _zombieCharacter.data.jumpsRemaining > 0)
                {
                    ApplyJumpImpulse();
                }
            }
        }
        catch (Exception ex) { LogError("FixedUpdate", ex); }
    }

    private void ApplyJumpImpulse()
    {
        float jumpForce = ZombiePlugin.JumpForce.Value;
        foreach (Bodypart part in _zombieCharacter.refs.ragdoll.partList)
        {
            Rigidbody rig = part?.Rig;
            if (rig == null || rig.isKinematic) continue;
            Vector3 v = rig.linearVelocity;
            v.y = jumpForce;
            rig.linearVelocity = v;
        }
        _zombieCharacter.data.jumpsRemaining--;
        _zombieCharacter.data.sinceJump = 0f;
    }

    private void LateUpdate()
    {
        if (!Active || _zombieCharacter == null) return;
        try
        {
            PinHiddenBody();
            SyncCharacterData();
            // Runs after every AnimatedMouth.Update (which drives the talking mouth), so the attack
            // bite expression always wins during a lunge.
            if (_mushroomZombie != null) ZombieHarmonyPatches.UpdateControlledMouth(_mushroomZombie);
            ReassertMushroomManVisualGuard();
            RefreshCamera();
        }
        catch (Exception ex) { LogError("LateUpdate", ex); }
    }

    private void ReassertMushroomManVisualGuard()
    {
        if (_zombieAppearance != ZombiePlugin.ZombieAppearanceOption.MushroomMan) return;
        if (_mushroomZombie == null || Time.unscaledTime > _mushroomManVisualGuardUntil) return;
        ForceMushroomManOnly(_mushroomZombie);
    }

    // ===================================================================
    // Drive the zombie via the original Character input system
    // ===================================================================

    private void DriveZombieInput()
    {
        if (_zombieCharacter.input == null) return;

        // --- Mouse look: only feed the raw delta. The zombie's own CharacterMovement.CameraLook
        // applies sensitivity + invert settings exactly like the vanilla player camera (we run at
        // execution order -100, before CharacterMovement, so the value is consumed the same frame).
        // Applying the delta here as well would double the sensitivity.
        _zombieCharacter.input.lookInput = ReadLookDelta();

        // --- Movement keys
        Vector2 moveInput = GetMovementInput();

        // --- Buttons
        bool jump = Transform.Core.GameInput.JumpHeld(ZombiePlugin.JumpKey.Value);
        bool sprint = Transform.Core.GameInput.SprintHeld(ZombiePlugin.SprintKey.Value);
        bool crouch = Transform.Core.GameInput.CrouchHeld(ZombiePlugin.CrouchKey.Value);
        bool attackHeld = Transform.Core.GameInput.UseSecondaryHeld(ZombiePlugin.AttackKey.Value);

        bool jumpPressed = jump && !_lastJumpPressed;
        bool attackPressed = attackHeld && !_lastAttackPressed;
        _lastJumpPressed = jump;
        _lastAttackPressed = attackHeld;

        // --- Attack (lunge): vanilla lunges with a jump at the start and a sprint charge
        // (WalkTowards mult 1.2, forceSprint) toward the target for lungeTime seconds.
        bool collapsing = _zombieCharacter.data.fallSeconds > 0f;
        bool climbing = IsCharacterClimbing();
        if (_attacking)
        {
            moveInput = new Vector2(0f, 1.2f);
            sprint = true;
            jump = false;
        }
        else if (climbing)
        {
            // While climbing (LMB held) the zombie climbs up; W speeds it up, A/D steer. Attacks are
            // suppressed so LMB stays a climb-only input.
            moveInput.y = Mathf.Max(moveInput.y, 1f);
            sprint = false;
            jump = false;
            attackPressed = false;
        }
        else if (collapsing)
        {
            moveInput = Vector2.zero;
            sprint = false;
            jump = false;
        }

        // After the lunge collapse ends, return the state machine to Idle so the vanilla
        // mouth/bite-collider visuals behave normally again.
        if (!_attacking && !collapsing && _mushroomZombie != null
            && _mushroomZombie.currentState == MushroomZombie.State.LungeRecovery)
        {
            _mushroomZombie.currentState = MushroomZombie.State.Idle;
        }

        // --- Feed the original character input system ---
        _zombieCharacter.input.movementInput = moveInput;
        _zombieCharacter.input.jumpIsPressed = jump && !_attacking && !collapsing;

        // For networked zombies (PhotonView.IsMine), let CharacterMovement.TryToJump handle it via RPC.
        // For local zombies (no network ownership), queue the jump and apply it directly in FixedUpdate
        // to avoid a NullReferenceException inside TryToJump's RPC call.
        bool jumpNow = jumpPressed && !_attacking && !collapsing;
        if (ViewIsMine())
        {
            _zombieCharacter.input.jumpWasPressed = jumpNow;
            _jumpQueued = false;
        }
        else
        {
            _zombieCharacter.input.jumpWasPressed = false;
            if (jumpNow) _jumpQueued = true;
        }

        _zombieCharacter.input.sprintIsPressed = sprint;
        _zombieCharacter.input.crouchIsPressed = crouch && !_attacking && !collapsing;

        // For locally-instantiated zombies (no PhotonView ownership), SetMovementState may not run,
        // so directly mirror the sprint / crouch flags.
        if (!ViewIsMine())
        {
            _zombieCharacter.data.isSprinting = _zombieCharacter.input.sprintIsPressed;
            _zombieCharacter.data.isCrouching = _zombieCharacter.input.crouchIsPressed && _zombieCharacter.data.isGrounded;
        }

        // --- Attack trigger
        if (attackPressed && !_attacking && !collapsing)
        {
            if (Time.unscaledTime - _lastAttackTime >= ZombiePlugin.AttackCooldown.Value) StartAttack();
        }

        // Shared stamina bar: NO auto-refill. The zombie's stamina is driven by the vanilla
        // systems (sprint/jump/climb consume it, it regenerates like a normal player), and we
        // mirror it onto the parked player body so both share one stamina value/UI bar.
        SyncPlayerStamina();

        // --- Climb input (hold LMB to climb, release to stop) ---
        ForwardClimbInput();
    }

    /// <summary>Shares the stamina bar with the parked player body. The player body is frozen while
    /// hidden (its Character.Update is skipped), so the zombie's vanilla stamina usage and
    /// regeneration ARE the shared bar; mirroring the zombie's value onto the player body keeps both
    /// (and the UI, which reads Character.localCharacter = the zombie) showing the same stamina.</summary>
    private void SyncPlayerStamina()
    {
        if (_playerCharacter?.data == null || _zombieCharacter?.data == null) return;
        try
        {
            _playerCharacter.data.currentStamina = _zombieCharacter.data.currentStamina;
        }
        catch (Exception ex) { LogError("SyncPlayerStamina", ex); }
    }

    // ===================================================================
    // Climbing — drives the vanilla CharacterClimbing (player climbing) so the
    // zombie can climb walls like a player. Core ideas from the reference
    // "I'm Zombie" mod's climbing: drive the vanilla StartClimbRpc via
    // reflection (not custom physics), reset the climbing FSM's internal state
    // after starting, and re-feed the climb state every frame.
    // ===================================================================

    private bool IsCharacterClimbing()
    {
        return _zombieCharacter?.data != null && _zombieCharacter.data.isClimbing;
    }

    private bool IsClimbInputHeld()
    {
        return Transform.Core.GameInput.UsePrimaryHeld(ZombiePlugin.ClimbKey.Value);
    }

    private void ForwardClimbInput()
    {
        if (_zombieCharacter?.input == null || _zombieCharacter.data == null || _zombieCharacter.refs?.climbing == null) return;

        bool usePrimaryPressed = false;
        bool usePrimaryHeld = false;
        bool usePrimaryReleased = false;
        // Prefer the game's unified Use action (works on a gamepad without any rebinding).
        KeyCode climbKey = ZombiePlugin.ClimbKey.Value;
        usePrimaryPressed = Transform.Core.GameInput.UsePrimaryPressed(climbKey);
        usePrimaryHeld = Transform.Core.GameInput.UsePrimaryHeld(climbKey);
        usePrimaryReleased = Transform.Core.GameInput.UsePrimaryReleased(climbKey);

        _zombieCharacter.input.usePrimaryWasPressed = usePrimaryPressed;
        _zombieCharacter.input.usePrimaryIsPressed = usePrimaryHeld || usePrimaryPressed;

        if (usePrimaryHeld || usePrimaryPressed)
        {
            try
            {
                if (_zombieCharacter.data.currentItem == null)
                    _zombieCharacter.data.sincePressClimb = 0f;
            }
            catch { }
            if (!IsCharacterClimbing() && (usePrimaryPressed || Time.time >= _nextClimbAttemptTime))
            {
                _nextClimbAttemptTime = Time.time + ClimbAttemptCooldownSeconds;
                TryStartLocalClimb();
            }
        }

        if (usePrimaryReleased)
        {
            _zombieCharacter.input.usePrimaryIsPressed = false;
            _zombieCharacter.input.usePrimaryWasReleased = true;
            _hasLastClimbStart = false;
            if (IsCharacterClimbing())
                StopControlledZombieClimb(ClimbReleaseFallSeconds);
        }
        else
        {
            _zombieCharacter.input.usePrimaryWasReleased = false;
        }
    }

    private bool TryStartLocalClimb()
    {
        if (_zombieCharacter == null || _zombieCharacter.data == null || _zombieCharacter.refs?.climbing == null) return false;
        if (_zombieCharacter.data.isClimbing || _zombieCharacter.data.isRopeClimbing || _zombieCharacter.data.isVineClimbing) return true;
        if (_zombieCharacter.data.currentItem != null) return false;

        CharacterClimbing climbing = _zombieCharacter.refs.climbing;
        if (!CanControlledZombieClimb(climbing)) return false;

        Vector3 origin = _zombieCharacter.Center;
        Vector3 forward = GetClimbForward();
        if (forward.sqrMagnitude < 0.0001f) return false;
        if (!TryFindLocalClimbHit(origin, forward, ClimbStartRayDistance, out RaycastHit hit)) return false;
        if (IsRepeatedClimbStart(hit.point, hit.normal)) return false;

        bool started = TryStartControlledZombieClimb(climbing, _zombieCharacter, hit.point, hit.normal);
        if (started)
        {
            RecordClimbStart(hit.point, hit.normal);
            if (_zombieCharacter.input != null)
            {
                _zombieCharacter.input.jumpWasPressed = false;
                _zombieCharacter.input.jumpIsPressed = false;
            }
        }
        return started;
    }

    /// <summary>Resolves a non-zero climb direction.</summary>
    private Vector3 GetClimbForward()
    {
        Vector3 forward = Vector3.zero;
        Camera climbCamera = GetClimbCamera();
        if (climbCamera != null) forward = climbCamera.transform.forward;
        if (forward.sqrMagnitude < 0.0001f && _zombieCharacter != null) forward = _zombieCharacter.transform.forward;
        if (forward.sqrMagnitude < 0.0001f && _zombieCharacter?.data != null) forward = _zombieCharacter.data.lookDirection;
        if (forward.sqrMagnitude < 0.0001f && _zombieCharacter?.data != null) forward = _zombieCharacter.data.lookDirection_Flat;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        return forward.normalized;
    }

    private Camera GetClimbCamera()
    {
        return Camera.main;
    }

    private bool CanControlledZombieClimb(CharacterClimbing climbing)
    {
        if (climbing == null) return false;
        try
        {
            if (ClimbCanClimbMethod != null && ClimbCanClimbMethod.Invoke(climbing, null) is bool canClimb)
                return canClimb;
        }
        catch { }
        return true;
    }

    private bool TryStartControlledZombieClimb(CharacterClimbing climbing, Character character, Vector3 climbPos, Vector3 climbNormal)
    {
        if (climbing == null) return false;
        try
        {
            // Go through the Photon RPC channel so remote clients start the climb too (the vanilla
            // player climb uses view.RPC("StartClimbRpc", RpcTarget.All, ...)). A direct reflection
            // Invoke would only run locally — remote clients would never see the zombie climbing.
            PhotonView view = character?.photonView;
            if (view != null)
            {
                view.RPC("StartClimbRpc", RpcTarget.All, climbPos, climbNormal);
            }
            else
            {
                if (ClimbStartClimbRpcMethod == null) { LogInfo("Climb: StartClimbRpc unavailable."); return false; }
                object[] args = BuildClimbRpcArguments(ClimbStartClimbRpcMethod, climbPos, climbNormal);
                if (args == null) return false;
                ClimbStartClimbRpcMethod.Invoke(climbing, args);
            }
            // Reset the vanilla climb FSM's internal state, otherwise it cancels the climb
            // immediately (thinks it's toggled/on-cooldown).
            ClimbClimbToggledOnField?.SetValue(climbing, false);
            ClimbPlayerSlideField?.SetValue(climbing, Vector2.zero);
            ClimbSinceLastClimbStartedField?.SetValue(climbing, 0f);
            if (character?.data != null)
            {
                _nextClimbAttemptTime = Time.time + ClimbAttemptCooldownSeconds;
                character.data.climbPos = climbPos;
                character.data.climbNormal = climbNormal.sqrMagnitude > 0.0001f ? climbNormal.normalized : Vector3.zero;
                character.data.sincePressClimb = 0f;
                character.data.sinceCanClimb = 0f;
                character.data.sinceGrounded = 0f;
                character.data.fallSeconds = 0f;
                character.data.passedOut = false;
                character.data.fullyPassedOut = false;
                character.data.avarageVelocity = Vector3.zero;
                character.data.avarageLastFrameVelocity = Vector3.zero;
                character.data.worldMovementInput_Grounded = Vector3.zero;
            }
            return true;
        }
        catch (Exception ex) { LogError("TryStartControlledZombieClimb", ex); return false; }
    }

    private void StopControlledZombieClimb(float setFall)
    {
        if (_zombieCharacter?.data == null) return;
        try
        {
            // Broadcast the stop through the vanilla RPC so remote clients end the climb too.
            PhotonView view = _zombieCharacter.photonView;
            if (view != null)
                view.RPC("StopClimbingRpc", RpcTarget.All, setFall);
        }
        catch { }
        // Local belt-and-braces (StopClimbingRpc also sets most of these).
        _zombieCharacter.data.isClimbing = false;
        _zombieCharacter.data.isRopeClimbing = false;
        _zombieCharacter.data.isVineClimbing = false;
        _zombieCharacter.data.isJumping = false;
        _zombieCharacter.data.sinceGrounded = setFall;
        _zombieCharacter.data.sinceClimb = 999f;
        _zombieCharacter.data.sinceCanClimb = 0f;
        _zombieCharacter.data.sincePressClimb = 1f;
        _zombieCharacter.data.climbNormal = Vector3.zero;
        _climbReleaseFallUntil = Time.time + ClimbReleaseFallWindowSeconds;
        _nextClimbAttemptTime = Time.time + ClimbAttemptCooldownSeconds;
        if (_zombieCharacter.input != null)
        {
            _zombieCharacter.input.usePrimaryIsPressed = false;
            _zombieCharacter.input.usePrimaryWasPressed = false;
            _zombieCharacter.input.usePrimaryWasReleased = true;
        }
        try
        {
            ClimbPlayerSlideField?.SetValue(_zombieCharacter.refs?.climbing, Vector2.zero);
            ClimbClimbToggledOnField?.SetValue(_zombieCharacter.refs?.climbing, false);
            ClimbSinceLastClimbStartedField?.SetValue(_zombieCharacter.refs?.climbing, 999f);
        }
        catch { }
    }

    /// <summary>Keeps the climbing zombie glued to the wall every frame: high ragdoll control,
    /// clears fall/passed-out so the vanilla checks don't knock it off, clamps bodypart velocities.</summary>
    private void StabilizeControlledClimb()
    {
        if (_zombieCharacter?.data == null) return;
        try
        {
            _zombieCharacter.data.currentRagdollControll = ClimbRagdollControl;
            _zombieCharacter.data.fallSeconds = 0f;
            _zombieCharacter.data.passedOut = false;
            _zombieCharacter.data.fullyPassedOut = false;
            _zombieCharacter.data.isGrounded = false;
            _zombieCharacter.data.sinceGrounded = 0f;
            _zombieCharacter.data.sinceCanClimb = 0f;
            _zombieCharacter.data.sincePressClimb = 0f;
        }
        catch { }
        if (_zombieCharacter.refs?.ragdoll?.partList == null) return;
        try
        {
            foreach (Bodypart part in _zombieCharacter.refs.ragdoll.partList)
            {
                Rigidbody rig = part?.Rig;
                if (rig == null || rig.isKinematic) continue;
                rig.linearVelocity = Vector3.ClampMagnitude(rig.linearVelocity, ClimbMaxLinearVelocity);
                rig.angularVelocity = Vector3.ClampMagnitude(rig.angularVelocity, ClimbMaxAngularVelocity);
            }
        }
        catch { }
    }

    /// <summary>Stops the climb when the wall is gone (or LMB released) so the zombie doesn't hover.</summary>
    private void EnsureClimbSurfaceStillValid()
    {
        if (!IsCharacterClimbing() || _zombieCharacter?.refs?.climbing == null) return;
        if (_zombieCharacter.data.isRopeClimbing || _zombieCharacter.data.isVineClimbing) return;
        if (!IsClimbInputHeld())
        {
            StopControlledZombieClimb(ClimbReleaseFallSeconds);
            return;
        }
        Vector3 origin = _zombieCharacter.Center;
        Vector3[] directions =
        {
            -_zombieCharacter.data.climbNormal,
            _zombieCharacter.data.lookDirection,
            _zombieCharacter.data.lookDirection_Flat,
            _zombieCharacter.transform.forward
        };
        foreach (Vector3 raw in directions)
        {
            if (raw.sqrMagnitude < 0.0001f) continue;
            if (TryFindLocalClimbHit(origin, raw.normalized, ClimbSurfaceProbeDistance, out _))
            {
                _zombieCharacter.data.sinceCanClimb = 0f;
                return;
            }
        }
        StopControlledZombieClimb(ClimbReleaseFallSeconds);
    }

    private bool TryFindLocalClimbHit(Vector3 origin, Vector3 direction, float distance, out RaycastHit climbHit)
    {
        climbHit = default;
        RaycastHit[] hits;
        try
        {
            hits = Physics.RaycastAll(origin, direction, Mathf.Max(distance, 0.25f), ~0, QueryTriggerInteraction.Ignore);
        }
        catch { return false; }
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.collider.isTrigger) continue;
            Character hitCharacter = hit.collider.GetComponentInParent<Character>();
            if (hitCharacter == _zombieCharacter || hitCharacter == _playerCharacter) continue;
            climbHit = hit;
            return true;
        }
        return false;
    }

    private bool IsRepeatedClimbStart(Vector3 point, Vector3 normal)
    {
        if (!_hasLastClimbStart) return false;
        if (Vector3.Distance(_lastClimbStartPoint, point) > ClimbRepeatedHitDistance) return false;
        Vector3 lastNormal = _lastClimbStartNormal.sqrMagnitude > 0.0001f ? _lastClimbStartNormal.normalized : Vector3.forward;
        Vector3 nextNormal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.forward;
        return Vector3.Dot(lastNormal, nextNormal) >= ClimbRepeatedNormalDot;
    }

    private void RecordClimbStart(Vector3 point, Vector3 normal)
    {
        _hasLastClimbStart = true;
        _lastClimbStartPoint = point;
        _lastClimbStartNormal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.forward;
    }

    private bool ViewIsMine()
    {
        return _zombiePhotonView != null && _zombiePhotonView.IsMine;
    }

    private static Vector2 GetMovementInput()
    {
        Vector2 input = Vector2.zero;
        if (Input.GetKey(KeyCode.W)) input.y += 1f;
        if (Input.GetKey(KeyCode.S)) input.y -= 1f;
        if (Input.GetKey(KeyCode.A)) input.x -= 1f;
        if (Input.GetKey(KeyCode.D)) input.x += 1f;

        // Merge gamepad left-stick / dpad from the game's unified Input System so the zombie
        // can be driven with a controller, matching the critter/ghost/statue forms.
        input += Transform.Core.GameInput.Move();
        return Vector2.ClampMagnitude(input, 1f);
    }

    /// <summary>
    /// Reads mouse/look delta from the game's new Input System (same source CharacterInput.Sample uses),
    /// falling back to the legacy Input Manager axes.
    /// </summary>
    private static Vector2 ReadLookDelta()
    {
        return Transform.Core.GameInput.Look();
    }

    private void KeepZombieAlive()
    {
        if (_zombieCharacter == null || _zombieCharacter.data == null) return;
        // While the post-lunge collapse (fallSeconds > 0) plays out, leave the vanilla ragdoll
        // behaviour alone so the zombie crumples like an NPC zombie does, then stands back up.
        if (_zombieCharacter.data.fallSeconds > 0f) return;
        _zombieCharacter.data.dead = false;
        _zombieCharacter.data.zombified = false;
        _zombieCharacter.data.passedOut = false;
        _zombieCharacter.data.fullyPassedOut = false;
        _zombieCharacter.data.fallSeconds = 0f;
        // Ensure ragdoll control stays at full so the animation-driven movement works
        if (_zombieCharacter.data.currentRagdollControll < 1f)
            _zombieCharacter.data.currentRagdollControll = 1f;
    }

    /// <summary>
    /// Re-asserts the parked body's position and re-freezes its ragdoll every frame. The vanilla
    /// WarpPlayerRPC "MovePlayer" routine only runs once at enter and then re-enables gravity, so
    /// without this the body falls through the world (the log shows it reaching -20000 m). At that
    /// range 32-bit float precision collapses and any shadow / fog / lighting work that references
    /// the player transform breaks, producing the in-game lighting glitches. Re-freezing + pinning
    /// every frame keeps the body exactly where the camera/lighting expect it.
    /// </summary>
    private void PinHiddenBody()
    {
        if (!_hiddenBodyPosSet || _playerCharacter == null) return;
        try
        {
            // Keep the parked body grounded and frozen every frame. The game's Character.Update
            // re-applies fall/parachute physics to a body with no ground beneath it, and the vanilla
            // warp routine re-enables gravity, so we must re-assert: grounded state, kinematic +
            // gravity-off bodyparts, and the exact stash position (root + bodyparts).
            ResetPlayerBodyFallState(_hiddenBodyPos);

            if (_playerCharacter.refs?.ragdoll?.partList != null)
            {
                foreach (Bodypart part in _playerCharacter.refs.ragdoll.partList)
                {
                    Rigidbody rig = part?.Rig;
                    if (rig == null) continue;
                    if (!rig.isKinematic)
                    {
                        rig.linearVelocity = Vector3.zero;
                        rig.angularVelocity = Vector3.zero;
                    }
                    rig.detectCollisions = false;
                    rig.useGravity = false;
                    rig.isKinematic = true;
                }
            }

            if (Vector3.Distance(_playerCharacter.Center, _hiddenBodyPos) > 0.5f)
                SetCharacterPositionImmediate(_playerCharacter, _hiddenBodyPos, _playerCharacter.transform.rotation);
        }
        catch (Exception ex) { LogError("PinHiddenBody", ex); }
    }

    /// <summary>
    /// Mirrors Character.RecalculateLookDirections() (which is internal) — converts
    /// lookValues (yaw=x, pitch=y) into lookDirection / lookDirection_Flat / right / up.
    /// </summary>
    private static void RecalculateLookDirections(Character character)
    {
        if (character?.data == null) return;
        Vector2 lv = character.data.lookValues;
        Vector3 dir = (Quaternion.Euler(-lv.y, lv.x, 0f) * Vector3.forward).normalized;
        character.data.lookDirection = dir;
        Vector3 flat = dir; flat.y = 0f; flat.Normalize();
        character.data.lookDirection_Flat = flat;
        character.data.lookDirection_Right = Vector3.Cross(Vector3.up, dir).normalized;
        character.data.lookDirection_Up = Vector3.Cross(dir, character.data.lookDirection_Right).normalized;
    }

    // ===================================================================
    // Attack — replicates the vanilla MushroomZombie lunge:
    // jump at the start, sprint charge (WalkTowards mult 1.2, forceSprint) for
    // lungeTime seconds with mouth open + bite collider active, then the zombie
    // crumples for 3 seconds (LungeRecovery) before standing back up.
    // ===================================================================

    private float GetLungeDuration()
    {
        if (_mushroomZombie != null && _mushroomZombie.lungeTime > 0f) return _mushroomZombie.lungeTime;
        return ZombiePlugin.AttackDuration.Value;
    }

    private void UpdateAttack()
    {
        if (!_attacking) return;
        float elapsed = Time.unscaledTime - _attackStartedAt;
        float duration = GetLungeDuration();
        if (elapsed >= duration) EndAttack();
        float revert = ZombiePlugin.AttackRevertSeconds.Value;
        if (revert > 0f && elapsed >= duration + revert) ExitZombie();
    }

    private void StartAttack()
    {
        _attacking = true; _attackStartedAt = Time.unscaledTime; _lastAttackTime = Time.unscaledTime;
        if (_mushroomZombie != null)
        {
            // State -> Lunging syncs to every client (PushState RPC); the visual update patch
            // then opens the mouth and enables the bite collider there, like an NPC zombie.
            _mushroomZombie.currentState = MushroomZombie.State.Lunging;
            ZombieHarmonyPatches.MarkAttackMouth(_mushroomZombie);
        }
        // Vanilla lunges start with a jump (StartLunging sets jumpWasPressed)
        if (ViewIsMine()) _zombieCharacter.input.jumpWasPressed = true;
        else _jumpQueued = true;
        LogInfo("Zombie lunge!");
    }

    private void EndAttack()
    {
        _attacking = false;
        if (_zombieCharacter != null && _zombieCharacter.data != null)
        {
            // Vanilla DoLunging() ends with character.Fall(3f) — the zombie crumples.
            // Set fallSeconds directly (same effect as RPCA_Fall) before the state change
            // so PushState syncs it to the other clients.
            _zombieCharacter.data.fallSeconds = Mathf.Max(_zombieCharacter.data.fallSeconds, 3f);
        }
        if (_mushroomZombie != null)
        {
            _mushroomZombie.currentState = MushroomZombie.State.LungeRecovery;
        }
    }

    // ===================================================================
    // Character data sync (velocity etc. for animation)
    // ===================================================================

    private void SyncCharacterData()
    {
        if (_zombieCharacter == null || _zombieCharacter.data == null) return;
        Vector3 center = _zombieCharacter.Center;
        Vector3 velocity = _prevCenter.sqrMagnitude > 0f ? (center - _prevCenter) / Mathf.Max(Time.deltaTime, 0.0001f) : Vector3.zero;
        _prevCenter = center;
        _zombieCharacter.data.avarageLastFrameVelocity = _zombieCharacter.data.avarageVelocity;
        _zombieCharacter.data.avarageVelocity = velocity;
        _zombieCharacter.data.worldMovementInput = velocity;
        _zombieCharacter.data.worldMovementInput_Grounded = velocity;
        _zombieCharacter.data.sinceGrounded = Mathf.Min(_zombieCharacter.data.sinceGrounded, _zombieCharacter.data.isGrounded ? 0f : 10f);
    }

    // ===================================================================
    // Third-person camera — same algorithm as the reference "I'm Zombie" mod:
    // position = lookTarget - flattenedForward * distance, rotation = LookRotation(lookDirection).
    // No collision avoidance, no ground clamp, no FOV changes (vanilla game feel).
    // During a lunge the direction freezes and the look-target drops to the attack offset.
    // ===================================================================

    private void RefreshCamera()
    {
        try
        {
            Camera camera = Camera.main;
            if (camera == null || _zombieCharacter == null || _zombieCharacter.data == null) return;

            Vector3 targetPosition = GetThirdPersonCameraPosition(camera, _zombieCharacter);
            Quaternion targetRotation = GetThirdPersonCameraRotation(camera, _zombieCharacter);

            // Guard against NaN (degenerate look state while the body is being warped): feeding a
            // NaN transform to the camera collapses the render and makes lighting appear to fail.
            if (!IsFiniteVector(targetPosition) || !IsFiniteQuaternion(targetRotation)) return;

            Vector3 smoothedPosition = GetSmoothedThirdPersonCameraPosition(targetPosition);
            Quaternion smoothedRotation = GetSmoothedThirdPersonCameraRotation(targetRotation);
            if (!IsFiniteVector(smoothedPosition) || !IsFiniteQuaternion(smoothedRotation)) return;
            camera.transform.SetPositionAndRotation(smoothedPosition, smoothedRotation);
        }
        catch (Exception ex) { LogError("RefreshCamera", ex); }
    }

    private bool IsControlledZombieAttackCameraState()
    {
        if (_mushroomZombie == null) return false;
        try
        {
            return _mushroomZombie.currentState == MushroomZombie.State.Lunging
                || _mushroomZombie.currentState == MushroomZombie.State.LungeRecovery;
        }
        catch { return false; }
    }

    private Vector3 GetThirdPersonCameraPosition(Camera camera, Character character)
    {
        Vector3 forward = Vector3.zero;
        if (IsControlledZombieAttackCameraState() && camera != null)
        {
            // During a lunge the camera direction freezes so it doesn't spin around the charge.
            forward = camera.transform.forward;
        }
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = character.data.lookDirection_Flat;
        }
        forward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.ProjectOnPlane(character.transform.forward, Vector3.up);
        }
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }
        forward.Normalize();
        return GetThirdPersonLookTarget(character) - forward * GetCameraDistance();
    }

    private Vector3 GetThirdPersonLookTarget(Character character)
    {
        Vector3 target = GetRawThirdPersonLookTarget(character);
        if (!_hasSmoothedCameraTarget || Vector3.Distance(_smoothedCameraTarget, target) > ThirdPersonCameraSnapDistance)
        {
            _smoothedCameraTarget = target;
            _hasSmoothedCameraTarget = true;
            return target;
        }
        float lerp = GetExponentialLerp(ThirdPersonCameraFollowSharpness);
        _smoothedCameraTarget = Vector3.Lerp(_smoothedCameraTarget, target, lerp);
        return _smoothedCameraTarget;
    }

    private Vector3 GetRawThirdPersonLookTarget(Character character)
    {
        if (IsControlledZombieAttackCameraState())
        {
            Vector3 attackTarget = character.Center + Vector3.up * Mathf.Max(ThirdPersonAttackHeightOffset, 0.1f);
            return IsFiniteVector(attackTarget) ? attackTarget : character.transform.position;
        }
        return character.Center + Vector3.up * GetCameraHeight();
    }

    private Quaternion GetThirdPersonCameraRotation(Camera camera, Character character)
    {
        if (IsControlledZombieAttackCameraState() && camera != null)
        {
            return camera.transform.rotation;
        }
        Vector3 lookDirection = character.data.lookDirection;
        if (lookDirection.sqrMagnitude < 0.0001f) lookDirection = character.data.lookDirection_Flat;
        if (lookDirection.sqrMagnitude < 0.0001f) lookDirection = character.transform.forward;
        if (lookDirection.sqrMagnitude < 0.0001f) lookDirection = Vector3.forward;
        return Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
    }

    private Vector3 GetSmoothedThirdPersonCameraPosition(Vector3 targetPosition)
    {
        if (!_hasSmoothedCameraPose || Vector3.Distance(_smoothedCameraPosition, targetPosition) > ThirdPersonCameraSnapDistance)
        {
            _smoothedCameraPosition = targetPosition;
            _hasSmoothedCameraPose = true;
            return targetPosition;
        }
        float lerp = GetExponentialLerp(ThirdPersonCameraPositionSharpness);
        _smoothedCameraPosition = Vector3.Lerp(_smoothedCameraPosition, targetPosition, lerp);
        return _smoothedCameraPosition;
    }

    private Quaternion GetSmoothedThirdPersonCameraRotation(Quaternion targetRotation)
    {
        if (!_hasSmoothedCameraPose)
        {
            _smoothedCameraRotation = targetRotation;
            return targetRotation;
        }
        float lerp = GetExponentialLerp(ThirdPersonCameraRotationSharpness);
        _smoothedCameraRotation = Quaternion.Slerp(_smoothedCameraRotation, targetRotation, lerp);
        return _smoothedCameraRotation;
    }

    private void ResetCameraSmoothing()
    {
        _hasSmoothedCameraPose = false;
        _smoothedCameraPosition = Vector3.zero;
        _smoothedCameraRotation = Quaternion.identity;
        _hasSmoothedCameraTarget = false;
        _smoothedCameraTarget = Vector3.zero;
    }

    private static float GetExponentialLerp(float sharpness)
    {
        float deltaTime = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime;
        return Mathf.Clamp01(1f - Mathf.Exp(-Mathf.Max(sharpness, 0.01f) * Mathf.Max(deltaTime, 0f)));
    }

    private static bool IsFiniteVector(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
            && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }

    private static bool IsFiniteQuaternion(Quaternion q)
    {
        return !float.IsNaN(q.x) && !float.IsNaN(q.y) && !float.IsNaN(q.z) && !float.IsNaN(q.w)
            && !float.IsInfinity(q.x) && !float.IsInfinity(q.y) && !float.IsInfinity(q.z) && !float.IsInfinity(q.w);
    }

    private static float GetCameraDistance()
    {
        return Mathf.Clamp(ZombiePlugin.CameraDistance?.Value ?? DefaultCameraDistance, 1.5f, 6f);
    }
    private static float GetCameraHeight()
    {
        return Mathf.Clamp(ZombiePlugin.CameraHeight?.Value ?? DefaultCameraHeight, 0.3f, 1.8f);
    }
    private static float GetCameraFov()
    {
        return Mathf.Clamp(ZombiePlugin.CameraFov?.Value ?? DefaultCameraFov, 60f, 110f);
    }
    private static float GetHiddenBodyDepth()
    {
        return Mathf.Clamp(ZombiePlugin.HiddenBodyDepth?.Value ?? DefaultHiddenBodyDepth, 5f, 200f);
    }
    private ZombiePlugin.ZombieAppearanceOption GetAppearance()
    {
        // The menu's explicit request (player zombie vs mushroom zombie card) wins over the
        // config; it is set right before Enter and cleared right after.
        if (ZombiePlugin.PendingAppearance.HasValue) return ZombiePlugin.PendingAppearance.Value;
        return ZombiePlugin.ZombieAppearance?.Value ?? ZombiePlugin.ZombieAppearanceOption.Player;
    }

    /// <summary>Appearance of this zombie instance (set at spawn; read by the menu to highlight
    /// the matching zombie card).</summary>
    internal ZombiePlugin.ZombieAppearanceOption CurrentAppearance => _zombieAppearance;

    /// <summary>Reads the appearance style that was broadcast with the zombie's Photon instantiation
    /// data (used by the Harmony patches on every client, modded or not).</summary>
    internal static ZombiePlugin.ZombieAppearanceOption GetZombieAppearance(MushroomZombie zombie)
    {
        if (zombie == null) return ZombiePlugin.ZombieAppearanceOption.Player;
        try
        {
            PhotonView view = zombie.GetComponent<PhotonView>();
            object[] data = view != null ? view.InstantiationData : null;
            if (data != null && data.Length > 2 && data[2] is int style
                && System.Enum.IsDefined(typeof(ZombiePlugin.ZombieAppearanceOption), style))
                return (ZombiePlugin.ZombieAppearanceOption)style;
        }
        catch { }
        return ZombiePlugin.ZombieAppearanceOption.Player;
    }

    // ===================================================================
    // Original camera control disable / restore
    // ===================================================================

    /// <summary>
    /// Mirrors the source player's fit lower-garment state onto a Player-style controlled zombie:
    /// skirt fits show the (mushroom-man) skirt, the shorts fit shows shorts, and noPants fits
    /// hide both (the suit mesh covers the legs). Called from the RPC_SetOutfit prefix so the
    /// vanilla binary skirt/shorts toggle — driven by each remote client's random AwakeRoutine
    /// Coinflip — is replaced by the player's actual fit state on every modded client. The
    /// player's own refs already reflect their fit (CharacterCustomization applied it), so we
    /// simply mirror the active states. Un-modded clients keep the vanilla toggle; the owner
    /// later re-broadcasts RPC_SetOutfit with the player's choice so they converge. If the
    /// source player cannot be resolved the current state is left untouched
    /// (OnPlayerDataChange already applied the owner's outfit data).
    /// </summary>
    internal static void ApplyPlayerFitLowerGarments(MushroomZombie zombie)
    {
        if (zombie == null) return;
        try
        {
            Character player = ResolveZombieSourcePlayer(zombie);
            if (player == null) return;
            CustomizationRefs pRefs = player.refs?.customization?.refs;
            if (pRefs == null || pRefs.skirt == null || pRefs.shorts == null) return;
            bool skirtOn = pRefs.skirt.gameObject.activeSelf;
            bool shortsOn = pRefs.shorts.gameObject.activeSelf;

            Character zCharacter = zombie.GetComponent<Character>();
            CustomizationRefs zRefs = zCharacter != null ? zCharacter.refs?.customization?.refs : null;
            if (zRefs == null) return;
            if (zRefs.skirt != null) zRefs.skirt.gameObject.SetActive(skirtOn);
            if (zRefs.shorts != null) zRefs.shorts.gameObject.SetActive(shortsOn);
            if (zRefs.skirtShadow != null) zRefs.skirtShadow.gameObject.SetActive(skirtOn);
            if (zRefs.shortsShadow != null) zRefs.shortsShadow.gameObject.SetActive(shortsOn);

            // Keep the vanilla wearingSkirt bookkeeping in sync (OnPlayerEnteredRoom /
            // PushState read it when (re-)sending RPC_SetOutfit to late joiners).
            var mField = typeof(MushroomZombie).GetField("wearingSkirt", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mField != null) mField.SetValue(zombie, skirtOn && !shortsOn);
        }
        catch (Exception ex) { LogError("ApplyPlayerFitLowerGarments", ex); }
    }

    /// <summary>Resolves the player a controlled zombie was spawned from: the Photon
    /// instantiation data carries the source player's ViewID (index 1); offline-spawned zombies
    /// (local Instantiate, no instantiation data) fall back to the parked player on the owner
    /// client. Returns null when the source cannot be resolved.</summary>
    private static Character ResolveZombieSourcePlayer(MushroomZombie zombie)
    {
        try
        {
            PhotonView view = zombie.GetComponent<PhotonView>();
            object[] data = view != null ? view.InstantiationData : null;
            if (data != null && data.Length > 1 && data[1] is int srcViewId && srcViewId != 0)
            {
                PhotonView srcView = PhotonNetwork.GetPhotonView(srcViewId);
                Character c = srcView != null ? srcView.GetComponent<Character>() : null;
                if (c != null) return c;
            }
        }
        catch { }
        return ParkedPlayerCharacter;
    }

    private void DisableOriginalCameraControl()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        _disabledCameraScripts.Clear(); _cameraScriptStates.Clear();
        try
        {
            // MainCameraMovement controls the first-person camera; disable it and any other
            // camera-driving scripts on the camera or its parents.
            MonoBehaviour[] all = cam.GetComponentsInParent<MonoBehaviour>(true);
            foreach (MonoBehaviour mb in all)
            {
                if (mb == null || !mb.enabled) continue;
                if (mb is ZombieController) continue;
                string tn = mb.GetType().Name;
                // Skip non-camera controllers. The previous blacklist disabled almost every
                // MonoBehaviour on the camera's parent chain, which could also disable URP /
                // post-processing / volume / fog components (e.g. UniversalAdditionalCameraData,
                // PeakFilter's URP feature, Fog&ColdControl) and break scene lighting while in
                // zombie form. Protect anything rendering- or lighting-related.
                if (tn.Contains("Audio") || tn.Contains("Flare") || tn.Contains("Light") || tn.Contains("GUI") || tn.Contains("Canvas") || tn.Contains("AudioListener")
                    || tn.Contains("Universal") || tn.Contains("Render") || tn.Contains("Volume") || tn.Contains("PostProcess")
                    || tn.Contains("Fog") || tn.Contains("Skybox") || tn.Contains("Reflection") || tn.Contains("Shadow")
                    || tn.Contains("Culling") || tn.Contains("Probe") || tn.Contains("Filter")) continue;
                _disabledCameraScripts.Add(mb);
                _cameraScriptStates.Add(mb.enabled);
                mb.enabled = false;
            }
        }
        catch (Exception ex) { LogError("DisableOriginalCameraControl", ex); }
    }

    private void RestoreOriginalCameraControl()
    {
        for (int i = 0; i < _disabledCameraScripts.Count && i < _cameraScriptStates.Count; i++)
        {
            try { if (_disabledCameraScripts[i] != null) _disabledCameraScripts[i].enabled = _cameraScriptStates[i]; } catch { }
        }
        _disabledCameraScripts.Clear(); _cameraScriptStates.Clear();
    }

    /// <summary>Adds the exit camera blend to the main camera, starting from its current
    /// (third-person zombie) pose. Replaces any blend still running from a previous exit.</summary>
    private void StartExitCameraBlend()
    {
        try
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            StopExitCameraBlend();
            _exitCameraBlend = cam.gameObject.AddComponent<ExitCameraBlend>();
            _exitCameraBlend.Begin(cam.transform.position, cam.transform.rotation, ExitCameraBlendSeconds);
        }
        catch (Exception ex) { LogError("StartExitCameraBlend", ex); }
    }

    private void StopExitCameraBlend()
    {
        if (_exitCameraBlend != null) { try { Destroy(_exitCameraBlend); } catch { } }
        _exitCameraBlend = null;
    }

    // ===================================================================
    // HUD hide / restore
    // ===================================================================

    private void HideHud()
    {
        // Keep only the status bar while any transform form is active.
        // (global:: prevents binding to UnityEngine.Transform.)
        global::Transform.Core.TransformHud.TickHide();
    }

    private void RestoreHud()
    {
        global::Transform.Core.TransformHud.Restore();
    }

    // ===================================================================
    // Collision ignore (used by Harmony patches for networked zombie visuals)
    // ===================================================================

    internal static void IgnoreCollisionWithCharacter(GameObject zombieRoot, Character character)
    {
        if (zombieRoot == null || character == null || character.refs == null || character.refs.ragdoll == null) return;
        Collider[] zombieColliders = zombieRoot.GetComponentsInChildren<Collider>(true);
        if (zombieColliders.Length == 0) return;
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (part == null) continue;
            Collider[] partColliders = part.GetComponentsInChildren<Collider>(true);
            foreach (Collider zc in zombieColliders)
            {
                foreach (Collider pc in partColliders)
                {
                    if (zc != null && pc != null && zc.enabled && pc.enabled)
                    {
                        try { Physics.IgnoreCollision(zc, pc, true); } catch { }
                    }
                }
            }
        }
    }
}

/// <summary>
/// Temporary component on the main camera that eases the handover from the mod's third-person
/// zombie camera back to the vanilla first-person camera when the player reverts. MainCameraMovement
/// (execution order 500) clamps the camera to within 0.1 m of the head the moment it is re-enabled,
/// which reads as a hard one-frame cut; this component runs AFTER it (order 600) and blends from the
/// pose captured at exit toward whatever the vanilla script wrote this frame, then removes itself.
/// Because the target is read live every frame, the transition keeps tracking head movement and look
/// input while it eases in, and it converges exactly onto the vanilla camera (no residual offset).
/// </summary>
[DefaultExecutionOrder(600)]
internal sealed class ExitCameraBlend : MonoBehaviour
{
    private Vector3 _startPosition;
    private Quaternion _startRotation;
    private float _startTime = -1f;
    private float _duration;

    public void Begin(Vector3 position, Quaternion rotation, float duration)
    {
        _startPosition = position;
        _startRotation = rotation;
        _duration = Mathf.Max(duration, 0.05f);
        _startTime = Time.unscaledTime;
    }

    private void LateUpdate()
    {
        // Never begun, or the vanilla camera switched to spectate mid-blend (death / rocket) —
        // stop overriding it and hand control straight back to the game.
        if (_startTime < 0f || MainCameraMovement.IsSpectating) { Destroy(this); return; }
        float t = Mathf.Clamp01((Time.unscaledTime - _startTime) / _duration);
        float ease = t * t * (3f - 2f * t); // smoothstep — gentle start and end
        transform.SetPositionAndRotation(
            Vector3.Lerp(_startPosition, transform.position, ease),
            Quaternion.Slerp(_startRotation, transform.rotation, ease));
        if (t >= 1f) Destroy(this);
    }
}









