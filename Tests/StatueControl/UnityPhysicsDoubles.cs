// Managed state only: native Unity simulation needs an in-game test.
namespace UnityEngine;

public enum RigidbodyInterpolation { None, Interpolate, Extrapolate }

public sealed class GameObject
{
	public bool activeInHierarchy = true;
}

public sealed class Rigidbody
{
	public readonly GameObject gameObject = new GameObject();
	public bool isKinematic;
	public bool useGravity = true;
	public bool detectCollisions = true;
	public RigidbodyInterpolation interpolation;
	public bool sleeping = true;
	public void WakeUp() => sleeping = false;
}
