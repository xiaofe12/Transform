using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

namespace ImScoutmaster;

/// <summary>Owns one native camera override, yielding it without touching player identity.</summary>
[DefaultExecutionOrder(450)]
internal sealed class ScoutmasterCameraRig : MonoBehaviour
{
    private static readonly FieldInfo OverrideField = typeof(MainCamera).GetField("camOverride", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo ObservedProperty = typeof(MainCameraMovement).GetProperty("specCharacter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private CameraOverride _pose;
    private MainCamera _camera;
    private CameraOverride _previous;
    private Character _previousObserved;
    private Character _observed;
    private bool _claimed;
    private bool _fovCaptured;

    internal static bool HasCameraOverride => MainCamera.instance != null
        && OverrideField?.GetValue(MainCamera.instance) as CameraOverride != null;

    internal static void AppendMissingReflectionMembers(List<string> missing)
    {
        if (OverrideField == null) missing.Add("MainCamera.camOverride");
        if (ObservedProperty?.GetGetMethod(true) == null || ObservedProperty.GetSetMethod(true) == null)
            missing.Add("MainCameraMovement.specCharacter accessors");
    }

    private void Awake() { _pose = gameObject.AddComponent<CameraOverride>(); }
    private void LateUpdate() { Plugin.RefreshControlledScoutmasterCamera(null); }

    internal void Publish(Character observedPlayer, Vector3 position, Quaternion rotation)
    {
        if (OverrideField == null) return;
        if (_camera != MainCamera.instance) Release();
        if (!_claimed && MainCamera.instance != null)
        {
            _camera = MainCamera.instance;
            _previous = OverrideField.GetValue(_camera) as CameraOverride;
            _previousObserved = ObservedProperty?.GetValue(null, null) as Character;
            if (!_fovCaptured)
            {
                Camera camera = _camera.GetComponent<Camera>();
                float fov = camera != null ? camera.fieldOfView : 70f;
                _pose.fov = !float.IsNaN(fov) && !float.IsInfinity(fov) && fov >= 1f && fov <= 179f ? fov : 70f;
                _fovCaptured = true;
            }
            _claimed = true;
        }
        if (!_claimed) return;
        transform.SetPositionAndRotation(position, rotation);
        _observed = observedPlayer;
        if (OverrideField.GetValue(_camera) as CameraOverride != _pose) _camera.SetCameraOverride(_pose);
        // The pose can follow a creature while native spectator UI observes the
        // original player, whose inventory and player-only data remain valid.
        ObservedProperty?.SetValue(null, observedPlayer, null);
    }

    internal void Release()
    {
        try
        {
            try
            {
                if (_claimed && _camera != null && OverrideField?.GetValue(_camera) as CameraOverride == _pose)
                    _camera.SetCameraOverride(_previous);
            }
            finally
            {
                if (_claimed && ObservedProperty?.GetValue(null, null) as Character == _observed)
                    ObservedProperty?.SetValue(null, _previousObserved, null);
            }
        }
        finally
        {
            _claimed = false;
            _camera = null;
            _previous = null;
            _observed = null;
            _previousObserved = null;
        }
    }

    private void OnDestroy() { Release(); }
}
