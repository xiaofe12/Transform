using UnityEngine;

namespace ImScoutmaster;

/// <summary>Session-local readiness and input ownership; never changes the game's local player.</summary>
internal sealed class ScoutmasterControlState
{
    private int _inputFrame = -1;
    internal bool Ready { get; private set; }
    internal void Begin() { Ready = false; _inputFrame = -1; }
    internal void Activate() { Ready = true; }
    internal void End() { Ready = false; _inputFrame = -1; }

    internal bool TrySample(int frame, bool blocked)
    {
        if (!Ready || blocked || frame == _inputFrame) return false;
        _inputFrame = frame;
        return true;
    }

    internal static Vector3 ThrowDirection(Vector3 sight, Vector3 fallback, float upBias)
    {
        if (!global::Transform.Core.SafeRestoreHistory.IsFinite(sight) || sight.sqrMagnitude < 0.0001f) sight = fallback;
        if (!global::Transform.Core.SafeRestoreHistory.IsFinite(sight) || sight.sqrMagnitude < 0.0001f) sight = Vector3.forward;
        if (float.IsNaN(upBias) || float.IsInfinity(upBias)) upBias = 0f;
        return sight.normalized + Vector3.up * Mathf.Clamp(upBias, 0f, 0.8f);
    }

    internal static Vector2 LookValues(Vector3 sight, Vector3 fallback)
    {
        Vector3 direction = ThrowDirection(sight, fallback, 0f).normalized;
        return new Vector2(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg,
            Mathf.Clamp(Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg, -85f, 85f));
    }
}
