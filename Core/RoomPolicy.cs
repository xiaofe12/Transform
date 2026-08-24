using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine.SceneManagement;
using PhotonPlayer = Photon.Realtime.Player;

namespace Transform.Core;

/// <summary>
/// 房间级变身策略：有管理权的一端统一管理全局策略、单个形态启用范围，以及各形态参数。
/// 在线房间通过 Photon Room CustomProperties 同步；离线 / 不在房间时使用本地配置。
/// </summary>
internal static class RoomPolicy
{
    internal enum Policy
    {
        AllowAll = 0,
        LobbyOnly = 1,
        Disabled = 2
    }

    private const string RoomPropertyKey = "Thanks.Transform.Policy";
    private const string RoomFormsPropertyKey = "Thanks.Transform.Forms";
    private const string RoomSettingsPropertyKey = "Thanks.Transform.Settings";
    private const string ModPlayerProperty = "Thanks.Transform.Mod";
    private const int FormCount = 14;
    private const int AllFormsMask = (1 << FormCount) - 1;
    private const int LegacyTenFormsMask = (1 << 10) - 1;
    private const int LegacyElevenFormsMask = (1 << 11) - 1;

    private static ManualLogSource _log;
    private static ConfigEntry<Policy> _configEntry;
    private static ConfigEntry<int> _formsMaskEntry;
    private static string _lastHostSettingsSnapshot;
    private static string _lastRemoteSettingsSnapshot;
    private static int _cachedMasterActorNumber = -1;
    private static bool _cachedMasterHasTransformMod;
    private static bool _cachedMasterStatusKnown;

    internal static Policy Current { get; private set; } = Policy.AllowAll;
    internal static int CurrentFormsMask { get; private set; } = AllFormsMask;

    internal static bool InLobby
    {
        get
        {
            try { return SceneManager.GetActiveScene().name == "Airport"; }
            catch { return false; }
        }
    }

    internal static bool IsHost
    {
        get
        {
            try { return !PhotonNetwork.InRoom || PhotonNetwork.OfflineMode || PhotonNetwork.IsMasterClient; }
            catch { return true; }
        }
    }

