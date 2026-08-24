using System;
using System.Reflection;
using HarmonyLib;
using Peak;
using UnityEngine;

namespace ImZombie;

/// <summary>
/// Compatibility guard for third-party status-bar mods (PeakStats, PeakStamina/peakstamina by
/// lstrings, PlayersInfo). These mods build stamina bars for every Character in
/// Character.AllCharacters and read <c>observedCharacter.refs.customization.PlayerColor</c> /
/// <c>localCharacter.refs.stats</c> per frame. While the Transform mod is active the local
/// character can be swapped to the controlled zombie (an NPC-prefab Character whose
/// refs.customization may be null) and remote transformed players appear as NPC characters too —
/// the status mods then throw NullReferenceException every frame.
///
/// The guard patches each mod's per-frame methods with prefixes that skip processing for
/// characters whose refs would NRE (customization/stats missing) or that belong to our forms
/// (active zombie / parked player body). It also blocks CharacterStaminaBar.AnimateEnable/
/// AnimateDisable while a Transform form has the HUD deactivated — the mod's StartCoroutine
/// on the inactive "Bar(Clone)" otherwise errors every frame. Types are resolved lazily via
/// reflection because the mods may load after us; ZombiePlugin.Tick retries the install
/// every couple of seconds.
///
/// PlayersInfo wraps all of its Update bodies in try/catch with throttled error logging, so it
/// cannot crash — we still guard TeammateBarDriver.Update to cut its error spam while a
/// transformed player is on screen.
/// </summary>
internal static class StatusBarGuard
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Namespace prefixes of every known build of the two stamina-bar mods.
    private static readonly string[] BarModNamespaces =
    {
        "PeakStats.MonoBehaviours", // PeakStats.dll (current)
        "PeakStatsEx.MonoBehaviours", // PeakStats.dll (older build)
        "PeakStamina", // com.lstrings.peak.peakstamina.dll
    };

    private static bool _installed;
    private static bool _loggedNotResolved;
    private static readonly System.Collections.Generic.List<string> _installedMods =
        new System.Collections.Generic.List<string>();
    private static readonly System.Collections.Generic.HashSet<string> _loggedPrefixFailures =
        new System.Collections.Generic.HashSet<string>();

    private static void LogPrefixFailureOnce(string key, Exception ex)
    {
        if (_loggedPrefixFailures.Add(key))
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] Status-bar guard " + key + " skipped once: " + ex.Message);
    }

    public static bool IsInstalled => _installed;

    public static void Install(Harmony harmony)
    {
        if (_installed) return;
        try
        {
            _installedMods.Clear();
            foreach (string ns in BarModNamespaces)
            {
                TryInstallBarMod(harmony, ns);
            }
            TryInstallLocalStatsOverlays(harmony);
            TryInstallStaminaInfoPatch(harmony, FindLoadedType("PeakStatsEx.StaminaInfoPatch"));
            TryInstallPlayersInfo(harmony);

            if (_installedMods.Count > 0)
            {
                _installed = true;
                ZombiePlugin.Log?.LogInfo("[I'm a Zombie] Installed status-bar compatibility guard for: " +
                    string.Join(", ", _installedMods) + ".");
            }
            else if (!_loggedNotResolved)
            {
                // None of the supported mods are loaded (yet). ZombiePlugin.Tick retries every
                // 2 s but stops after a while — log the miss only once to avoid log spam.
                _loggedNotResolved = true;
                ZombiePlugin.Log?.LogInfo("[I'm a Zombie] Status-bar mods not loaded (PeakStats/PeakStamina/" +
                    "PlayersInfo types not resolved); compatibility guard idle.");
            }
        }
        catch (Exception ex)
        {
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] Failed to install status-bar compatibility guard: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // PeakStats / PeakStamina (same structure, different namespaces)
    // ------------------------------------------------------------------

    private static void TryInstallBarMod(Harmony harmony, string ns)
    {
        try
        {
            Type managerType = FindLoadedType(ns + ".ProximityStaminaManager");
            Type barType = FindLoadedType(ns + ".CharacterStaminaBar");
            Type afflictionType = FindLoadedType(ns + ".CharacterBarAffliction");
            if (managerType == null || barType == null)
            {
                return; // this mod is not installed
            }

            int patched = 0;

            // Manager.Update — shield the localCharacter/spectate reads. The mod's own null
            // checks vary between builds, so we re-check here; a valid local character (even
            // our zombie) is allowed through so teammate bars keep working.
            MethodInfo managerUpdate = FindMethodNoArgs(managerType, "Update");
            if (managerUpdate != null)
            {
                harmony.Patch(managerUpdate, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(ManagerUpdatePrefix)));
                patched++;
            }

            // Manager.CreateStaminaBar(Character) — never build a bar for characters whose
            // refs would NRE downstream (NPC-prefab characters, e.g. mushroom zombies).
            MethodInfo createBar = FindMethodOneArg(managerType, "CreateStaminaBar");
            if (createBar != null)
            {
                harmony.Patch(createBar, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(CreateStaminaBarPrefix)));
                patched++;
            }

            // CharacterStaminaBar.Update — THE per-frame NRE: reads
            // observedCharacter.refs.customization.PlayerColor without a null check.
            MethodInfo barUpdate = FindMethodNoArgs(barType, "Update");
            FieldInfo observedField = FindObservedCharacterField(barType);
            if (barUpdate != null && observedField != null)
            {
                _observedFields.RemoveAll(kv => kv.Key == barType);
                _observedFields.Add(new System.Collections.Generic.KeyValuePair<Type, FieldInfo>(barType, observedField));
                harmony.Patch(barUpdate, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(StaminaBarUpdatePrefix)));
                patched++;
            }

            // CharacterStaminaBar.AnimateEnable / AnimateDisable — the coroutine error:
            // the mod parents its "Bar(Clone)" under the vanilla stamina bar's parent, and
            // while a Transform form hides the full HUD (TransformHud.TickHide(false)) that
            // ancestor is deactivated, so StartCoroutine on the inactive-in-hierarchy bar
            // throws "Coroutine couldn't be started because the game object 'Bar(Clone)'
            // is inactive!" every frame. Skip both while the bar is not visible; the
            // manager calls them every frame, so they self-heal once the HUD is restored.
            foreach (string name in new[] { "AnimateEnable", "AnimateDisable" })
            {
                MethodInfo animate = FindMethodNoArgs(barType, name);
                if (animate != null)
                {
                    harmony.Patch(animate, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(AnimateBarPrefix)));
                    patched++;
                }
            }

            // CharacterBarAffliction.FetchReferences / FetchDesiredSize — called while the
            // bar initializes and updates; same observed-character shield.
            if (afflictionType != null)
            {
                FieldInfo barField = afflictionType.GetField("characterStaminaBar", InstanceFlags);
                if (barField != null)
                {
                    _afflictionBarFields.RemoveAll(kv => kv.Key == afflictionType);
                    _afflictionBarFields.Add(new System.Collections.Generic.KeyValuePair<Type, FieldInfo>(afflictionType, barField));
                    foreach (string name in new[] { "FetchReferences", "FetchDesiredSize" })
                    {
                        MethodInfo m = FindMethodNoArgs(afflictionType, name);
                        if (m != null)
                        {
                            harmony.Patch(m, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(AfflictionMethodPrefix)));
                            patched++;
                        }
                    }
                }
            }

            if (patched > 0)
            {
                _installedMods.Add(ns + " (" + patched + " patches)");
            }
        }
        catch (Exception ex)
        {
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] Status-bar guard for " + ns + " failed: " + ex.Message);
        }
    }

    private static void TryInstallLocalStatsOverlays(Harmony harmony)
    {
        TryInstallTimerHeightStats(harmony, FindLoadedType("PeakStats.MonoBehaviours.TimerHeightStats"));
        TryInstallTimerHeightStats(harmony, FindLoadedType("PeakStatsEx.MonoBehaviours.TimerHeightStats"));
        TryInstallMapStats(harmony, FindLoadedType("PeakStatsEx.MonoBehaviours.MapStats"));
    }

    /// <summary>PeakStats/PeakStatsEx local overlays read local/observed character refs and
    /// stats with incomplete null checks — skip while a Transform form has swapped the local
    /// display character to an NPC/player proxy.</summary>
    private static void TryInstallTimerHeightStats(Harmony harmony, Type timerType)
    {
        if (timerType == null) return; // PeakStats build without the overlay
        try
        {
            MethodInfo update = FindMethodNoArgs(timerType, "Update");
            if (update == null) return;
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(TimerHeightStatsUpdatePrefix)));
            _installedMods.Add(timerType.FullName);
        }
        catch (Exception ex)
        {
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] TimerHeightStats guard failed: " + ex.Message);
        }
    }

    private static void TryInstallMapStats(Harmony harmony, Type mapStatsType)
    {
        if (mapStatsType == null) return;
        try
        {
            MethodInfo update = FindMethodNoArgs(mapStatsType, "Update");
            if (update == null) return;
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(MapStatsUpdatePrefix)));
            _installedMods.Add(mapStatsType.FullName);
        }
        catch (Exception ex)
        {
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] MapStats guard failed: " + ex.Message);
        }
    }

    private static void TryInstallStaminaInfoPatch(Harmony harmony, Type staminaInfoPatchType)
    {
        if (staminaInfoPatchType == null) return;
        try
        {
            int patched = 0;
            foreach (MethodInfo method in staminaInfoPatchType.GetMethods(InstanceFlags | BindingFlags.Static | BindingFlags.Public))
            {
                if (method.Name == "Update" && method.GetParameters().Length == 1)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(CharacterBarArgPrefix)));
                    patched++;
                }
                else if (method.Name == "StaminaInfoStaminaBarUpdate" && method.GetParameters().Length == 1)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(LocalStaminaBarArgPrefix)));
                    patched++;
                }
            }
            if (patched > 0) _installedMods.Add(staminaInfoPatchType.FullName + " (" + patched + " patches)");
        }
        catch (Exception ex)
        {
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] StaminaInfoPatch guard failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // PlayersInfo (self-defending, we only cut the error spam)
    // ------------------------------------------------------------------

    private static void TryInstallPlayersInfo(Harmony harmony)
    {
        try
        {
            int patched = 0;

            Type driverType = FindLoadedType("PlayersInfo.MonoBehaviours.TeammateBarDriver");
            if (driverType != null)
            {
                RegisterPlayersInfoTargetField(driverType);
                foreach (string methodName in new[] { "Update", "DoUpdate", "UpdateValueTexts" })
                {
                    MethodInfo method = FindMethodNoArgs(driverType, methodName);
                    if (method == null) continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(PlayersInfoTargetPrefix)));
                    patched++;
                }
            }

            Type inventoryRowType = FindLoadedType("PlayersInfo.MonoBehaviours.TeammateInventoryRow");
            if (inventoryRowType != null)
            {
                RegisterPlayersInfoTargetField(inventoryRowType);
                MethodInfo lateUpdate = FindMethodNoArgs(inventoryRowType, "LateUpdate");
                if (lateUpdate != null)
                {
                    harmony.Patch(lateUpdate, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(PlayersInfoTargetPrefix)));
                    patched++;
                }
            }

            Type coordinatorType = FindLoadedType("PlayersInfo.MonoBehaviours.TeammateBarsCoordinator");
            if (coordinatorType != null)
            {
                foreach (string methodName in new[] { "Update", "CreateBar" })
                {
                    MethodInfo method = FindMethodNoArgs(coordinatorType, methodName);
                    if (method == null) continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(PlayersInfoGlobalPrefix)));
                    patched++;
                }
            }

            Type rosterType = FindLoadedType("PlayersInfo.Helpers.TeamRosterTracker");
            if (rosterType != null)
            {
                MethodInfo update = FindMethodNoArgs(rosterType, "Update");
                if (update != null)
                {
                    harmony.Patch(update, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(PlayersInfoGlobalPrefix)));
                    patched++;
                }
            }

            Type localPatchType = FindLoadedType("PlayersInfo.Patches.LocalStaminaBarPatch");
            if (localPatchType != null)
            {
                MethodInfo hunger = FindMethodWithCharacterArg(localPatchType, "UpdateHungerCountdown");
                if (hunger != null)
                {
                    harmony.Patch(hunger, prefix: new HarmonyMethod(typeof(StatusBarGuard), nameof(CharacterArgPrefix)));
                    patched++;
                }
            }

            if (patched > 0) _installedMods.Add("PlayersInfo (" + patched + " patches)");
        }
        catch (Exception ex)
        {
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] PlayersInfo guard failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Patch prefixes
    // ------------------------------------------------------------------

    private static bool ManagerUpdatePrefix()
    {
        Character local = Character.localCharacter;
        if (local == null) return false;
        if (MainCameraMovement.IsSpectating && MainCameraMovement.specCharacter == null) return false;
        if (local.refs == null) return false;
        return true;
    }

    private static bool CreateStaminaBarPrefix(object[] __args)
    {
        for (int i = 0; i < __args.Length; i++)
        {
            if (__args[i] is Character c && (ShouldSkipCharacter(c) || HasBrokenRefs(c))) return false;
        }
        return true;
    }

    private static bool CharacterArgPrefix(object[] __args)
    {
        for (int i = 0; i < __args.Length; i++)
        {
            if (__args[i] is Character c && (ShouldSkipCharacter(c) || HasBrokenRefs(c))) return false;
        }
        return true;
    }

    private static bool CharacterBarArgPrefix(object[] __args)
    {
        for (int i = 0; i < __args.Length; i++)
        {
            Character observed = GetObservedCharacter(__args[i]);
            if (observed != null && (ShouldSkipCharacter(observed) || HasBrokenRefs(observed))) return false;
        }
        return true;
    }

    private static bool LocalStaminaBarArgPrefix()
    {
        return ShouldAllowLocalStatsOverlay();
    }

    private static bool StaminaBarUpdatePrefix(object __instance)
    {
        Character observed = GetObservedCharacter(__instance);
        if (observed == null) return true; // their own null handling destroys the bar
        if (ShouldSkipCharacter(observed) || HasBrokenRefs(observed)) return false;
        return true;
    }

    /// <summary>Blocks AnimateEnable/AnimateDisable while their bar cannot run coroutines:
    /// with a Transform form active the HUD ancestor is deactivated, so the mod's
    /// StartCoroutine would throw ("Bar(Clone) is inactive"). Skipping is safe because
    /// ProximityStaminaManager.Update re-invokes both every frame — normal behaviour
    /// resumes as soon as the form exits and the HUD is visible again.</summary>
    private static bool AnimateBarPrefix(object __instance)
    {
        if (!global::TransformState.AnyFormActive) return true;
        if (__instance is Component component)
        {
            GameObject go = component.gameObject;
            if (!go.activeSelf || !go.activeInHierarchy) return false;
        }
        return true;
    }

    private static bool AfflictionMethodPrefix(object __instance)
    {
        if (__instance == null) return true;
        FieldInfo barField = ResolveAfflictionBarField(__instance.GetType());
        if (barField == null) return true;
        try
        {
            object bar = barField.GetValue(__instance);
            if (bar == null) return true;
            Character observed = GetObservedCharacter(bar);
            if (observed != null && (ShouldSkipCharacter(observed) || HasBrokenRefs(observed))) return false;
        }
        catch (Exception ex)
        {
            LogPrefixFailureOnce(nameof(AfflictionMethodPrefix), ex);
        }
        return true;
    }

    private static bool TimerHeightStatsUpdatePrefix()
    {
        return ShouldAllowLocalStatsOverlay();
    }

    private static bool MapStatsUpdatePrefix()
    {
        return ShouldAllowLocalStatsOverlay();
    }

    private static bool ShouldAllowLocalStatsOverlay()
    {
        Character local = Character.localCharacter;
        if (local == null) return false;
        if (ShouldSkipCharacter(local)) return false;
        if (local.refs == null || local.refs.stats == null) return false;
        return true;
    }

    private static bool PlayersInfoTargetPrefix(object __instance)
    {
        if (__instance == null) return true;
        try
        {
            FieldInfo field = ResolvePlayersInfoTargetField(__instance.GetType());
            if (field != null && field.GetValue(__instance) is Character target
                && (ShouldSkipCharacter(target) || HasBrokenRefs(target)))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            LogPrefixFailureOnce(nameof(PlayersInfoTargetPrefix), ex);
        }
        return true;
    }

    private static bool PlayersInfoGlobalPrefix()
    {
        if (!global::TransformState.AnyFormActive) return true;
        return ShouldAllowLocalStatsOverlay();
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static bool ShouldSkipCharacter(Character character)
    {
        if (character == null) return false;
        try
        {
            return ZombieController.ActiveZombieCharacter == character
                || ZombieController.ParkedPlayerCharacter == character
                || ImScoutmaster.Plugin.IsControlledScoutmasterCharacter(character)
                || ImScoutmaster.Plugin.IsStashedSourceCharacter(character)
                || ImCritter.CritterController.ActiveCritterCharacter == character
                || ImTumbleweed.TumbleweedController.ActiveWeedCharacter == character
                || ImGhost.GhostController.ActiveGhostCharacter == character
                || ImTornado.TornadoController.ActiveTornadoCharacter == character
                || global::Transform.Statue.StatueController.ActiveStatueCharacter == character;
        }
        catch
        {
            return ZombieController.ActiveZombieCharacter == character
                || ZombieController.ParkedPlayerCharacter == character;
        }
    }

    /// <summary>True when the character's refs are not a full player's refs — the exact
    /// condition that NREs the status mods (NPC-prefab characters like mushroom zombies).</summary>
    private static bool HasBrokenRefs(Character character)
    {
        if (character == null) return false;
        return character.refs == null || character.refs.customization == null;
    }

    private static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, FieldInfo>> _observedFields =
        new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, FieldInfo>>();
    private static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, FieldInfo>> _afflictionBarFields =
        new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, FieldInfo>>();
    private static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, FieldInfo>> _playersInfoTargetFields =
        new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, FieldInfo>>();

    private static Character GetObservedCharacter(object bar)
    {
        if (bar == null) return null;
        try
        {
            FieldInfo field = ResolveObservedField(bar.GetType());
            return field?.GetValue(bar) as Character;
        }
        catch (Exception ex)
        {
            LogPrefixFailureOnce(nameof(GetObservedCharacter), ex);
        }
        return null;
    }

    private static FieldInfo ResolveObservedField(Type barType)
    {
        foreach (var kv in _observedFields)
        {
            if (kv.Key == barType) return kv.Value;
        }
        FieldInfo field = FindObservedCharacterField(barType);
        if (field != null)
        {
            _observedFields.Add(new System.Collections.Generic.KeyValuePair<Type, FieldInfo>(barType, field));
        }
        return field;
    }

    private static FieldInfo ResolveAfflictionBarField(Type afflictionType)
    {
        foreach (var kv in _afflictionBarFields)
        {
            if (kv.Key == afflictionType) return kv.Value;
        }
        FieldInfo field = afflictionType?.GetField("characterStaminaBar", InstanceFlags);
        if (field != null)
        {
            _afflictionBarFields.Add(new System.Collections.Generic.KeyValuePair<Type, FieldInfo>(afflictionType, field));
        }
        return field;
    }

    private static void RegisterPlayersInfoTargetField(Type type)
    {
        if (type == null) return;
        FieldInfo field = type.GetField("Target", InstanceFlags | BindingFlags.Public | BindingFlags.Static);
        if (field == null || field.FieldType != typeof(Character)) return;
        _playersInfoTargetFields.RemoveAll(kv => kv.Key == type);
        _playersInfoTargetFields.Add(new System.Collections.Generic.KeyValuePair<Type, FieldInfo>(type, field));
    }

    private static FieldInfo ResolvePlayersInfoTargetField(Type type)
    {
        foreach (var kv in _playersInfoTargetFields)
        {
            if (kv.Key == type) return kv.Value;
        }
        FieldInfo field = type?.GetField("Target", InstanceFlags | BindingFlags.Public | BindingFlags.Static);
        if (field != null && field.FieldType == typeof(Character))
        {
            _playersInfoTargetFields.Add(new System.Collections.Generic.KeyValuePair<Type, FieldInfo>(type, field));
        }
        return field;
    }

    /// <summary>The observed character lives in field "_observedCharacter" in every known
    /// build (the property name differs: observedCharacter / ObservedCharacter).</summary>
    private static FieldInfo FindObservedCharacterField(Type barType)
    {
        FieldInfo field = barType.GetField("_observedCharacter", InstanceFlags);
        if (field != null) return field;
        // Fallback: find the backing field of any observed-character property.
        foreach (PropertyInfo prop in barType.GetProperties(InstanceFlags))
        {
            string name = prop.Name;
            if (name.Equals("observedCharacter", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ObservedCharacter", StringComparison.Ordinal))
            {
                foreach (FieldInfo f in barType.GetFields(InstanceFlags))
                {
                    if (f.Name.EndsWith("_observedCharacter", StringComparison.Ordinal)
                        || f.Name.EndsWith("<observedCharacter>k__BackingField", StringComparison.Ordinal)
                        || f.Name.EndsWith("<ObservedCharacter>k__BackingField", StringComparison.Ordinal))
                    {
                        return f;
                    }
                }
            }
        }
        return null;
    }

    private static MethodInfo FindMethodNoArgs(Type type, string name)
    {
        foreach (MethodInfo m in type.GetMethods(InstanceFlags | BindingFlags.Static))
        {
            if (m != null && m.Name == name && m.GetParameters().Length == 0) return m;
        }
        return null;
    }

    private static MethodInfo FindMethodOneArg(Type type, string name)
    {
        foreach (MethodInfo m in type.GetMethods(InstanceFlags | BindingFlags.Static | BindingFlags.Public))
        {
            if (m != null && m.Name == name && m.GetParameters().Length == 1) return m;
        }
        return null;
    }

    private static MethodInfo FindMethodWithCharacterArg(Type type, string name)
    {
        foreach (MethodInfo m in type.GetMethods(InstanceFlags | BindingFlags.Static | BindingFlags.Public))
        {
            if (m == null || m.Name != name) continue;
            foreach (ParameterInfo p in m.GetParameters())
            {
                if (p.ParameterType == typeof(Character)) return m;
            }
        }
        return null;
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch (Exception ex)
            {
                LogPrefixFailureOnce(nameof(FindLoadedType), ex);
            }
        }
        return null;
    }
}
