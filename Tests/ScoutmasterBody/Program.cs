using System;
using System.Linq;
using System.Reflection;
using ImScoutmaster;
using Transform.Core;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string reason) { _checks++; if (!value) throw new Exception(reason); }
    private static void Near(Vector3 a, Vector3 b, string reason, float tolerance = 0.002f)
        => Check(CameraStateSafety.Finite(a) && (a - b).sqrMagnitude <= tolerance * tolerance, reason);
    private static Quaternion Yaw(float degrees)
    {
        double half = degrees * Math.PI / 360;
        return new Quaternion(0, (float)Math.Sin(half), 0, (float)Math.Cos(half));
    }
    private static Character Body()
    {
        var c = new Character(); c.transform.position = new Vector3(10, 3, -20);
        Bodypart Add(UnityEngine.Transform parent, Vector3 offset, bool hasRig = true)
        {
            var t = new UnityEngine.Transform { parent = parent, localPosition = offset };
            var p = new Bodypart { transform = t, Rig = hasRig ? new Rigidbody { transform = t, isKinematic = true } : null };
            c.refs.ragdoll.partList.Add(p); return p;
        }
        var hip = Add(c.transform, Vector3.up); c.refs.hip = hip;
        var torso = Add(hip.transform, Vector3.up); c.Torso = torso;
        var head = Add(torso.transform, Vector3.up);
        Add(torso.transform, new Vector3(1, 0, 0));
        Add(hip.transform, new Vector3(0, -1, 0));
        Add(head.transform, new Vector3(0, 0.1f, 0), false);
        return c;
    }
    private static void LegacyReproducer()
    {
        var c = Body(); var oldCenter = c.Center; var target = new Vector3(20, -30, 80);
        var head = c.refs.ragdoll.partList[2]; var expectedHead = target + (head.transform.position - oldCenter);
        // Previous production order: move root, then read each child's now-modified old pose.
        c.transform.position += target - oldCenter;
        foreach (var p in c.refs.ragdoll.partList)
            p.transform.position = target + (p.transform.position - oldCenter);
        Check((head.transform.position - expectedHead).sqrMagnitude > 100, "Reproducer exposes finite head displacement missed by NaN-only guards");
    }
    private static void Warps()
    {
        foreach (bool reverse in new[] { false, true })
        foreach (float yaw in new[] { 0f, 35f, -110f, 180f })
        {
            var c = Body(); c.transform.rotation = Yaw(25);
            var parts = c.refs.ragdoll.partList.ToArray();
            Vector3 center = c.Center, root = c.transform.position;
            Vector3[] before = parts.Select(p => p.transform.position).ToArray();
            if (reverse) c.refs.ragdoll.partList.Reverse();
            var saved = new ScoutmasterBodyPose(c);
            var target = new Vector3(30, -30, 70); var rotation = Yaw(yaw);
            var delta = rotation * Quaternion.Inverse(c.transform.rotation);
            saved.Apply(target, rotation, true);
            Near(c.Center, target, "World-space torso target is exact with nested/reordered parts");
            for (int i = 0; i < parts.Length; i++)
                Near(parts[i].transform.position, target + delta * (before[i] - center), "Bone geometry survives one root/child transaction");
            Near(c.transform.position, target + delta * (root - center), "Root offset rotates around body center consistently");
        }
    }
    private static void FollowAndRestore()
    {
        var c = Body(); var parts = c.refs.ragdoll.partList.ToArray();
        var entry = new ScoutmasterBodyPose(c);
        var entryCenter = c.Center;
        var offsets = parts.Select(p => p.transform.position - entryCenter).ToArray();
        entry.Apply(new Vector3(10, -30, -20), Quaternion.identity, false);
        Vector3 frozenRoot = c.transform.position;
        var follow = new ScoutmasterBodyPose(c);
        for (int frame = 0; frame < 500; frame++)
        {
            var target = new Vector3(30 + frame * 0.25f, 10 + frame * 0.1f, -100 + frame * 0.5f);
            follow.Capture(c); follow.TranslateTo(target);
            Near(c.Center, target, "Repeated following reaches creature center without drift");
            Near(c.transform.position, frozenRoot, "Following leaves source root and skeleton rotation alone");
            for (int i = 0; i < parts.Length; i++)
                Near(parts[i].transform.position - c.Center, offsets[i], "Frozen nested geometry never stretches while following");
        }
        foreach (var p in parts) p.forcesToAdd = new Vector3(float.NaN, 9, 9);
        // Restore from immutable entry geometry, even when live poses are finite but enormous.
        parts[2].transform.position = new Vector3(1000000, -1000000, 1000000);
        var restore = new Vector3(3, 6, 9); var rotation = Yaw(90);
        entry.Apply(restore, rotation, false); ScoutmasterBodyPose.ResetDynamics(c);
        Near(c.transform.position, frozenRoot, "Source hide/restore preserves its native root throughout the session");
        Near(c.Center, restore, "Restore does not trust the followed/deformed body as its source");
        for (int i = 0; i < parts.Length; i++)
        {
            Near(parts[i].transform.position, restore + rotation * offsets[i], "Entry bone shape is restored with exit yaw");
            Near(parts[i].forcesToAdd, Vector3.zero, "No queued force is released on physics wake-up");
            var fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var cached = (Vector3)typeof(Bodypart).GetField("prevPos", fields).GetValue(parts[i]);
            Near(cached, parts[i].transform.position, "Native body history is reseeded from the restored pose");
            var relative = (Vector3)typeof(Bodypart).GetField("targetOffsetRelativeToHip", fields).GetValue(parts[i]);
            Near(relative, parts[i].transform.position - c.refs.hip.transform.position, "Animation target offsets match the restored geometry");
        }
        // A subsequent entry captures the newly returned player rather than reusing stale state.
        var next = new ScoutmasterBodyPose(c);
        next.Apply(new Vector3(20, 5, 40), Yaw(-45), true);
        Near(c.Center, new Vector3(20, 5, 40), "Reentry remains stable");

        // Entry geometry also recovers nonfinite live body/root state, independently of NaN comparisons.
        var healthy = new ScoutmasterBodyPose(c);
        c.transform.position = new Vector3(float.NaN, 0, 0);
        c.transform.rotation = default;
        healthy.Apply(new Vector3(4, 5, 6), Quaternion.identity, false);
        Near(c.Center, new Vector3(4, 5, 6), "Immutable geometry can recover a corrupted root without following its NaN poses");
        Check(CameraStateSafety.Rotation(c.transform.rotation), "Source root recovery also replaces zero quaternions");
    }
    private static void Main()
    {
        LegacyReproducer(); Warps(); FollowAndRestore();
        Console.WriteLine("PASS: " + _checks + " Scoutmaster nested-body checks: old-order reproducer, world-pose transaction, frozen follow, geometry restore, force/history reset and reentry.");
    }
}
