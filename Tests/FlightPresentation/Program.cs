using System;
using Transform.Core;
using UnityEngine;

// Run the production flight math against Unity's own managed vector operations.
// No replacement math, mocked camera or running Unity scene is required.
internal static class Program
{
    private const float FixedStep = 0.02f;
    private static int _assertions;

    private static void Near(float actual, float expected, float tolerance, string reason)
    {
        _assertions++;
        if (!float.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
            throw new Exception(reason + ": expected " + expected + ", got " + actual);
    }

    private static void Near(Vector3 actual, Vector3 expected, float tolerance, string reason)
    {
        Near(actual.x, expected.x, tolerance, reason + " X");
        Near(actual.y, expected.y, tolerance, reason + " Y");
        Near(actual.z, expected.z, tolerance, reason + " Z");
    }

    private static void VerticalFlight(string form, Vector3 cameraOffset, Vector3 lookOffset, int fps, float speed)
    {
        var motion = new FlightMotion();
        var camera = new FlightCameraRig();
        motion.Reset(new Vector3(4, -2, 6), new Vector3(4, 0, 6));
        double fixedTime = 0;
        float previousY = 0;
        for (int frame = 0; frame <= fps * 3; frame++)
        {
            double now = frame / (double)fps;
            while (fixedTime + FixedStep <= now + 1e-8)
            {
                motion.Step(Vector3.up * speed * FixedStep, FixedStep);
                fixedTime += FixedStep;
                Near(motion.Velocity.y, speed, 0.0001f, "physics velocity");
            }
            Vector3 center = motion.RenderPosition((float)((now - fixedTime) / FixedStep));
            camera.Step(center, cameraOffset, lookOffset, 1f / fps);
            Near(camera.LookDirection, lookOffset - cameraOffset, 0.00001f, "elevation must not affect pitch");
            Near(camera.Position - center, cameraOffset, 0.00002f, "model and camera must share a render center");
            Near(center.y, speed * (float)Math.Max(0, now - FixedStep), 0.0002f, "continuous render interpolation");
            if (now - 1.0 / fps >= FixedStep)
                Near(center.y - previousY, speed / fps, 0.0001f, "no alternating physics/render-frame jumps");
            previousY = center.y;
        }
        Console.WriteLine("PASS: " + form + " " + fps + " FPS, vertical speed " + speed + ", constant camera pitch");
    }

    private static void PauseReverseAndReset()
    {
        var motion = new FlightMotion();
        motion.Reset(Vector3.zero, new Vector3(0, 2, 0));
        motion.Step(Vector3.up, FixedStep);
        motion.Step(Vector3.zero, FixedStep);
        Near(motion.RenderPosition(0), motion.RenderPosition(1), 0, "pause must not replay the preceding displacement");
        Near(motion.Velocity, Vector3.zero, 0, "pause velocity");
        motion.Step(-Vector3.up, FixedStep);
        Near(motion.RenderPosition(0).y, 3, 0, "reverse starts at previous pose");
        Near(motion.RenderPosition(1).y, 2, 0, "reverse ends at current pose");
        Near(motion.RootPosition.y, 0, 0, "preserve the initial torso/root offset");
        motion.Reset(new Vector3(40, 0, 20), new Vector3(40, 2, 20));
        Near(motion.RenderPosition(0), motion.RenderPosition(1), 0, "reenter has no stale interpolation history");
        Near(motion.RenderPosition(-2), motion.Position, 0, "clamped interpolation below zero");
        Near(motion.RenderPosition(2), motion.Position, 0, "clamped interpolation above one");
        Console.WriteLine("PASS: pause, direction reversal, reentry and interpolation boundaries");
    }

    private static void TurningAndExternalCameraReturn()
    {
        var stationary = new FlightCameraRig();
        var ascending = new FlightCameraRig();
        for (int i = 0; i < 360; i++)
        {
            float angle = i * 0.01f;
            Vector3 orbit = new Vector3((float)Math.Sin(angle) * 8, 3, -(float)Math.Cos(angle) * 8);
            Vector3 look = new Vector3(-(float)Math.Sin(angle) * 3, 2.6f, (float)Math.Cos(angle) * 3);
            stationary.Step(Vector3.zero, orbit, look, 1f / 144);
            // Also cover large map coordinates, where subtracting world positions
            // to construct the view direction would lose significant precision.
            ascending.Step(new Vector3(100000, i * 0.2f, -100000), orbit, look, 1f / 144);
            Near(ascending.LookDirection, stationary.LookDirection, 0, "orbit must be independent of vertical translation");
        }
        ascending.Reset();
        var newOffset = new Vector3(-6, 4, 0);
        ascending.Step(new Vector3(100, 20, 50), newOffset, Vector3.zero, 0);
        Near(ascending.Position, new Vector3(94, 24, 50), 0, "external camera return must not retain a stale orbit");
        Near(ascending.LookDirection, -newOffset, 0, "zero delta time must stay finite");
        Console.WriteLine("PASS: simultaneous orbit/vertical flight, large coordinates, external camera return, zero delta time");
    }

    private static void Main()
    {
        foreach (int fps in new[] { 30, 60, 120, 144, 240 })
        {
            foreach (float speed in new[] { -30f, -9f, 9f, 30f })
            {
                VerticalFlight("Condor", new Vector3(0, 3.5f, -8), new Vector3(0, 3.1f, 3), fps, speed);
                VerticalFlight("Ghost", new Vector3(0, 7, -16), new Vector3(0, 5, 4), fps, speed);
            }
        }
        PauseReverseAndReset();
        TurningAndExternalCameraReturn();
        Console.WriteLine("All flight presentation regressions passed (" + _assertions + " assertions).");
    }
}
