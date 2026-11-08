using UnityEngine;

namespace ImRaven;

internal enum CondorFlightMode { Flap, Glide, Dive }

/// <summary>Velocity-based presentation with separate enter/leave thresholds.</summary>
internal sealed class CondorFlightState
{
    private Vector3 _velocity;
    private bool _initialized;
    internal CondorFlightMode Mode { get; private set; } = CondorFlightMode.Flap;

    internal CondorFlightMode Step(Vector3 velocity, float deltaTime, bool carrying)
    {
        if (!Transform.Core.SafeRestoreHistory.IsFinite(velocity)) velocity = Vector3.zero;
        _velocity = !_initialized ? velocity : Vector3.Lerp(_velocity, velocity,
            1f - Mathf.Exp(-6f * Mathf.Max(0f, deltaTime)));
        _initialized = true;
        float speed = new Vector2(_velocity.x, _velocity.z).magnitude;
        if (carrying) Mode = CondorFlightMode.Flap;
        else if (_velocity.y < (Mode == CondorFlightMode.Dive ? -5f : -7f)) Mode = CondorFlightMode.Dive;
        else if (_velocity.y > 0.5f || speed < (Mode == CondorFlightMode.Glide ? 10f : 13f)) Mode = CondorFlightMode.Flap;
        else Mode = CondorFlightMode.Glide;
        return Mode;
    }
}
