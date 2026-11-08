using UnityEngine;

namespace Transform.Core;

/// <summary>
/// Smooth the orbit offset, not the flight translation. Camera and aim point share
/// one render center, so vertical motion cannot feed back into camera pitch.
/// Explicit delta time keeps this math independent of Unity's update callbacks.
/// </summary>
internal sealed class FlightCameraRig
{
    private Vector3 _offset;
    private Vector3 _offsetVelocity;
    private bool _initialized;

    internal Vector3 Position { get; private set; }
    internal Vector3 LookDirection { get; private set; }

    internal void Reset()
    {
        _initialized = false;
        _offsetVelocity = Vector3.zero;
    }

    internal void Step(Vector3 center, Vector3 desiredOffset, Vector3 lookOffset, float deltaTime)
    {
        if (!_initialized)
        {
            _offset = desiredOffset;
            _offsetVelocity = Vector3.zero;
            _initialized = true;
        }
        else if (deltaTime > 0f)
        {
            _offset = Vector3.SmoothDamp(_offset, desiredOffset, ref _offsetVelocity,
                0.08f, float.PositiveInfinity, deltaTime);
        }
        Position = center + _offset;
        // Subtract offsets directly: large world coordinates and frame translation
        // should never introduce rounding noise or a height-dependent pitch change.
        LookDirection = lookOffset - _offset;
    }
}
