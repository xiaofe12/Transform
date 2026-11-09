using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Transform.Core;

/// <summary>One local form session. Only occupied/invalid exit positions are replaced.</summary>
internal static class RestoreSafety
{
    private static readonly SafeRestoreHistory History = new SafeRestoreHistory();
    private static readonly Collider[] Hits = new Collider[64];
    private static readonly List<GameObject> IgnoredRoots = new List<GameObject>();
    private static Character _source;
    private static int _scene;
    private static float _nextSample;
    private static Vector3 _entryRootOffset;
    private static readonly Dictionary<Rigidbody, Vector3> EntryBodyOffsets = new Dictionary<Rigidbody, Vector3>();
    internal static Vector3 EntryPosition { get; private set; }

    internal static void Begin(Character source)
    {
        Reset();
        if (source == null) return;
        _source = source;
        EntryPosition = source.Center;
        _scene = SceneManager.GetActiveScene().handle;
        History.Reset(source.Center);
        _entryRootOffset = source.transform.position - source.Center;
        if (source.refs?.ragdoll?.partList != null)
            foreach (Bodypart part in source.refs.ragdoll.partList)
                if (part != null && part.Rig != null)
                    EntryBodyOffsets[part.Rig] = part.Rig.position - source.Center;
        History.Observe(source.Center, IsClear);
    }

    internal static void Ignore(GameObject root)
    {
        if (_source != null && root != null && !IgnoredRoots.Contains(root)) IgnoredRoots.Add(root);
    }

    internal static void Tick()
    {
        if (!ValidSession || !FormRegistry.AnyActive || Time.unscaledTime < _nextSample) return;
        _nextSample = Time.unscaledTime + 0.2f;
        Character local = Character.localCharacter;
        if (local == null) return;
        FormId? id = FormRegistry.ActiveForm?.Id;
        if (id == FormId.Scoutmaster) local = ImScoutmaster.Plugin.GetControlledScoutmasterCharacter() ?? local;
        bool inPlace = id == FormId.Ghost || id == FormId.Raven || id == FormId.Crow || id == FormId.Tornado;
        // Ground/item forms already add this clearance when they restore. Sample
        // the same torso location, rather than the small item's ground-level pivot.
        History.Observe(local.Center + (inPlace ? Vector3.zero : Vector3.up * 0.6f), IsClear);
    }

    private static bool ValidSession => _source != null && _scene == SceneManager.GetActiveScene().handle;

    internal static Vector3 Resolve(Vector3 requested)
    {
        if (!ValidSession) return requested;
        Physics.SyncTransforms();
        if (History.TryResolve(requested, IsClear, out Vector3 result))
        {
            if (!SafeRestoreHistory.IsFinite(requested) || (result - requested).sqrMagnitude > 0.0001f)
                TransformPlugin.Log?.LogInfo("[Transform] Blocked restore position; using safe anchor " + result);
            return result;
        }
        TransformPlugin.Log?.LogWarning("[Transform] No clear restore anchor found in the bounded search area.");
        return SafeRestoreHistory.IsFinite(requested) ? requested : EntryPosition;
    }

    internal static void MoveBeforePhysics(Character character)
    {
        if (character == null || !ValidSession) return;
        Vector3 center = character.Center;
        Vector3 target = Resolve(center);
        if (!SafeRestoreHistory.IsFinite(target)) return;
        if (!SafeRestoreHistory.IsFinite(center) || !SafeRestoreHistory.IsFinite(character.transform.position))
        {
            character.transform.position = target + (SafeRestoreHistory.IsFinite(_entryRootOffset) ? _entryRootOffset : Vector3.zero);
            foreach (var pair in EntryBodyOffsets)
                if (pair.Key != null)
                    pair.Key.position = target + (SafeRestoreHistory.IsFinite(pair.Value) ? pair.Value : Vector3.zero);
            Physics.SyncTransforms();
            return;
        }
        Vector3 delta = target - center;
        if (!SafeRestoreHistory.IsFinite(delta) || delta.sqrMagnitude <= 0.0001f) return;
        character.transform.position += delta;
        // Commit kinematic body poses as well; cached physics poses need not have
        // followed a root Transform write yet when interpolation was disabled.
        if (character.refs?.ragdoll?.partList != null)
            foreach (Bodypart part in character.refs.ragdoll.partList)
                if (part != null && part.Rig != null) part.Rig.position = part.transform.position;
        Physics.SyncTransforms();
    }

    private static bool IsClear(Vector3 position)
    {
        int count = Physics.OverlapCapsuleNonAlloc(position + Vector3.up * 0.3f,
            position - Vector3.up * 0.3f, 0.28f, Hits, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        bool clear = count < Hits.Length; // Saturated queries cannot establish safety.
        for (int i = 0; i < count; i++)
        {
            Collider hit = Hits[i];
            Hits[i] = null;
            if (hit == null || hit.GetComponentInParent<Character>() != null
                || (CharacterRagdoll.TryGetCharacterFromCollider(hit, out Character owner) && owner != null)) continue;
            bool ignored = false;
            foreach (GameObject root in IgnoredRoots)
                if (root != null && hit.transform.IsChildOf(root.transform)) { ignored = true; break; }
            if (!ignored) clear = false;
        }
        return clear;
    }

    internal static void Reset() { _source = null; IgnoredRoots.Clear(); EntryBodyOffsets.Clear(); _nextSample = 0f; }
}
