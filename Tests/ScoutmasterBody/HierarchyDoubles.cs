using System;
using System.Collections.Generic;
using NQuaternion = System.Numerics.Quaternion;
using NVector = System.Numerics.Vector3;

namespace UnityEngine
{
    // Model nested, kinematic Transform parenting, not Unity's physics solver/rendering.
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 zero => default;
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
    }
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        private NQuaternion Native => new NQuaternion(x, y, z, w);
        private static Quaternion From(NQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        public static Quaternion Inverse(Quaternion q) => From(NQuaternion.Inverse(q.Native));
        public static Quaternion operator *(Quaternion a, Quaternion b) => From(a.Native * b.Native);
        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            var r = NVector.Transform(new NVector(v.x, v.y, v.z), q.Native);
            return new Vector3(r.X, r.Y, r.Z);
        }
    }
    public class Transform
    {
        public Transform parent;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 position
        {
            get => parent == null ? localPosition : parent.position + parent.rotation * localPosition;
            set => localPosition = parent == null ? value : Quaternion.Inverse(parent.rotation) * (value - parent.position);
        }
        public Quaternion rotation
        {
            get => parent == null ? localRotation : parent.rotation * localRotation;
            set => localRotation = parent == null ? value : Quaternion.Inverse(parent.rotation) * value;
        }
        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 up => rotation * Vector3.up;
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { position = p; rotation = r; }
    }
    public class Rigidbody
    {
        public Transform transform;
        public bool isKinematic;
        public Vector3 position { get => transform.position; set => transform.position = value; }
        public Quaternion rotation { get => transform.rotation; set => transform.rotation = value; }
        public Vector3 linearVelocity, angularVelocity;
    }
    // Camera APIs compile the shared production validator but are deliberately unavailable here.
    public struct Matrix4x4
    {
        public float m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33;
        public float this[int index] => throw new NotSupportedException("No camera matrix simulation");
    }
    public class Camera
    {
        public float fieldOfView, nearClipPlane, farClipPlane;
        public bool orthographic;
        public Matrix4x4 projectionMatrix, worldToCameraMatrix, cullingMatrix;
        public void ResetProjectionMatrix() => throw new NotSupportedException();
        public void ResetWorldToCameraMatrix() => throw new NotSupportedException();
        public void ResetCullingMatrix() => throw new NotSupportedException();
    }
}
public class Bodypart
{
    public UnityEngine.Transform transform;
    public UnityEngine.Rigidbody Rig;
    public UnityEngine.Vector3 forcesToAdd;
#pragma warning disable CS0169
    private UnityEngine.Vector3 prevPos, targetForward, targetUp, targetOffsetRelativeToHip;
    private UnityEngine.Quaternion prevRot, targetRotation, lastTargetRotation;
#pragma warning restore CS0169
}
public class CharacterRefs
{
    public CharacterRagdoll ragdoll = new CharacterRagdoll();
    public Bodypart hip;
}
public class CharacterRagdoll { public List<Bodypart> partList = new List<Bodypart>(); }
public class Character
{
    public UnityEngine.Transform transform = new UnityEngine.Transform();
    public CharacterRefs refs = new CharacterRefs();
    public Bodypart Torso;
    public UnityEngine.Vector3 Center => Torso.transform.position;
}
