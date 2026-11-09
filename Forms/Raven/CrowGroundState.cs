using UnityEngine;

namespace ImRaven;

internal sealed class CrowGroundState
{
    internal const float Clearance = 0.04f;
    internal bool Grounded { get; private set; }

    internal void Reset(bool grounded = false) { Grounded = grounded; }

    internal Vector3 Step(Vector3 position, Vector3 displacement, float groundY, bool rising)
    {
        bool hasGround = !float.IsNaN(groundY) && !float.IsInfinity(groundY);
        if (!hasGround) { Grounded = false; return displacement; }
        float floor = groundY + Clearance;
        if (rising) Grounded = false;
        else if (Grounded || (displacement.y <= 0f && position.y + displacement.y <= floor + 0.025f))
        {
            bool wasGrounded = Grounded;
            Grounded = true;
            // No native walking clip: remain perched until the ascent control.
            // On touchdown, finish the step onto the support we probed ahead.
            return new Vector3(wasGrounded ? 0f : displacement.x, floor - position.y, wasGrounded ? 0f : displacement.z);
        }
        return displacement;
    }
}
