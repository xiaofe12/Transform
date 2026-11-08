using System;
using System.Collections.Generic;
using ImRaven;
using Transform.Core;
using UnityEngine;

internal static class Program
{
    private static int _assertions;
    private static void Require(bool condition, string reason)
    {
        _assertions++;
        if (!condition) throw new Exception(reason);
    }

    private static void CleanupFailures()
    {
        // Inject a failure into every possible stage, including multiple failures
        // and a logger that also throws. Later stages must still execute in order.
        for (int mask = 0; mask < 256; mask++)
        {
            var ran = new List<int>();
            int reported = 0;
            var cleanup = new CleanupSequence((step, error) => { reported++; throw new Exception("logger unavailable"); });
            for (int stage = 0; stage < 8; stage++)
            {
                int current = stage;
                cleanup.Step("stage " + stage, () =>
                {
                    ran.Add(current);
                    if ((mask & (1 << current)) != 0) throw new InvalidOperationException("injected failure");
                });
            }
            Require(ran.Count == 8, "Failure cannot truncate remaining cleanup stages");
            for (int stage = 0; stage < 8; stage++) Require(ran[stage] == stage, "Cleanup order is preserved");
            Require(cleanup.Succeeded == (mask == 0), "Failure status is retained");
            int expected = 0;
            for (int bit = 0; bit < 8; bit++) if ((mask & (1 << bit)) != 0) expected++;
            Require(reported == expected, "Every failed step is reported independently");
        }
        var silent = new CleanupSequence(null);
        silent.Step("failure", () => throw new Exception());
        Require(!silent.Succeeded, "Null reporter is supported");
    }

    private static void SafePositions()
    {
        var history = new SafeRestoreHistory();
        var entry = new Vector3(2, 5, 8);
        var last = new Vector3(10, 20, 30);
        var requested = new Vector3(60, 80, 90);
        history.Reset(entry);
        history.Observe(last, p => true);
        Require(history.TryResolve(requested, p => true, out var point) && point == requested,
            "Clear request is exact, including flight altitude; never snap to ground");
        Require(history.TryResolve(Vector3.zero, p => true, out point) && point == Vector3.zero, "World origin is a valid target");
        Require(history.TryResolve(requested, p => p == last || p == entry, out point) && point == last,
            "Prefer recent clear anchor over entry");
        Require(history.TryResolve(requested, p => p == entry, out point) && point == entry,
            "Recheck last anchor: a moving obstacle can invalidate it");
        Require(history.TryResolve(new Vector3(float.NaN, 0, 0), p => p == entry, out point) && point == entry,
            "NaN target falls back without being sent to the collision predicate");
        history.Observe(new Vector3(float.PositiveInfinity, 0, 0), p => throw new Exception("must not query NaN/infinity"));
        Require(history.TryResolve(requested, p => p == last, out point) && point == last, "Invalid sample cannot poison history");
        Require(history.TryResolve(requested, p => p == last + Vector3.up, out point) && point == last + Vector3.up,
            "Bounded nearby search if both anchors are now occupied");
        Require(history.TryResolve(requested, p => p == entry + Vector3.up, out point) && point == entry + Vector3.up,
            "Also search near entry if the recent anchor's entire neighborhood is occupied");
        Require(!history.TryResolve(requested, p => false, out point), "All blocked is explicitly unresolved");
        history.Reset(entry);
        Require(history.TryResolve(requested, p => p == entry || p == last, out point) && point == entry,
            "New session has no anchor from a previous scene/form");
        history.Reset(new Vector3(float.NaN, 0, 0));
        Require(!history.TryResolve(requested, p => false, out point), "Invalid entry cannot start a nonfinite search");

        var random = new System.Random(39);
        for (int i = 0; i < 3000; i++)
        {
            var p = new Vector3((float)random.NextDouble() * 2000f - 1000f, (float)random.NextDouble() * 300f, (float)random.NextDouble() * 2000f);
            history.Reset(p + Vector3.up * 20);
            Require(history.TryResolve(p, q => true, out point) && point == p, "Any clear target is preserved exactly");
            Require(history.TryResolve(p + Vector3.one * 20, q => (q - p).sqrMagnitude < 0.01f, out point) == false,
                "Bounded search does not teleport arbitrarily far to a clear point");
        }
    }

    private static CondorFlightMode Settle(CondorFlightState state, Vector3 velocity, int fps = 60)
    {
        for (int frame = 0; frame < fps * 3; frame++) state.Step(velocity, 1f / fps, false);
        return state.Mode;
    }

    private static void FlightModes()
    {
        foreach (int fps in new[] { 30, 60, 120, 144, 240 })
        {
            var state = new CondorFlightState();
            Require(state.Step(Vector3.zero, 1f / fps, false) == CondorFlightMode.Flap, "Stationary birds flap");
            Require(Settle(state, Vector3.forward * 12, fps) == CondorFlightMode.Flap, "Low speed stays flapping");
            Require(Settle(state, Vector3.forward * 14, fps) == CondorFlightMode.Glide, "Fast flight enters glide");
            Require(Settle(state, Vector3.forward * 12, fps) == CondorFlightMode.Glide, "Glide holds between separate thresholds");
            Require(Settle(state, Vector3.forward * 9, fps) == CondorFlightMode.Flap, "Slow down exits glide");
            Require(Settle(state, new Vector3(20, 2, 0), fps) == CondorFlightMode.Flap, "Rising uses flap even at high speed");
            Require(Settle(state, new Vector3(20, -8, 0), fps) == CondorFlightMode.Dive, "Fast descent enters dive");
            Require(Settle(state, new Vector3(20, -6, 0), fps) == CondorFlightMode.Dive, "Dive has its own exit threshold");
            Require(Settle(state, new Vector3(20, -4, 0), fps) == CondorFlightMode.Glide, "Slow descent leaves dive");
            Require(state.Step(new Vector3(0, -30, 0), 1f / fps, true) == CondorFlightMode.Flap, "Legacy carrying presentation is preserved");
            Require(Settle(state, new Vector3(float.NaN, 0, 0), fps) == CondorFlightMode.Flap, "Invalid network velocity cannot poison the state");
        }
        // Verify smoothing makes transition timing effectively independent of FPS.
        float reference = -1f;
        foreach (int fps in new[] { 30, 60, 120, 144, 240 })
        {
            var state = new CondorFlightState();
            state.Step(Vector3.zero, 0, false);
            float time = 0;
            while (state.Mode != CondorFlightMode.Glide && time < 1f) { state.Step(Vector3.forward * 20, 1f / fps, false); time += 1f / fps; }
            if (reference < 0) reference = time;
            Require(Math.Abs(time - reference) <= 1f / 30f, "Smoothed animation transition is frame-rate independent");
        }
    }

    private static void Main()
    {
        CleanupFailures();
        SafePositions();
        FlightModes();
        Console.WriteLine("PASS: " + _assertions + " assertions (cleanup failure injection, safe restore selection, Condor flight transitions).");
    }
}
