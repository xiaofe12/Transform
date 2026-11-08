using UnityEngine;

namespace Transform.Core;

/// <summary>
/// Authoritative kinematic flight position, independent of the animated torso.
/// Physics uses Position; rendering interpolates the same two completed physics poses.
/// </summary>
internal sealed class FlightMotion
{
    private Vector3 _previousPosition;
    private Vector3 _rootOffset;

    internal Vector3 Position { get; private set; }
    internal Vector3 Velocity { get; private set; }
    internal Vector3 RootPosition => Position + _rootOffset;

    internal void Reset(Vector3 rootPosition, Vector3 center)
    {
        Position = _previousPosition = center;
        _rootOffset = rootPosition - center;
        Velocity = Vector3.zero;
    }

    internal void Step(Vector3 displacement, float fixedDeltaTime)
    {
        _previousPosition = Position;
        Position += displacement;
        Velocity = fixedDeltaTime > 0f ? displacement / fixedDeltaTime : Vector3.zero;
    }

    internal Vector3 RenderPosition(float fraction)
    {
        return Vector3.Lerp(_previousPosition, Position, Mathf.Clamp01(fraction));
    }
}
