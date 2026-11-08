using System;
using System.Collections.Generic;
using System.Reflection;
using Transform.Core;
using UnityEngine;

namespace ImScoutmaster;

// A warp is a transaction over WORLD poses. Moving a parent before reading a child's
// old pose applies the same displacement/rotation again to nested ragdoll parts.
internal sealed class ScoutmasterBodyPose
{
    private struct PartPose
    {
        internal Bodypart Part;
        internal Rigidbody Rig;
        internal UnityEngine.Transform Transform;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal int Depth;
    }

    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo PreviousPosition = typeof(Bodypart).GetField("prevPos", Fields);
    private static readonly FieldInfo PreviousRotation = typeof(Bodypart).GetField("prevRot", Fields);
    private static readonly FieldInfo TargetRotation = typeof(Bodypart).GetField("targetRotation", Fields);
    private static readonly FieldInfo LastTargetRotation = typeof(Bodypart).GetField("lastTargetRotation", Fields);
    private static readonly FieldInfo TargetForward = typeof(Bodypart).GetField("targetForward", Fields);
    private static readonly FieldInfo TargetUp = typeof(Bodypart).GetField("targetUp", Fields);
    private static readonly FieldInfo TargetOffset = typeof(Bodypart).GetField("targetOffsetRelativeToHip", Fields);
    private readonly List<PartPose> _parts = new List<PartPose>(24);
    private Character _character;
    private Vector3 _center;
    private Vector3 _rootPosition;
    private Quaternion _rootRotation;

    internal ScoutmasterBodyPose(Character character) { Capture(character); }

    internal static void AppendMissingReflectionMembers(List<string> missing)
    {
        foreach (var field in new[] { PreviousPosition, PreviousRotation, TargetRotation, LastTargetRotation, TargetForward, TargetUp, TargetOffset })
            if (field == null) { missing.Add("Bodypart pose/history fields"); break; }
    }

    // The session reuses a separate buffer for following; its entry snapshot stays immutable.
    internal void Capture(Character character)
    {
        _parts.Clear();
        _character = character;
        if (character == null) return;
        var root = character.transform;
        _center = CameraStateSafety.Finite(character.Center) ? character.Center : root.position;
        if (!CameraStateSafety.Finite(_center)) _center = Vector3.zero;
        _rootPosition = CameraStateSafety.Finite(root.position) ? root.position : _center;
        _rootRotation = CameraStateSafety.NormalizeRotation(root.rotation);
        if (character.refs?.ragdoll?.partList == null) return;
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (part == null) continue;
            Rigidbody rig = part.Rig;
            var target = part.transform;
            Vector3 position = rig != null ? rig.position : target.position;
            Quaternion rotation = rig != null ? rig.rotation : target.rotation;
            int depth = 0;
            for (var parent = target.parent; parent != null; parent = parent.parent) depth++;
            _parts.Add(new PartPose {
                Part = part, Rig = rig, Transform = target, Depth = depth,
                Position = CameraStateSafety.Finite(position) ? position : _center,
                Rotation = CameraStateSafety.Rotation(rotation) ? CameraStateSafety.NormalizeRotation(rotation) : _rootRotation
            });
        }
        // Native part lists usually follow hierarchy order. Do not rely on that for clones.
        _parts.Sort((a, b) => a.Depth.CompareTo(b.Depth));
    }

    internal void Apply(Vector3 center, Quaternion rotation, bool moveRoot)
    {
        if (_character == null || !CameraStateSafety.Finite(center)) return;
        rotation = CameraStateSafety.NormalizeRotation(rotation);
        Quaternion delta = rotation * Quaternion.Inverse(_rootRotation);
        if (moveRoot)
            _character.transform.SetPositionAndRotation(center + delta * (_rootPosition - _center), rotation);
        else if (!CameraStateSafety.Finite(_character.transform.position) || !CameraStateSafety.Rotation(_character.transform.rotation))
            _character.transform.SetPositionAndRotation(_rootPosition, _rootRotation);
        foreach (PartPose pose in _parts)
        {
            if (pose.Part == null || pose.Transform == null) continue;
            Vector3 position = center + delta * (pose.Position - _center);
            Quaternion partRotation = CameraStateSafety.NormalizeRotation(delta * pose.Rotation);
            if (!CameraStateSafety.Finite(position)) position = center;
            if (pose.Rig != null)
            {
                pose.Rig.position = position;
                pose.Rig.rotation = partRotation;
                if (!pose.Rig.isKinematic)
                {
                    pose.Rig.linearVelocity = Vector3.zero;
                    pose.Rig.angularVelocity = Vector3.zero;
                }
            }
            pose.Transform.SetPositionAndRotation(position, partRotation);
            pose.Part.forcesToAdd = Vector3.zero;
        }
    }

    internal void TranslateTo(Vector3 center)
    {
        Apply(center, _rootRotation, moveRoot: false);
    }

    // Seed animation/physics history from the restored body before its first dynamic step.
    internal static void ResetDynamics(Character character)
    {
        if (character?.refs?.ragdoll?.partList == null) return;
        Vector3 hip = character.refs.hip != null ? character.refs.hip.transform.position : character.Center;
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (part == null) continue;
            var transform = part.transform;
            part.forcesToAdd = Vector3.zero;
            PreviousPosition?.SetValue(part, transform.position);
            PreviousRotation?.SetValue(part, transform.rotation);
            TargetRotation?.SetValue(part, transform.localRotation);
            LastTargetRotation?.SetValue(part, transform.localRotation);
            TargetForward?.SetValue(part, transform.forward);
            TargetUp?.SetValue(part, transform.up);
            TargetOffset?.SetValue(part, transform.position - hip);
        }
    }
}
