using Unity.Mathematics;
using UnityEngine;

namespace ImRaven;

/// <summary>
/// The vanilla wire pose always stays at the real flight position. Every peer,
/// including an unmodded host/victim and late joiner, understands this payload.
/// Body visibility is a renderer concern on peers that can display the bird.
/// </summary>
internal sealed class CondorNetworkPose
{
    private Vector3 _hipOffset;

    internal void Reset(Vector3 center, Vector3 hip)
    {
        _hipOffset = hip - center;
    }

    internal void Write(Vector3 center, Vector3 velocity, ref CharacterSyncData packet)
    {
        Vector3 hip = center + _hipOffset;
        packet.hipLocation = new float3(hip.x, hip.y, hip.z);
        packet.averageVelocity = new float3(velocity.x, velocity.y, velocity.z);
        packet.movementInput = Vector2.zero;
        packet.isGrounded = false;
        packet.isClimbing = false;
        packet.ropeClimbing = false;
    }
}
