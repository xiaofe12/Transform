using System;
using UnityEngine;

namespace Transform.Core;

// Shared boundary checks for poses/projections read from native ragdolls and camera drivers.
internal static class CameraStateSafety
{
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    internal static bool Direction(Vector3 value) => Finite(value) && Finite(value.sqrMagnitude) && value.sqrMagnitude > 0.0001f;

    internal static bool Rotation(Quaternion value)
    {
        double norm = (double)value.x * value.x + (double)value.y * value.y
            + (double)value.z * value.z + (double)value.w * value.w;
        return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w)
            && norm > 0.000001 && norm < 1000000;
    }

    internal static Quaternion NormalizeRotation(Quaternion value)
    {
        if (!Rotation(value)) return Quaternion.identity;
        double scale = 1.0 / Math.Sqrt((double)value.x * value.x + (double)value.y * value.y
            + (double)value.z * value.z + (double)value.w * value.w);
        return new Quaternion((float)(value.x * scale), (float)(value.y * scale),
            (float)(value.z * scale), (float)(value.w * scale));
    }

    internal static Vector2 Look(Vector2 value)
    {
        float yaw = Finite(value.x) ? value.x % 360f : 0f;
        float pitch = Finite(value.y) ? Math.Max(-85f, Math.Min(85f, value.y)) : 0f;
        return new Vector2(yaw, pitch);
    }

    internal static bool Matrix(Matrix4x4 m)
    {
        for (int i = 0; i < 16; i++) if (!Finite(m[i])) return false;
        // Use double precision: a valid distant/oblique perspective may have a small determinant.
        double a = (double)m.m00 * m.m11 - (double)m.m01 * m.m10;
        double b = (double)m.m00 * m.m12 - (double)m.m02 * m.m10;
        double c = (double)m.m00 * m.m13 - (double)m.m03 * m.m10;
        double d = (double)m.m01 * m.m12 - (double)m.m02 * m.m11;
        double e = (double)m.m01 * m.m13 - (double)m.m03 * m.m11;
        double f = (double)m.m02 * m.m13 - (double)m.m03 * m.m12;
        double determinant = a * ((double)m.m22 * m.m33 - (double)m.m23 * m.m32)
            - b * ((double)m.m21 * m.m33 - (double)m.m23 * m.m31)
            + c * ((double)m.m21 * m.m32 - (double)m.m22 * m.m31)
            + d * ((double)m.m20 * m.m33 - (double)m.m23 * m.m30)
            - e * ((double)m.m20 * m.m32 - (double)m.m22 * m.m30)
            + f * ((double)m.m20 * m.m31 - (double)m.m21 * m.m30);
        return !double.IsNaN(determinant) && !double.IsInfinity(determinant) && Math.Abs(determinant) > 1e-30;
    }

    // Native Mirror returns identity when its corner rays miss the near plane. Identity is
    // invertible, but is not a perspective projection and breaks Unity's screen/world conversion.
    internal static bool Perspective(Matrix4x4 m) => Matrix(m) && Math.Abs(m.m33) < 0.000001f
        && Math.Abs(m.m32) > 0.000001f && Math.Abs(m.m00) > 0.000001f && Math.Abs(m.m11) > 0.000001f;

    internal static void RepairMatrices(Camera camera)
    {
        if (camera == null) return;
        if (!Finite(camera.fieldOfView) || camera.fieldOfView < 1f || camera.fieldOfView > 179f) camera.fieldOfView = 70f;
        if (!Finite(camera.nearClipPlane) || camera.nearClipPlane <= 0f) camera.nearClipPlane = 0.01f;
        if (!Finite(camera.farClipPlane) || camera.farClipPlane <= camera.nearClipPlane) camera.farClipPlane = camera.nearClipPlane + 1000f;
        if (!Matrix(camera.worldToCameraMatrix)) camera.ResetWorldToCameraMatrix();
        if (!Matrix(camera.projectionMatrix) || (!camera.orthographic && !Perspective(camera.projectionMatrix))) camera.ResetProjectionMatrix();
        if (!Matrix(camera.cullingMatrix)) camera.ResetCullingMatrix();
    }
}
