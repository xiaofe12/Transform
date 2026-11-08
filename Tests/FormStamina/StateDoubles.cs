using System.Collections;
using System.Collections.Generic;

// State doubles only. Native stamina entry paths are checked separately against game IL.
namespace UnityEngine
{
    public class Coroutine { }
    public class Transform : IEnumerable<Transform>
    {
        public GameObject gameObject;
        public readonly List<Transform> Children = new List<Transform>();
        public IEnumerator<Transform> GetEnumerator() => Children.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public class GameObject
    {
        public readonly Transform transform;
        public bool activeSelf = true;
        public GameObject() { transform = new Transform { gameObject = this }; }
        public void SetActive(bool active) { activeSelf = active; }
        public static GameObject Find(string name) => null;
    }
    public class UiElement
    {
        public readonly GameObject gameObject = new GameObject();
        public Transform transform => gameObject.transform;
    }
    public static class Time { public static float unscaledTime; }
}
public class Character
{
    public bool infiniteStam { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] get; set; }
    public float stamina;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public float GetTotalStamina() => stamina;
}
public class GUIManager
{
    public static GUIManager instance;
    public UnityEngine.UiElement hudCanvas, staminaCanvasGroup, bar;
}
namespace ImZombie { public static class ZombieController { public static Character ActiveZombieCharacter; } }
namespace ImScoutmaster
{
    public static class Plugin
    {
        public static Character Controlled;
        public static bool IsLocallyControlledScoutmasterCharacter(Character character) => character != null && character == Controlled;
    }
}
namespace Transform
{
    public class TransformPlugin
    {
        public static TransformPlugin Instance;
        public bool isActiveAndEnabled;
        public void StopCoroutine(UnityEngine.Coroutine routine) { }
        public UnityEngine.Coroutine StartCoroutine(IEnumerator routine) => new UnityEngine.Coroutine();
    }
}
namespace Transform.Core
{
    public static class FormRegistry { public static bool AnyActive; }
    public static class ThirdPartyCameras { public static bool ExternalCameraActive; }
}
