using System;
using UnityEngine;

namespace Transform.Core;

/// <summary>Candidate selection only; does not snap a valid position to the ground.</summary>
internal sealed class SafeRestoreHistory
{
    private Vector3 _entry;
    private Vector3 _lastClear;
    private bool _hasLastClear;
    internal void Reset(Vector3 entry) { _entry = entry; _hasLastClear = false; }
    internal static bool IsFinite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x)
        && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);

    internal void Observe(Vector3 position, Func<Vector3, bool> clear)
    {
        if (IsFinite(position) && clear(position)) { _lastClear = position; _hasLastClear = true; }
    }

    internal bool TryResolve(Vector3 requested, Func<Vector3, bool> clear, out Vector3 result)
    {
        result = requested;
        if (IsFinite(requested) && clear(requested)) return true;
        if (_hasLastClear && clear(_lastClear)) { result = _lastClear; return true; }
        if (IsFinite(_entry) && clear(_entry)) { result = _entry; return true; }
        // A moving prop may now occupy both saved anchors. Search a bounded area
        // around the last usable anchor rather than teleporting to an arbitrary floor.
        for (int anchor = 0; anchor < (_hasLastClear ? 2 : 1); anchor++)
        {
            Vector3 origin = _hasLastClear && anchor == 0 ? _lastClear : _entry;
            if (!IsFinite(origin)) continue;
            for (int ring = 0; ring <= 3; ring++)
            for (int height = 0; height <= 3; height++)
            for (int direction = 0; direction < (ring == 0 ? 1 : 8); direction++)
            {
                float angle = direction * Mathf.PI / 4f;
                Vector3 candidate = origin + new Vector3(Mathf.Cos(angle) * ring, height * 0.5f, Mathf.Sin(angle) * ring);
                if (clear(candidate)) { result = candidate; return true; }
            }
        }
        return false;
    }
}
