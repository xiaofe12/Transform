using System;

namespace ImRaven;

// The native 3-second takeoff clip contains a matching wing cycle at .3-.8s.
// Its remaining tail is a held pose, not sustained flight.
internal sealed class CrowFlightCycle
{
    internal const float StartSeconds = 0.3f;
    internal const float EndSeconds = 0.8f;
    private float _time;

    internal float Step(float deltaTime, float clipLength, bool flap)
    {
        if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) deltaTime = 0f;
        if (float.IsNaN(clipLength) || float.IsInfinity(clipLength) || clipLength < EndSeconds) clipLength = 3f;
        float period = EndSeconds - StartSeconds;
        _time = (_time + Math.Max(0f, deltaTime) * (flap ? 1f : 0.65f)) % period;
        return (StartSeconds + _time) / clipLength;
    }
}
