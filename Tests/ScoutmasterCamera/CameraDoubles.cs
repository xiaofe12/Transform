using System;
using System.Collections.Generic;

namespace UnityEngine
{
    // State doubles for native camera ownership. No Unity scene/rendering is simulated.
    [AttributeUsage(AttributeTargets.Class)] public sealed class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int order) { } }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Quaternion { public float w; }
    public class Transform
    {
        public Vector3 position; public Quaternion rotation;
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { position = p; rotation = r; }
    }
    public class MonoBehaviour
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : MonoBehaviour => gameObject.GetComponent<T>();
    }
    public class Camera : MonoBehaviour { public float fieldOfView = 70f; }
    public class GameObject
    {
        public readonly Transform transform = new Transform();
        private readonly Dictionary<Type, MonoBehaviour> _components = new Dictionary<Type, MonoBehaviour>();
        public T AddComponent<T>() where T : MonoBehaviour, new() { var c = new T { gameObject = this }; _components[typeof(T)] = c; return c; }
        public T GetComponent<T>() where T : MonoBehaviour => _components.TryGetValue(typeof(T), out var c) ? (T)c : null;
    }
}
public class Character { }
namespace Photon.Pun { public class PhotonView { public object Owner; } }
public class CharacterData
{
    private Photon.Pun.PhotonView m_photonView;
    private static readonly Dictionary<object, CharacterData> m_instances = new Dictionary<object, CharacterData>();
    public CharacterData(object owner = null, bool register = false)
    {
        if (owner != null) m_photonView = new Photon.Pun.PhotonView { Owner = owner };
        if (register) m_instances[owner] = this;
    }
}
public class CameraOverride : UnityEngine.MonoBehaviour { public float fov; }
public class MainCamera : UnityEngine.MonoBehaviour
{
    public static MainCamera instance;
    internal CameraOverride camOverride;
    public bool FailNextSet;
    public void SetCameraOverride(CameraOverride value)
    {
        if (FailNextSet) { FailNextSet = false; throw new InvalidOperationException("Camera teardown failure"); }
        camOverride = value;
    }
}
public class MainCameraMovement { public static Character specCharacter { get; set; } }
namespace ImScoutmaster { public static class Plugin { internal static void RefreshControlledScoutmasterCamera(object movement) { } } }
