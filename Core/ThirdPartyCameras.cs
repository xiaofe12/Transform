using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace Transform.Core;

/// <summary>
/// 第三方自由相机模组（PeakSpectatorMode / PeakCinema）的统一检测与让路门控。
///
/// 兼容策略（外部相机优先）：双方都在逐帧写主相机变换，同时激活会互相覆盖导致相机抖动，
/// 因此任一外部相机激活期间，本模组所有形态的相机覆盖跳过写入；外部相机关闭后自动恢复。
///
/// 纯反射检测，不引用对方程序集：
///   - PeakSpectatorMode.Plugin.CameraController（internal static 字段）
///     → SpectatorCameraController.IsActive（internal instance 属性）
///   - PeakCinema.Plugin.CinemaCamActive（internal static bool 属性）
/// BepInEx 插件均在启动时加载完毕，但保守起见沿用重试机制：每 2 秒一次，30 秒后放弃。
/// </summary>
internal static class ThirdPartyCameras
{
    private const float RetryIntervalSeconds = 2f;
    private const int MaxRetryAttempts = 15;

    private static ManualLogSource _log;
    private static float _nextRetryTime;
    private static int _spectatorRetryAttempts;
    private static int _cinemaRetryAttempts;
    private static bool _spectatorGaveUp;
    private static bool _cinemaGaveUp;

    private static FieldInfo _spectatorControllerField;
    private static PropertyInfo _spectatorIsActiveProperty;
    private static PropertyInfo _cinemaActiveProperty;

    private static bool SpectatorResolved => _spectatorControllerField != null;
    private static bool CinemaResolved => _cinemaActiveProperty != null;
    private static bool SpectatorDone => SpectatorResolved || _spectatorGaveUp;
    private static bool CinemaDone => CinemaResolved || _cinemaGaveUp;

    /// <summary>至少检测到一个外部相机模组。</summary>
    internal static bool Detected =>
        _spectatorControllerField != null || _cinemaActiveProperty != null;

    internal static bool ShouldPauseFormControl => ExternalCameraActive;

    /// <summary>任一外部相机模组处于激活状态：本模组的相机覆盖本帧应让路。
    /// 未检测到任何外部相机模组时恒为 false（一次布尔比较的代价）。</summary>
    internal static bool ExternalCameraActive
    {
        get
        {
            if (_spectatorControllerField != null)
            {
                try
                {
                    object controller = _spectatorControllerField.GetValue(null);
                    if (controller != null
                        && _spectatorIsActiveProperty != null
                        && _spectatorIsActiveProperty.GetValue(controller, null) is bool spectatorActive
                        && spectatorActive)
                    {
                        return true;
                    }
                }
                catch { /* 对方模组内部状态异常时视为未激活 */ }
            }

            if (_cinemaActiveProperty != null)
            {
                try
                {
                    return _cinemaActiveProperty.GetValue(null, null) is bool cinemaActive && cinemaActive;
                }
                catch { }
            }

            return false;
        }
    }

    internal static void Initialize(ManualLogSource log)
    {
        _log = log;
        _nextRetryTime = 0f;
        _spectatorRetryAttempts = 0;
        _cinemaRetryAttempts = 0;
        _spectatorGaveUp = false;
        _cinemaGaveUp = false;
    }

    /// <summary>每帧驱动（TransformPlugin.Update）：两类相机都解析到或放弃前，按间隔重试。</summary>
    internal static void Tick()
    {
        if (SpectatorDone && CinemaDone) return;

        float now;
        try { now = Time.unscaledTime; }
        catch { return; }
        if (now < _nextRetryTime) return;
        _nextRetryTime = now + RetryIntervalSeconds;

        Resolve();
    }

    private static void Resolve()
    {
        if (!SpectatorDone)
        {
            ResolveSpectator();
            if (!SpectatorResolved && ++_spectatorRetryAttempts >= MaxRetryAttempts)
            {
                _spectatorGaveUp = true;
            }
        }

        if (!CinemaDone)
        {
            ResolveCinema();
            if (!CinemaResolved && ++_cinemaRetryAttempts >= MaxRetryAttempts)
            {
                _cinemaGaveUp = true;
            }
        }
    }

    private static void ResolveSpectator()
    {
        Type pluginType = FindLoadedType("PeakSpectatorMode.Plugin");
        if (pluginType == null) return;

        FieldInfo controllerField = pluginType.GetField(
            "CameraController", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo isActiveProperty = controllerField?.FieldType.GetProperty(
            "IsActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (controllerField == null || isActiveProperty == null || isActiveProperty.PropertyType != typeof(bool)) return;

        _spectatorControllerField = controllerField;
        _spectatorIsActiveProperty = isActiveProperty;
        _log?.LogInfo("[Transform] Detected PeakSpectatorMode; form cameras yield while its spectator camera is active.");
    }

    private static void ResolveCinema()
    {
        Type pluginType = FindLoadedType("PeakCinema.Plugin");
        if (pluginType == null) return;

        PropertyInfo activeProperty = pluginType.GetProperty(
            "CinemaCamActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (activeProperty == null || activeProperty.PropertyType != typeof(bool)) return;

        _cinemaActiveProperty = activeProperty;
        _log?.LogInfo("[Transform] Detected PeakCinema; form cameras yield while its cinema camera is active.");
    }

    private static Type FindLoadedType(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
        }
        catch { }
        return null;
    }
}
