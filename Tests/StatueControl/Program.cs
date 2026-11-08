using System;
using Transform.Statue;
using UnityEngine;

internal static class Program
{
	private static void Require(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	private static void Main()
	{
		// Match the prefab: one live hip body plus inactive break fragments.
		var hip = new Rigidbody { isKinematic = true, useGravity = false };
		var fragment = new Rigidbody();
		fragment.gameObject.activeInHierarchy = false;
		var driven = StatuePhysicsState.PrepareDrivenBodies(new[] { fragment, null, hip });
		Require(driven.Length == 1 && driven[0] == hip, "Only the intact body can be driven");
		Require(!hip.isKinematic && hip.useGravity && !hip.sleeping
		        && hip.interpolation == RigidbodyInterpolation.Interpolate, "Live body is ready for force and rendering");
		Require(fragment.sleeping && fragment.interpolation == RigidbodyInterpolation.None, "Break fragments retain native state");
		Require(StatuePhysicsState.PrepareDrivenBodies(new[] { fragment, null }).Length == 0,
		        "Missing live body must fail entry rather than accept an uncontrollable form");

		var rider = new Rigidbody();
		var previouslyNonColliding = new Rigidbody { detectCollisions = false };
		var state = new StatuePhysicsState(new[] { rider, null, previouslyNonColliding });
		state.IsolateRider();
		Require(rider.isKinematic && !rider.useGravity && !rider.detectCollisions, "Rider is isolated at entry");

		// The native warp coroutine can enable colliders AFTER controller callbacks.
		// Even then, the body's collision switch prevents the overlapping rider from
		// entering the solver. Check both orders for the late native collider write.
		foreach (bool warpRunsAfterController in new[] { false, true })
		{
			bool colliderEnabled = false;
			if (!warpRunsAfterController) colliderEnabled = true;
			state.IsolateRider();
			if (warpRunsAfterController) colliderEnabled = true;
			Require(!(colliderEnabled && rider.detectCollisions), "Late collider enable cannot restore rider contacts");
		}

		// Another transition can also change the body flags. Recover on the next
		// controller tick, then restore the ORIGINAL collision flags when exiting.
		rider.detectCollisions = true;
		rider.isKinematic = false;
		rider.useGravity = true;
		state.IsolateRider();
		Require(rider.isKinematic && !rider.detectCollisions && !rider.useGravity, "Recover isolation after an external state change");
		state.RestoreRiderCollisions();
		Require(rider.detectCollisions && !previouslyNonColliding.detectCollisions, "Exit restores individual collision settings");

		// Re-entering must capture fresh state rather than reusing the prior snapshot.
		rider.detectCollisions = false;
		previouslyNonColliding.detectCollisions = true;
		var reentry = new StatuePhysicsState(new[] { rider, previouslyNonColliding });
		reentry.IsolateRider();
		reentry.RestoreRiderCollisions();
		Require(!rider.detectCollisions && previouslyNonColliding.detectCollisions, "Re-entry has its own collision snapshot");
		Console.WriteLine("PASS: live-body selection, initialization, missing-body rejection, rider isolation, both warp callback orders, isolation recovery, exit restoration and re-entry.");
	}
}
