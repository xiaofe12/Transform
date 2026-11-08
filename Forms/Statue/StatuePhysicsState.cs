using System.Collections.Generic;
using UnityEngine;

namespace Transform.Statue;

/// <summary>Physics state owned by one local statue transformation.</summary>
internal sealed class StatuePhysicsState
{
	private readonly Rigidbody[] _riderBodies;
	private readonly bool[] _riderCollisionStates;

	internal StatuePhysicsState(Rigidbody[] riderBodies)
	{
		_riderBodies = riderBodies;
		_riderCollisionStates = new bool[riderBodies.Length];
		for (int i = 0; i < riderBodies.Length; i++)
		{
			if (riderBodies[i] != null) _riderCollisionStates[i] = riderBodies[i].detectCollisions;
		}
	}

	internal static Rigidbody[] PrepareDrivenBodies(Rigidbody[] candidates)
	{
		var bodies = new List<Rigidbody>();
		foreach (Rigidbody body in candidates)
		{
			// The prefab also contains inactive break fragments. They must keep their
			// original state and must never be driven after the statue shatters.
			if (body == null || !body.gameObject.activeInHierarchy) continue;
			body.isKinematic = false;
			body.useGravity = true;
			body.interpolation = RigidbodyInterpolation.Interpolate;
			body.WakeUp();
			bodies.Add(body);
		}
		return bodies.ToArray();
	}

	internal void IsolateRider()
	{
		foreach (Rigidbody body in _riderBodies)
		{
			if (body == null) continue;
			// Character.WarpPlayer's coroutine re-enables the ragdoll colliders.
			// Disable collisions on the bodies as well, so that late collider writes
			// cannot pin the driven statue against the invisible, overlapping rider.
			body.detectCollisions = false;
			body.isKinematic = true;
			body.useGravity = false;
		}
	}

	internal void RestoreRiderCollisions()
	{
		for (int i = 0; i < _riderBodies.Length; i++)
		{
			Rigidbody body = _riderBodies[i];
			if (body != null) body.detectCollisions = _riderCollisionStates[i];
		}
	}
}
