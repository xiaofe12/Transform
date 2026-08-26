using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ImCritter;
using ImGhost;
using ImScoutmaster;
using ImTornado;
using ImTumbleweed;
using ImZombie;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Transform;

/// <summary>
/// Unified entry point of the Transform mod. Hosts the five static form modules
/// (Zombie / Ghost / Tumbleweed / Tornado / Statue) and the Scoutmaster MonoBehaviour module on
/// one shared ConfigFile and log, drives their per-frame maintenance, owns the single
/// toggle key (hold ~1s to open/close the menu, short-press to transform/restore) and
/// renders the dual-page TransformMenu.
///
/// Each module keeps its own proven safety nets — endgame airport patches, scene-load
/// force-exit, death/pass-out RPC blocks — so the orchestrator stays thin and never patches the
/// same game methods twice.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class TransformPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.github.Thanks.Transform";
    public const string PluginName = "Transform";
    public const string PluginVersion = "0.9.8";

    internal static TransformPlugin Instance { get; private set; }
    internal static ManualLogSource Log { get; private set; }

    /// <summary>Single toggle key: hold ~1 second to open/close the menu, short-press to
    /// transform into the last form / restore the original form.</summary>
    internal ConfigEntry<KeyCode> MenuKey;

    /// <summary>UI 整体缩放（1~2，默认 1），标题栏下拉框实时调整。</summary>
    internal ConfigEntry<float> MenuScale;

    private const float DefaultMenuHoldSeconds = 1f;
    private const float ExternalCameraRecoverySeconds = 1.0f;
    private const float ExternalCameraRepairMaxDistance = 35f;
    private const float DefaultPlayerCameraFov = 70f;

    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo CharacterGetBodypartMethod = typeof(Character).GetMethod("GetBodypart", InstanceFlags);
    private static readonly PropertyInfo MainCameraSpecCharacterProperty = typeof(MainCameraMovement).GetProperty("specCharacter", StaticFlags);
    private static readonly FieldInfo MainCameraIsSpectatingField = typeof(MainCameraMovement).GetField("isSpectating", InstanceFlags);
    private static readonly FieldInfo MainCameraRagdollCamField = typeof(MainCameraMovement).GetField("ragdollCam", InstanceFlags);
    private static readonly FieldInfo MainCameraCurrentForwardOffsetField = typeof(MainCameraMovement).GetField("currentForwardOffset", InstanceFlags);
    private static readonly FieldInfo MainCameraTargetPlayerPovPositionField = typeof(MainCameraMovement).GetField("targetPlayerPovPosition", InstanceFlags);
    private static readonly FieldInfo MainCameraPhysicsRotField = typeof(MainCameraMovement).GetField("physicsRot", InstanceFlags);
    private static readonly MethodInfo FindObjectsOfTypeByTypeMethod = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });

    private Harmony _harmony;
    private GameObject _moduleHost;
    private ImScoutmaster.Plugin _scoutmasterModule;
    private float _menuKeyHoldStart = -1f;
    private bool _menuKeyHoldFired;
    private bool _enteringForm;
    private Core.FormId? _lastFormId;
    private Coroutine _postRestoreControlRoutine;
    private bool _externalCameraWasActive;
    private float _externalCameraRecoveryUntil;
    private int _externalCameraForceRepairFrames;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        MenuKey = Config.Bind("Menu", "MenuKey", KeyCode.F,
            "Single toggle key. Hold ~1 second to open/close the transform menu; short-press " +
            "while transformed to restore your original form, or re-enter the last form.");

        MenuScale = Config.Bind("Menu", "MenuScale", 1f,
            new ConfigDescription("UI menu overall scale (1-2).",
                new AcceptableValueRange<float>(1f, 2f)));

        Core.Localization.Initialize(Logger);

        // 第三方自由相机模组检测（PeakSpectatorMode / PeakCinema）：激活期间各形态相机让路。
        Core.ThirdPartyCameras.Initialize(Logger);

        // 房主选项（房间内变身效果）：三档策略 + Photon 房间属性同步 + 配置持久化。
        RunGuarded("room policy", () => Core.RoomPolicy.Initialize(Config, Logger));

        // Unified game-wide guards while any form is active: item using/switching block for the
        // local character and the backpack-wheel block (the reticle guard lives in the zombie
        // module's patch and is gated on the same TransformState.AnyFormActive flag).
        // A game update that removes one patch target must degrade that guard only — never
        // abort Awake (one such abort left every form dead until the patch was fixed).
        _harmony = new Harmony(PluginGuid);
        RunGuarded("core patches", () => _harmony.PatchAll(typeof(Core.TransformHarmonyPatches)));
        RunGuarded("canEmote guard", () => Core.TransformHarmonyPatches.InstallCanEmoteGuard(_harmony));
        RunGuarded("emote wheel guard", () => Core.TransformHarmonyPatches.InstallEmoteWheelGuard(_harmony));
        RunGuarded("PeakCinema NRE guard", () => Core.TransformHarmonyPatches.InstallPeakCinemaGuard(_harmony));

        // Static modules — each binds its own prefixed config sections and installs its own
        // Harmony patches (including the endgame/scene-safety nets they were proven with).
        RunGuarded("zombie module", () => ZombiePlugin.Initialize(Config, Logger));
        RunGuarded("wind module", () => WindPlugin.Initialize(Config, Logger));
        RunGuarded("ghost module", () => GhostPlugin.Initialize(Config, Logger));
        RunGuarded("tumbleweed module", () => TumbleweedPlugin.Initialize(Config, Logger));
        RunGuarded("statue module", () => Statue.StatuePlugin.Initialize(Config, Logger));
        RunGuarded("critter module", () => CritterPlugin.Initialize(Config, Logger));

        // Scoutmaster keeps its MonoBehaviour lifecycle: a persistent host object carries the
        // module across scene loads so its Photon callbacks survive.
        _moduleHost = new GameObject("Transform.ScoutmasterModule");
        DontDestroyOnLoad(_moduleHost);
        _scoutmasterModule = _moduleHost.AddComponent<ImScoutmaster.Plugin>();
        _scoutmasterModule.InitializeModule(Config, Logger);

        Core.TransformMenu.Initialize(Logger, MenuScale);

        // 场景卸载时清空本地形态状态，防止重进大厅后短按 F 误触上次变身。
        SceneManager.sceneUnloaded += OnSceneUnloaded;

        Logger.LogInfo("[Transform] Unified mod loaded: normal zombie, player zombie, mushroom-man zombie, scoutmaster, ghost, tumbleweed, tornado, statue, frog, beetle, scorpion, coconut and bomb forms.");
        Logger.LogInfo("[Transform] Hold " + MenuKey.Value + " for ~1s to open/close the menu; tap it to transform/restore.");
    }

    /// <summary>场景卸载时清除本地形态状态：上次形态 + 菜单选中/双击状态 + 关闭菜单。</summary>
    private static void OnSceneUnloaded(Scene scene)
    {
        if (Instance == null)
        {
            return;
        }
        Instance._lastFormId = null;
        Core.TransformMenu.ClearSelection();
        Core.TransformMenu.SetOpen(false);
    }

    private void Update()
    {
        Core.Localization.Tick();
        Core.ThirdPartyCameras.Tick();

        // Mirror the form state for the game-wide Harmony guards (reticle, item bar, backpack
        // wheel) — see Core.TransformState. One frame of lag at enter/exit is harmless.
        TransformState.AnyFormActive = Core.FormRegistry.AnyActive;
        TickExternalCameraRecovery();

        // 房主选项维护：刷新生效策略（房间属性同步）并在策略收紧时强制还原当前形态。
        Core.RoomPolicy.Tick();

        ZombiePlugin.Tick();
        WindPlugin.Tick();
        GhostPlugin.Tick();
        TumbleweedPlugin.Tick();
        Statue.StatuePlugin.Tick();
        CritterPlugin.Tick();
        // The Scoutmaster module self-ticks through its own MonoBehaviour Update.

        HandleToggleKey();
    }

    private void OnGUI()
    {
        Core.TransformMenu.OnGUI();
    }

    /// <summary>
    /// Single toggle-key state machine (default F): holding the key for the configured duration
    /// toggles the menu; a short press closes an open menu, restores the original form while
    /// transformed, or re-enters the last form otherwise.
    /// </summary>
    private void HandleToggleKey()
    {
        try
        {
            if (MenuKey == null || MenuKey.Value == KeyCode.None) return;

            if (Input.GetKeyDown(MenuKey.Value))
            {
                _menuKeyHoldStart = Time.unscaledTime;
                _menuKeyHoldFired = false;
            }

            if (!_menuKeyHoldFired
                && _menuKeyHoldStart >= 0f
                && Input.GetKey(MenuKey.Value)
                && Time.unscaledTime - _menuKeyHoldStart >= DefaultMenuHoldSeconds)
            {
                _menuKeyHoldFired = true;
                // 主菜单（无本地角色）禁止打开面板；面板已开时仍可关闭。
                if (!Core.TransformMenu.IsOpen && Character.localCharacter == null)
                {
                    return;
                }
                Core.TransformMenu.SetOpen(!Core.TransformMenu.IsOpen);
            }

            if (Input.GetKeyUp(MenuKey.Value))
            {
                float held = _menuKeyHoldStart >= 0f ? Time.unscaledTime - _menuKeyHoldStart : 0f;
                _menuKeyHoldStart = -1f;
                bool shortPress = !_menuKeyHoldFired && held > 0f;
                _menuKeyHoldFired = false;

                if (!shortPress) return;

                // 菜单打开时：选中形态优先（选中=当前 → 还原；否则变身/切换）。
                if (Core.TransformMenu.IsOpen)
                {
                    Core.FormRegistry.FormDescriptor sel = Core.TransformMenu.GetSelectedForm();
                    if (sel != null)
                    {
                        Core.FormRegistry.FormDescriptor current = Core.FormRegistry.ActiveForm;
                        if (current == sel)
                        {
                            // 选中形态就是当前形态 → 恢复原形。
                            Core.TransformMenu.SetOpen(false);
                            RequestRestore();
                            return;
                        }
                        // 可进入，或当前有激活形态且目标不是它（切换：EnterFormRoutine 会先退出）。
                        if (Core.RoomPolicy.CanUseForm(sel.Id) && (sel.CanEnter() || CanSwitchTo(sel)))
                        {
                            Core.TransformMenu.SetOpen(false);
                            RequestEnterForm(sel.Id);
                            return;
                        }
                    }
                    // 无选中形态或选中形态不能进入 → 仅关闭菜单。
                    Core.TransformMenu.SetOpen(false);
                    return;
                }

                // 菜单已关但未变身：单击选中过的形态优先于"上次变身形态"——即
                // "单击形态卡 → 退出菜单 → 按 F 变身"同样生效（选中在关菜单后保留）。
                if (!Core.FormRegistry.AnyActive)
                {
                    Core.FormRegistry.FormDescriptor selected = Core.TransformMenu.GetSelectedForm();
                    if (selected != null && Core.RoomPolicy.CanUseForm(selected.Id) && selected.CanEnter())
                    {
                        RequestEnterForm(selected.Id);
                        return;
                    }
                }

                if (_enteringForm) return;
                // Don't transform on top of the game's own blocking UI (pause menu, backpack wheel).
                if (GUIManager.instance != null && GUIManager.instance.windowBlockingInput) return;

                if (Core.FormRegistry.AnyActive)
                {
                    RequestRestore();
                }
                else if (_lastFormId.HasValue)
                {
                    RequestEnterForm(_lastFormId.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError("[Transform] Toggle key handling failed:\n" + ex);
        }
    }

    /// <summary>True when the given form is a valid SWITCH target: some form is active and it
    /// is not the target. EnterFormRoutine exits the active form before entering the target,
    /// so the per-module "another special form" guards must NOT block the entry request.</summary>
    private bool CanSwitchTo(Core.FormRegistry.FormDescriptor target)
    {
        Core.FormRegistry.FormDescriptor active = Core.FormRegistry.ActiveForm;
        return active != null && active != target;
    }

    /// <summary>Enters the requested form, exiting the active one first when switching.</summary>
    internal void RequestEnterForm(Core.FormId formId)
    {
        if (_enteringForm) return;
        // 房主选项硬拦截：全局策略 + 单形态开关都在这里兜住所有程序化入口。
        if (!Core.RoomPolicy.CanUseForm(formId))
        {
            Log.LogWarning("[Transform] 房间策略禁止变身: " + Core.RoomPolicy.Current + ", form=" + formId);
            return;
        }
        StartCoroutine(EnterFormRoutine(formId));
    }

    private IEnumerator EnterFormRoutine(Core.FormId formId)
    {
        _enteringForm = true;
        try
        {
            if (Core.FormRegistry.AnyActive)
            {
                Log.LogInfo("[Transform] Switching forms — exiting the active form first.");
                Core.FormRegistry.ExitActiveForm();
                StabilizeLocalCharacterForFormSwitch("after exit");
                // Two frames for the exit to fully restore Character.localCharacter before the
                // next form validates the character state (some exits restore over LateUpdate).
                // Clear velocity across those frames so the next form does not inherit a leap,
                // hop, throw, or dash impulse and launch the player upward while switching.
                yield return null;
                StabilizeLocalCharacterForFormSwitch("after first exit frame");
                yield return null;
                StabilizeLocalCharacterForFormSwitch("before enter");
            }

            Core.FormRegistry.FormDescriptor target = null;
            foreach (Core.FormRegistry.FormDescriptor form in Core.FormRegistry.Forms)
            {
                if (form.Id == formId) { target = form; break; }
            }
            if (target == null)
            {
                Log.LogWarning("[Transform] Unknown form id: " + formId);
                yield break;
            }

            bool entered = false;
            try
            {
                Core.CarryGuard.DropBeforeTransform(Character.localCharacter, Log);
                entered = target.Enter();
            }
            catch (Exception ex) { Log.LogError("[Transform] Failed to enter " + target.Name + ":\n" + ex); }

            if (entered)
            {
                _lastFormId = target.Id;
                Log.LogInfo("[Transform] Entered form: " + target.Name);
            }
            else
            {
                Log.LogWarning("[Transform] Could not enter form: " + target.Name);
            }
        }
        finally
        {
            _enteringForm = false;
        }
    }

    private void StabilizeLocalCharacterForFormSwitch(string phase)
    {
        try
        {
            Character character = Character.localCharacter;
            if (character == null) return;

            if (character.data != null)
            {
                character.data.avarageVelocity = Vector3.zero;
                character.data.avarageLastFrameVelocity = Vector3.zero;
                character.data.worldMovementInput = Vector3.zero;
                character.data.worldMovementInput_Grounded = Vector3.zero;
                character.data.fallSeconds = 0f;
                character.data.passedOut = false;
                character.data.fullyPassedOut = false;
            }

            CharacterRagdoll ragdoll = character.refs != null ? character.refs.ragdoll : null;
            if (ragdoll == null) return;

            try { ragdoll.HaltBodyVelocity(false); } catch { /* individual bodyparts are cleared below */ }

            if (ragdoll.partList == null) return;
            foreach (Bodypart part in ragdoll.partList)
            {
                Rigidbody rig = part != null ? part.Rig : null;
                if (rig == null || rig.isKinematic) continue;
                rig.linearVelocity = Vector3.zero;
                rig.angularVelocity = Vector3.zero;
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning("[Transform] Form switch velocity stabilization failed (" + phase + "): " + ex.Message);
        }
    }

    /// <summary>Restores the player's original form.</summary>
    internal void RequestRestore()
    {
        if (!Core.FormRegistry.ExitActiveForm())
        {
            Log.LogWarning("[Transform] Restore requested but no form is active.");
        }
        else
        {
            Log.LogInfo("[Transform] Restored original form.");
            BeginPostRestoreRecovery("restore request");
        }
    }

    internal void BeginPostRestoreRecovery(string reason)
    {
        try
        {
            if (_postRestoreControlRoutine != null)
            {
                StopCoroutine(_postRestoreControlRoutine);
            }
            RestoreLocalPlayerControlState(reason + " immediate");
            BeginExternalCameraRecovery();
            RepairLocalPlayerCamera(forceSnap: true);
            _postRestoreControlRoutine = StartCoroutine(PostRestoreControlRoutine());
        }
        catch (Exception ex)
        {
            Log?.LogWarning("[Transform] Post-restore recovery failed (" + reason + "): " + ex.Message);
        }
    }

    private IEnumerator PostRestoreControlRoutine()
    {
        RestoreLocalPlayerControlState("restore immediate");
        yield return null;
        RestoreLocalPlayerControlState("restore next frame");
        yield return null;
        RestoreLocalPlayerControlState("restore second frame");
        _postRestoreControlRoutine = null;
    }

    private void RestoreLocalPlayerControlState(string phase)
    {
        try
        {
            Character character = Character.localCharacter;
            if (character == null || Core.FormRegistry.AnyActive) return;

            if (!character.gameObject.activeSelf)
            {
                character.gameObject.SetActive(true);
            }

            CharacterMovement movement = character.GetComponent<CharacterMovement>();
            if (movement != null && !movement.enabled)
            {
                movement.enabled = true;
            }

            CharacterInput inputComponent = character.GetComponent<CharacterInput>();
            if (inputComponent != null && !inputComponent.enabled)
            {
                inputComponent.enabled = true;
            }

            CharacterSyncer syncer = character.GetComponent<CharacterSyncer>();
            if (syncer != null && !syncer.enabled)
            {
                syncer.enabled = true;
            }

            Photon.Pun.PhotonView view = character.photonView;
            if (view != null && !view.enabled)
            {
                view.enabled = true;
            }

            if (character.input != null)
            {
                character.input.movementInput = Vector2.zero;
                character.input.lookInput = Vector2.zero;
                character.input.jumpWasPressed = false;
                character.input.jumpIsPressed = false;
                character.input.sprintIsPressed = false;
                character.input.sprintWasPressed = false;
                character.input.sprintToggleWasPressed = false;
                character.input.usePrimaryWasPressed = false;
                character.input.usePrimaryIsPressed = false;
                character.input.useSecondaryWasPressed = false;
                character.input.useSecondaryIsPressed = false;
                character.input.crouchWasPressed = false;
                character.input.crouchIsPressed = false;
                character.input.crouchToggleWasPressed = false;
                character.input.interactWasPressed = false;
                character.input.interactIsPressed = false;
                character.input.dropWasPressed = false;
                character.input.dropIsPressed = false;
            }

            if (character.data != null)
            {
                character.data.dead = false;
                character.data.zombified = false;
                character.data.passedOut = false;
                character.data.fullyPassedOut = false;
                character.data.fallSeconds = 0f;
                character.data.deathTimer = 0f;
                character.data.currentRagdollControll = 1f;
                character.data.isSprinting = false;
                character.data.isCrouching = false;
                character.data.isJumping = false;
                character.data.isClimbing = false;
                character.data.isRopeClimbing = false;
                character.data.isVineClimbing = false;
                character.data.isReaching = false;
                character.data.avarageVelocity = Vector3.zero;
                character.data.avarageLastFrameVelocity = Vector3.zero;
                character.data.worldMovementInput = Vector3.zero;
                character.data.worldMovementInput_Grounded = Vector3.zero;
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning("[Transform] Post-restore control repair failed (" + phase + "): " + ex.Message);
        }
    }

    private void TickExternalCameraRecovery()
    {
        bool externalActive = Core.ThirdPartyCameras.ExternalCameraActive;
        if (_externalCameraWasActive && !externalActive)
        {
            BeginExternalCameraRecovery();
        }
        _externalCameraWasActive = externalActive;

        if (externalActive || Time.unscaledTime > _externalCameraRecoveryUntil)
        {
            return;
        }

        bool forceSnap = _externalCameraForceRepairFrames > 0;
        if (_externalCameraForceRepairFrames > 0)
        {
            _externalCameraForceRepairFrames--;
        }
        RepairLocalPlayerCamera(forceSnap);
    }

    private void BeginExternalCameraRecovery()
    {
        _externalCameraRecoveryUntil = Time.unscaledTime + ExternalCameraRecoverySeconds;
        _externalCameraForceRepairFrames = 2;
    }

    private void RepairLocalPlayerCamera(bool forceSnap)
    {
        try
        {
            if (Core.FormRegistry.AnyActive) return;
            Character character = Character.localCharacter;
            Camera camera = Camera.main;
            if (character == null || character.data == null || camera == null) return;

            Vector3 cameraPosition = ResolvePlayerCameraPosition(character);
            Quaternion cameraRotation = ResolvePlayerCameraRotation(character);
            if (!IsFiniteVector(cameraPosition) || !IsFiniteQuaternion(cameraRotation)) return;

            ResetMainCameraMovementState(cameraPosition, cameraRotation);

            bool invalidCamera = !IsFiniteVector(camera.transform.position)
                || !IsFiniteQuaternion(camera.transform.rotation)
                || float.IsNaN(camera.fieldOfView)
                || float.IsInfinity(camera.fieldOfView)
                || camera.fieldOfView < 1f
                || camera.fieldOfView > 179f;
            bool tooFar = Vector3.Distance(camera.transform.position, cameraPosition) > ExternalCameraRepairMaxDistance;

            if (forceSnap || invalidCamera || tooFar)
            {
                camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
                if (float.IsNaN(camera.fieldOfView) || float.IsInfinity(camera.fieldOfView) || camera.fieldOfView < 1f || camera.fieldOfView > 179f)
                {
                    camera.fieldOfView = DefaultPlayerCameraFov;
                }
            }
        }
        catch (Exception ex)
        {
            Log?.LogWarning("[Transform] External camera recovery failed: " + ex.Message);
        }
    }

    private static Vector3 ResolvePlayerCameraPosition(Character character)
    {
        UnityEngine.Transform head = ResolveHeadTransform(character);
        if (head != null)
        {
            Vector3 headPosition = head.TransformPoint(Vector3.up);
            if (IsFiniteVector(headPosition)) return headPosition;
        }

        try
        {
            Vector3 characterHead = character.Head;
            if (IsFiniteVector(characterHead)) return characterHead;
        }
        catch { }

        Vector3 position = character != null ? character.transform.position : Vector3.zero;
        return IsFiniteVector(position) ? position + Vector3.up : Vector3.zero;
    }

    private static UnityEngine.Transform ResolveHeadTransform(Character character)
    {
        if (character == null) return null;

        try
        {
            if (character.refs?.head != null)
            {
                return character.refs.head.transform;
            }

            Bodypart head = GetBodypart(character, BodypartType.Head);
            return head != null ? head.transform : null;
        }
        catch
        {
            return null;
        }
    }

    private static Bodypart GetBodypart(Character character, BodypartType bodypartType)
    {
        if (character == null || CharacterGetBodypartMethod == null) return null;

        try
        {
            return CharacterGetBodypartMethod.Invoke(character, new object[] { bodypartType }) as Bodypart;
        }
        catch
        {
            return null;
        }
    }

    private static void ResetMainCameraMovementState(Vector3 cameraPosition, Quaternion cameraRotation)
    {
        try
        {
            MainCameraSpecCharacterProperty?.SetValue(null, null, null);
        }
        catch
        {
        }

        try
        {
            UnityEngine.Object[] cameraMovements = FindObjectsOfTypeByTypeMethod?.Invoke(null, new object[] { typeof(MainCameraMovement) }) as UnityEngine.Object[];
            if (cameraMovements == null) return;

            foreach (UnityEngine.Object movement in cameraMovements)
            {
                if (movement == null) continue;

                MainCameraIsSpectatingField?.SetValue(movement, false);
                MainCameraRagdollCamField?.SetValue(movement, 0f);
                MainCameraCurrentForwardOffsetField?.SetValue(movement, 0f);
                MainCameraTargetPlayerPovPositionField?.SetValue(movement, cameraPosition);
                MainCameraPhysicsRotField?.SetValue(movement, cameraRotation);
            }
        }
        catch
        {
        }
    }

    private static Quaternion ResolvePlayerCameraRotation(Character character)
    {
        Vector3 direction = character != null && character.data != null ? character.data.lookDirection : Vector3.zero;
        if (!IsUsableDirection(direction) && character != null && character.data != null)
        {
            direction = character.data.lookDirection_Flat;
        }
        if (!IsUsableDirection(direction) && character != null)
        {
            direction = character.transform.forward;
        }
        if (!IsUsableDirection(direction) && Camera.main != null)
        {
            direction = Camera.main.transform.forward;
        }
        if (!IsUsableDirection(direction))
        {
            direction = Vector3.forward;
        }

        return Quaternion.LookRotation(direction.normalized, Vector3.up);
    }

    private static bool IsUsableDirection(Vector3 value)
    {
        return IsFiniteVector(value) && value.sqrMagnitude >= 0.0001f;
    }

    private static bool IsFiniteVector(Vector3 value)
    {
        return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z);
    }

    private static bool IsFiniteQuaternion(Quaternion value)
    {
        return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z) && IsFiniteFloat(value.w);
    }

    private static bool IsFiniteFloat(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        Core.TransformMenu.SetOpen(false);

        Statue.StatuePlugin.Shutdown();
        CritterPlugin.Shutdown();
        TumbleweedPlugin.Shutdown();
        GhostPlugin.Shutdown();
        WindPlugin.Shutdown();
        ZombiePlugin.Shutdown();
        // The Scoutmaster module cleans itself up in its own OnDestroy (host object teardown).

        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Runs one Awake initialization step, isolating its failure: a game update that breaks a
    /// single patch target or module logs an error and degrades that one feature instead of
    /// aborting the whole plugin (which would kill the menu key and every form).
    /// </summary>
    private void RunGuarded(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.LogError("[Transform] " + stepName + " failed (feature degraded, mod keeps loading):\n" + ex);
        }
    }
}