    internal static bool MasterHasTransformMod
    {
        get
        {
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) return true;
                PhotonPlayer master = PhotonNetwork.MasterClient;
                if (master == null) return true;

                if (_cachedMasterActorNumber != master.ActorNumber)
                {
                    _cachedMasterActorNumber = master.ActorNumber;
                    _cachedMasterHasTransformMod = false;
                    _cachedMasterStatusKnown = false;
                }

                Hashtable properties = master.CustomProperties;
                if (properties != null && properties.ContainsKey(ModPlayerProperty))
                {
                    _cachedMasterHasTransformMod = true;
                    _cachedMasterStatusKnown = true;
                    return true;
                }

                return _cachedMasterStatusKnown && _cachedMasterHasTransformMod;
            }
            catch { return false; }
        }
    }

    internal static bool InOnlineRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    internal static bool IsClientInOnlineRoom => InOnlineRoom && !IsHost;

    internal static bool HostControlsRoomPolicy => InOnlineRoom && IsHost;

    internal static bool UsesRoomPolicy => InOnlineRoom && (IsHost || MasterHasTransformMod);

    internal static bool SettingsControlledByHost =>
        InOnlineRoom && !IsHost && MasterHasTransformMod;

    internal static bool CanManageRoomPolicy =>
        IsHost || (InOnlineRoom && !MasterHasTransformMod);

    internal static bool LocalOnlyPolicyMode =>
        InOnlineRoom && !IsHost && !MasterHasTransformMod;

    internal static bool CanTransformNow =>
        Current == Policy.AllowAll || (Current == Policy.LobbyOnly && InLobby);

    internal static void Initialize(ConfigFile config, ManualLogSource log)
    {
        _log = log;
        _configEntry = config.Bind("Room", "TransformPolicy", Policy.AllowAll,
            "变身策略：房主安装模组时由房主同步全房间；房主未安装模组时仅本地生效。" +
            "AllowAll = 允许所有场景使用；LobbyOnly = 仅在大厅（机场）可用；Disabled = 禁用变身模组。");
        _formsMaskEntry = config.Bind("Room", "EnabledFormsMask", AllFormsMask,
            "允许的形态 bitmask：房主安装模组时由房主同步全房间；房主未安装模组时仅本地生效。默认 16383 = 全部启用。");
        if (_formsMaskEntry.Value == LegacyTenFormsMask || _formsMaskEntry.Value == LegacyElevenFormsMask)
        {
            _formsMaskEntry.Value = AllFormsMask;
        }

        Current = _configEntry.Value;
        CurrentFormsMask = NormalizeFormsMask(_formsMaskEntry.Value);
    }

    internal static void Tick()
    {
        try
        {
            AdvertisePresence();
            Policy effectivePolicy = ReadEffectivePolicy();
            int effectiveMask = ReadEffectiveFormsMask();

            if (effectivePolicy != Current)
            {
                Current = effectivePolicy;
                _log?.LogInfo("[Transform] 房间变身策略: " + Current);
            }
            if (effectiveMask != CurrentFormsMask)
            {
                CurrentFormsMask = effectiveMask;
                _log?.LogInfo("[Transform] 房间允许形态 bitmask: " + CurrentFormsMask);
            }

            SyncSettingsSnapshot();

            FormRegistry.FormDescriptor active = FormRegistry.ActiveForm;
            if (active != null && (!CanTransformNow || !IsFormAllowed(active.Id)))
            {
                FormRegistry.ExitActiveForm();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Transform] RoomPolicy.Tick: " + ex.Message);
        }
    }

    internal static bool IsFormAllowed(FormId formId)
    {
        int bit = 1 << (int)formId;
        return (CurrentFormsMask & bit) != 0;
    }

    internal static bool CanUseForm(FormId formId)
    {
        return CanTransformNow && IsFormAllowed(formId);
    }

    internal static void ToggleFormAllowedFromMenu(FormId formId)
    {
        if (!CanManageRoomPolicy) return;
        SetFormAllowed(formId, !IsFormAllowed(formId));
    }

    internal static void SetFormAllowed(FormId formId, bool allowed)
    {
        if (!CanManageRoomPolicy) return;

        int bit = 1 << (int)formId;
        int next = allowed ? (CurrentFormsMask | bit) : (CurrentFormsMask & ~bit);
        next = NormalizeFormsMask(next);
        CurrentFormsMask = next;
        if (_formsMaskEntry != null) _formsMaskEntry.Value = next;

        if (HostControlsRoomPolicy)
        {
            ApplyToRoom(Current, CurrentFormsMask, _lastHostSettingsSnapshot ?? BuildSettingsSnapshot());
        }
        _log?.LogInfo("[Transform] " + ManagerLogLabel() + (allowed ? "启用" : "禁用") + "形态: " + formId);
    }

    internal static void CycleFromMenu()
    {
        if (!CanManageRoomPolicy) return;
        SetPolicy((Policy)(((int)Current + 1) % 3));
    }

    internal static void SetPolicy(Policy policy)
    {
        if (!CanManageRoomPolicy) return;
        if (_configEntry != null) _configEntry.Value = policy;
        Current = policy;
        if (HostControlsRoomPolicy)
        {
            ApplyToRoom(policy, CurrentFormsMask, _lastHostSettingsSnapshot ?? BuildSettingsSnapshot());
        }
        _log?.LogInfo("[Transform] " + ManagerLogLabel() + "设置变身策略: " + policy);
    }

    private static void AdvertisePresence()
    {
        try
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode || PhotonNetwork.LocalPlayer == null) return;
            Hashtable properties = PhotonNetwork.LocalPlayer.CustomProperties;
            if (properties != null && properties.ContainsKey(ModPlayerProperty)) return;
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { [ModPlayerProperty] = TransformPlugin.PluginVersion });
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Transform] 写入模组玩家标记失败: " + ex.Message);
        }
    }

    private static Policy ReadEffectivePolicy()
    {
        if (!UsesRoomPolicy)
        {
            return _configEntry?.Value ?? Policy.AllowAll;
        }

        try
        {
            Hashtable props = PhotonNetwork.CurrentRoom?.CustomProperties;
            if (props != null && props.ContainsKey(RoomPropertyKey))
            {
                return ParsePolicy(props[RoomPropertyKey]);
            }

            if (HostControlsRoomPolicy)
            {
                ApplyToRoom(_configEntry?.Value ?? Policy.AllowAll,
                    NormalizeFormsMask(_formsMaskEntry?.Value ?? AllFormsMask),
                    BuildSettingsSnapshot());
            }
            return _configEntry?.Value ?? Policy.AllowAll;
        }
        catch
        {
            return Current;
        }
    }

    private static int ReadEffectiveFormsMask()
    {
        if (!UsesRoomPolicy)
        {
            return NormalizeFormsMask(_formsMaskEntry?.Value ?? AllFormsMask);
        }

        try
        {
            Hashtable props = PhotonNetwork.CurrentRoom?.CustomProperties;
            if (props != null && props.ContainsKey(RoomFormsPropertyKey))
            {
                return ParseFormsMask(props[RoomFormsPropertyKey]);
            }

            if (HostControlsRoomPolicy)
            {
                ApplyToRoom(_configEntry?.Value ?? Policy.AllowAll,
                    NormalizeFormsMask(_formsMaskEntry?.Value ?? AllFormsMask),
                    BuildSettingsSnapshot());
            }
            return NormalizeFormsMask(_formsMaskEntry?.Value ?? AllFormsMask);
        }
        catch
        {
            return CurrentFormsMask;
        }
    }

    private static void SyncSettingsSnapshot()
    {
        if (!InOnlineRoom)
        {
            _lastHostSettingsSnapshot = BuildSettingsSnapshot();
            _lastRemoteSettingsSnapshot = null;
            return;
        }

        if (HostControlsRoomPolicy)
        {
            string snapshot = BuildSettingsSnapshot();
            Hashtable props = PhotonNetwork.CurrentRoom?.CustomProperties;
            bool missing = props == null || !props.ContainsKey(RoomSettingsPropertyKey);
            if (missing || !string.Equals(snapshot, _lastHostSettingsSnapshot, StringComparison.Ordinal))
            {
                _lastHostSettingsSnapshot = snapshot;
                ApplyToRoom(Current, CurrentFormsMask, snapshot);
            }
            return;
        }

        if (SettingsControlledByHost)
        {
            try
            {
                Hashtable props = PhotonNetwork.CurrentRoom?.CustomProperties;
                if (props != null && props[RoomSettingsPropertyKey] is string snapshot
                    && !string.Equals(snapshot, _lastRemoteSettingsSnapshot, StringComparison.Ordinal))
                {
                    ApplySettingsSnapshot(snapshot);
                    _lastRemoteSettingsSnapshot = snapshot;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning("[Transform] 应用房间参数失败: " + ex.Message);
            }
            return;
        }

        _lastHostSettingsSnapshot = BuildSettingsSnapshot();
        _lastRemoteSettingsSnapshot = null;
    }
    private static string BuildSettingsSnapshot()
    {
        List<ConfigEntry<float>> entries = CollectFloatEntries();
        StringBuilder sb = new StringBuilder(entries.Count * 8);
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            ConfigEntry<float> entry = entries[i];
            float value = entry != null ? entry.Value : 0f;
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void ApplySettingsSnapshot(string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot)) return;

        string[] parts = snapshot.Split(',');
        List<ConfigEntry<float>> entries = CollectFloatEntries();
        int count = Math.Min(parts.Length, entries.Count);
        for (int i = 0; i < count; i++)
        {
            ConfigEntry<float> entry = entries[i];
            if (entry == null) continue;
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                entry.Value = value;
            }
        }
    }

    private static List<ConfigEntry<float>> CollectFloatEntries()
    {
        List<ConfigEntry<float>> entries = new List<ConfigEntry<float>>();
        foreach (FormRegistry.FormDescriptor form in FormRegistry.Forms)
        {
            AddEntry(entries, SafeEntry(form.CameraDistance));
            AddEntry(entries, SafeEntry(form.CameraHeight));
            AddEntry(entries, SafeEntry(form.CameraFov));
            foreach (FormRegistry.ParamDescriptor param in form.Params)
            {
                AddEntry(entries, SafeEntry(param.Entry));
            }
        }
        return entries;
    }

    private static void AddEntry(List<ConfigEntry<float>> entries, ConfigEntry<float> entry)
    {
        if (entry != null) entries.Add(entry);
    }

    private static ConfigEntry<float> SafeEntry(Func<ConfigEntry<float>> getter)
    {
        try { return getter?.Invoke(); }
        catch { return null; }
    }

    private static Policy ParsePolicy(object raw)
    {
        if (raw is string text && Enum.TryParse(text, out Policy parsed)) return parsed;
        if (raw is int value && value >= 0 && value <= (int)Policy.Disabled) return (Policy)value;
        return Policy.AllowAll;
    }

    private static int ParseFormsMask(object raw)
    {
        if (raw is int value) return NormalizeFormsMask(value);
        if (raw is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return NormalizeFormsMask(parsed);
        }
        return AllFormsMask;
    }

    private static int NormalizeFormsMask(int mask)
    {
        return mask & AllFormsMask;
    }

    private static string ManagerLogLabel()
    {
        if (IsHost) return "房主";
        return LocalOnlyPolicyMode ? "本地策略" : "客机";
    }

    private static void ApplyToRoom(Policy policy, int formsMask, string settingsSnapshot)
    {
        try
        {
            if (!HostControlsRoomPolicy) return;
            Hashtable props = new ExitGames.Client.Photon.Hashtable
            {
                [RoomPropertyKey] = policy.ToString(),
                [RoomFormsPropertyKey] = NormalizeFormsMask(formsMask)
            };
            if (settingsSnapshot != null)
            {
                props[RoomSettingsPropertyKey] = settingsSnapshot;
            }
            PhotonNetwork.CurrentRoom.SetCustomProperties(props, null, null);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Transform] 同步房间策略失败: " + ex.Message);
        }
    }
}
