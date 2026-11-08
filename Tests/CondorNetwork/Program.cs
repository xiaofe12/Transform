using System;
using ImRaven;
using Transform.Core;
using Unity.Mathematics;
using UnityEngine;

// Exercise the production flight/network writer on the game's actual vanilla
// CharacterSyncData type. This does not emulate Photon delivery or remote physics.
internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new Exception(message);
    }
    private static void Near(float3 value, Vector3 expected, string message)
    {
        Check(Math.Abs(value.x-expected.x) < 0.002f && Math.Abs(value.y-expected.y) < 0.002f
            && Math.Abs(value.z-expected.z) < 0.002f, message);
    }
    private static void Main()
    {
        foreach (Vector3 start in new[] { new Vector3(10,2,20), new Vector3(-200,500,-500), new Vector3(10000,3000,10000) })
        {
            var flight = new FlightMotion();
            var network = new CondorNetworkPose();
            Vector3 offset = new Vector3(0,-0.5f,0);
            flight.Reset(start-Vector3.up, start);
            network.Reset(start, start+offset);
            float3 look = new float3(20,30,0);
            for (int step = 0; step < 1500; step++)
            {
                Vector3 delta = step < 500 ? new Vector3(0.24f,0.18f,0) : step < 1000 ? new Vector3(-0.24f,-0.18f,0) : Vector3.zero;
                flight.Step(delta,0.02f);
                // Native animated bones may be displaced or contain a stale underground
                // pose. Neither may affect the wire pose, even during grab/drop frames.
                var packet = new CharacterSyncData {
                    hipLocation = new float3(-999,-30,-999), averageVelocity = new float3(99,99,99),
                    movementInput = Vector2.one, isGrounded = true, isClimbing = true, ropeClimbing = true,
                    stammina = 0.7f, extraStammina = 0.2f, lookValues = new float2(look.x,look.y),
                    spectateZoom = 4, petrify = 0, sinceGrounded = 0.1f
                };
                network.Write(flight.Position,flight.Velocity,ref packet);
                Near(packet.hipLocation,flight.Position+offset,"real hip position, no hidden offset or grab/drop jump");
                Near(packet.averageVelocity,delta/0.02f,"physics velocity preserved for vanilla interpolation");
                Check(packet.movementInput.x == 0 && packet.movementInput.y == 0,"no native ground walking while flying");
                Check(!packet.isGrounded && !packet.isClimbing && !packet.ropeClimbing,"no contradictory native locomotion flags");
                Check(packet.stammina == 0.7f && packet.extraStammina == 0.2f && packet.lookValues.x == look.x
                    && packet.lookValues.y == look.y && packet.spectateZoom == 4 && packet.petrify == 0,"unrelated vanilla state preserved");
            }
            // Reenter at a new position with a different hip offset: do not retain
            // another character's proportions/pose from the previous transformation.
            Vector3 reentry = new Vector3(50,60,70), newHip = reentry + new Vector3(0.1f,-0.8f,0.2f);
            network.Reset(reentry,newHip);
            var reentered = new CharacterSyncData();
            network.Write(reentry,Vector3.zero,ref reentered);
            Near(reentered.hipLocation,newHip,"reentry resets hip offset");
            Near(reentered.averageVelocity,Vector3.zero,"reentry has no stale velocity");
        }
        Console.WriteLine($"PASS: {_checks:N0} assertions against the game's CharacterSyncData payload: ascent/descent/stop, stable real hip and velocity, vanilla flags/state, reentry.");
    }
}
