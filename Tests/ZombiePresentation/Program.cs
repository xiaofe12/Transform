using System;
using ImZombie;
using Transform.Core;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string reason) { _checks++; if (!value) throw new Exception(reason); }
    private static void Context()
    {
        var player = new Character { voice = new Voice() };
        var zombie = new Character(); // Native creature has no player voice.
        var foreign = new Character();
        void Enter()
        {
            Character.localCharacter = zombie;
            ZombieController.ActiveZombieCharacter = zombie;
            ZombieController.ParkedPlayerCharacter = player;
            MainCameraMovement.Observe(null);
        }
        Enter();
        zombie.input.pauseWasPressed = true;
        var scope = ZombieGuiContext.Begin();
        Check(Character.localCharacter == player, "Native GUI gets real-player identity");
        Check(!Character.localCharacter.voice.transmitting, "Native voice read is valid with a voice-less creature");
        Check(player.input.pauseWasPressed, "Pause input follows the controlled character");
        Check(ZombieGuiContext.Begin() == null, "Nested GUI call does not capture a second ownership scope");
        player.input.pauseWasPressed = false;
        scope.End();
        Check(Character.localCharacter == zombie && !zombie.input.pauseWasPressed, "Consumed pause input and movement identity are restored");
        scope.End();
        Check(Character.localCharacter == zombie, "Repeated cleanup is harmless");

        Enter();
        MainCameraMovement.Observe(zombie);
        scope = ZombieGuiContext.Begin();
        Check(MainCameraMovement.specCharacter == player, "Creature spectator reference is safe during GUI update");
        try { throw new InvalidOperationException("native GUI exception"); }
        catch (InvalidOperationException) { }
        finally { scope.End(); }
        Check(Character.localCharacter == zombie && MainCameraMovement.specCharacter == zombie, "Exceptional GUI exit restores owned identity and observation");

        Enter(); MainCameraMovement.Observe(foreign);
        scope = ZombieGuiContext.Begin();
        Check(MainCameraMovement.specCharacter == foreign, "Another camera's observed character is retained");
        scope.End();
        Check(MainCameraMovement.specCharacter == foreign, "Another camera's target survives cleanup");

        Enter(); MainCameraMovement.Observe(zombie);
        scope = ZombieGuiContext.Begin();
        MainCameraMovement.Observe(foreign);
        Character.localCharacter = foreign;
        scope.End();
        Check(Character.localCharacter == foreign && MainCameraMovement.specCharacter == foreign, "Ownership changes inside GUI are preserved");

        Enter(); MainCameraMovement.Observe(zombie);
        scope = ZombieGuiContext.Begin();
        ZombieController.ActiveZombieCharacter = null;
        ZombieController.ParkedPlayerCharacter = null;
        scope.End();
        Check(Character.localCharacter == player && MainCameraMovement.specCharacter == player, "Exit inside GUI cannot resurrect the retired creature or clear a new form's player target");
        Check(ZombieGuiContext.Begin() == null, "No identity intervention outside zombie form");
    }

    private static Matrix4x4 Projection(float horizontalShift = 0, float verticalShift = 0)
    {
        // Standard off-centre OpenGL perspective, independent of Unity native entry points.
        return new Matrix4x4 {
            m00 = 1.5f, m11 = 2f, m02 = horizontalShift, m12 = verticalShift,
            m22 = -1.0002f, m23 = -0.20002f, m32 = -1f
        };
    }

    private static void CameraMath()
    {
        Check(CameraStateSafety.Perspective(Projection()), "Normal perspective is valid");
        Check(CameraStateSafety.Perspective(Projection(2.5f, -3.5f)), "Valid asymmetric mirror projection is retained");
        var oblique = Projection(); oblique.m20 = 3; oblique.m21 = -7;
        Check(CameraStateSafety.Perspective(oblique), "Valid oblique projection is retained");
        Check(CameraStateSafety.Matrix(Matrix4x4.identity) && !CameraStateSafety.Perspective(Matrix4x4.identity), "Native identity fallback is invertible but invalid for perspective");
        Check(!CameraStateSafety.Matrix(default), "Zero matrix is rejected");
        var singular = Projection(); singular.m11 = 0;
        Check(!CameraStateSafety.Matrix(singular), "Finite singular matrix is rejected");
        foreach (float bad in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            for (int i = 0; i < 16; i++)
            {
                var corrupted = Projection(); corrupted[i] = bad;
                Check(!CameraStateSafety.Perspective(corrupted), "Every nonfinite matrix element is rejected");
            }
            Check(!CameraStateSafety.Finite(new Vector3(0, bad, 0)), "Corrupted body position is rejected");
            Check(!CameraStateSafety.Direction(new Vector3(bad, 1, 0)), "Corrupted look direction is rejected");
            Check(!CameraStateSafety.Rotation(new Quaternion(0, bad, 0, 1)), "Corrupted camera rotation is rejected");
            var look = CameraStateSafety.Look(new Vector2(bad, bad));
            Check(look.x == 0 && look.y == 0, "Corrupted restored look is finite");
        }
        Check(!CameraStateSafety.Rotation(default), "Zero quaternion cannot pass pose validation");
        Check(!CameraStateSafety.Direction(Vector3.zero), "Zero look direction cannot reach LookRotation");
        var rotation = CameraStateSafety.NormalizeRotation(new Quaternion(0, 2, 0, 2));
        Check(Math.Abs(rotation.y * rotation.y + rotation.w * rotation.w - 1) < 0.00001, "Recovered rotation has unit norm");
        Check(CameraStateSafety.NormalizeRotation(default).w == 1, "Unusable rotation has a valid identity fallback");
        var clamped = CameraStateSafety.Look(new Vector2(725, 100000));
        Check(clamped.x == 5 && clamped.y == 85, "Look remains within native pitch limits");
        Check(!CameraStateSafety.Direction(new Vector3(float.MaxValue, 1, 0)), "Overflowing look vectors cannot poison normalization");
    }

    private static void Main()
    {
        Context();
        CameraMath();
        Console.WriteLine("Zombie GUI ownership and camera-state checks passed: " + _checks);
    }
}
