using System.Reflection;
using HarmonyLib;
using Peak;
using UnityEngine;

namespace ImRaven;

/// <summary>
/// While the local player is in Condor form (legacy Raven module identifiers):
///  - the vanilla network pose is the real flight position for modded and unmodded peers;
///    only clients displaying a Condor hide the original body renderers;
///  - vanilla CharacterGrabbing.Update is skipped to prevent reach/kick attacks;
///    the controller releases any stale grab state through RPCA_GrabUnattach;
///  - modded clients hide a remote raven owner's body and position its grab hand at
///    the Condor talons. Hidden-coordinate interpolation is skipped for legacy senders.
/// </summary>
[HarmonyPatch]
public static class RavenHarmonyPatches
{
	private static readonly MethodInfo LimitFallingMethod = AccessTools.Method(typeof(Character), "LimitFalling");
	private static readonly FieldInfo GrabbedPlayerField = AccessTools.Field(typeof(CharacterData), "grabbedPlayer");

	[HarmonyPatch(typeof(CharacterGrabbing), "FixedUpdate")]
	[HarmonyPostfix]
	private static void CharacterGrabbingFixedUpdatePostfix(CharacterGrabbing __instance)
	{
		RavenController.UpdateRemoteGrabAnchor(__instance != null ? __instance.GetComponent<Character>() : null);
	}

	[HarmonyPatch(typeof(CharacterGrabbing), "Update")]
	[HarmonyPrefix]
	private static bool CharacterGrabbingUpdatePrefix(CharacterGrabbing __instance)
	{
		Character character = __instance != null ? __instance.GetComponent<Character>() : null;
		if (!RavenController.IsLocalRavenCharacter(character))
		{
			return true;
		}

		try
		{
			if (character.data != null
			    && character.data.grabJoint != null
			    && GrabbedPlayerField != null)
			{
				Character carried = GrabbedPlayerField.GetValue(character.data) as Character;
				if (carried != null)
				{
					LimitFallingMethod?.Invoke(carried, null);
				}
			}
		}
		catch
		{
		}

		return false;
	}

	// Remote Condor owners: hide the original body. Skip position interpolation only
	// for legacy senders that still transmit an underground pose.
	[HarmonyPatch(typeof(CharacterSyncer), "InterpolateRigPositions")]
	[HarmonyPrefix]
	private static bool CharacterSyncerInterpolatePrefix(CharacterSyncer __instance)
	{
		Character character = __instance != null ? __instance.GetComponent<Character>() : null;
		return !RavenController.IsHiddenRemoteRider(character);
	}

	// Owner side: use the same authoritative flight pose during free flight, grabs and
	// drops. Unmodded peers have no custom event handler to correct a fake position.
	[HarmonyPatch(typeof(CharacterSyncer), "GetDataToWrite")]
	[HarmonyPostfix]
	private static void CharacterSyncerGetDataToWritePostfix(CharacterSyncer __instance, ref CharacterSyncData __result)
	{
		Character character = __instance != null ? __instance.GetComponent<Character>() : null;
		RavenController.WriteLocalNetworkPose(character, ref __result);
	}

	// Camera fallback: after the vanilla MainCameraMovement.LateUpdate finishes, re-point the
	// camera at the flying raven (applied via AccessTools in RavenPlugin, so the target type
	// and method can be resolved defensively).
	internal static void MainCameraMovementLateUpdatePostfix()
	{
		RavenController.ApplyCameraOverrideForLocalRaven();
	}
}
