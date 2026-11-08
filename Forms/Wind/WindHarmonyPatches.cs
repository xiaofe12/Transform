using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;

namespace ImTornado;

/// <summary>
/// While the local player is in tornado form, block the game's death, pass-out,
/// and warp RPCs so the tornado cannot be killed or teleported away by the game.
/// </summary>
[HarmonyPatch]
public static class WindHarmonyPatches
{
	private static readonly FieldInfo BananaPeelItemField =
		typeof(BananaPeel).GetField("item", BindingFlags.NonPublic | BindingFlags.Instance);

	private static readonly FieldInfo ItemVfxItemField =
		typeof(ItemVFX).GetField("item", BindingFlags.NonPublic | BindingFlags.Instance);

	private static bool IsTornado(Character character)
	{
		return TornadoController.IsLocalTornadoCharacter(character);
	}

	private static bool HasUsableLocalCharacter()
	{
		Character character = Character.localCharacter;
		return character != null
		       && character.data != null
		       && character.refs != null
		       && character.refs.view != null
		       && character.photonView != null;
	}

	private static Item GetOrCacheBananaItem(BananaPeel banana)
	{
		if (banana == null || BananaPeelItemField == null)
		{
			return null;
		}

		Item item = BananaPeelItemField.GetValue(banana) as Item;
		if (item != null)
		{
			return item;
		}

		item = banana.GetComponent<Item>();
		if (item != null)
		{
			BananaPeelItemField.SetValue(banana, item);
		}
		return item;
	}

	private static Item GetOrCacheItemVfxItem(ItemVFX itemVfx)
	{
		if (itemVfx == null || ItemVfxItemField == null)
		{
			return null;
		}

		Item item = ItemVfxItemField.GetValue(itemVfx) as Item;
		if (item != null)
		{
			return item;
		}

		item = itemVfx.GetComponent<Item>();
		if (item != null)
		{
			ItemVfxItemField.SetValue(itemVfx, item);
		}
		return item;
	}

	/// <summary>
	/// Identifies our networked tornado visual through PhotonView.InstantiationData.
	/// The patch call sites inject Tornado's private `view` field (assigned in Start)
	/// so the common case costs no GetComponent; the fallback covers RPCs that can
	/// arrive before Start has run.
	/// </summary>
	private static bool IsWindNetworkVisual(Tornado tornado, PhotonView view)
	{
		if (tornado == null)
		{
			return false;
		}

		if (view == null)
		{
			view = tornado.GetComponent<PhotonView>();
		}
		object[] data = view != null ? view.InstantiationData : null;
		return data != null
		       && data.Length > 0
		       && data[0] is string marker
		       && marker == TornadoController.NetworkVisualMarker;
	}

	// Our networked tornado keeps its vanilla Tornado component running so the
	// vanilla AttractCharacters/CapturedCharacter push logic is simulated locally
	// on every client (including unmodded ones) with ZERO extra network RPCs.
	// We only:
	//  - run the scale-up animation ourselves and skip the rest of Update() (the
	//    vanilla 0.5s RPCA_SyncTornado spam, lifetime countdown and target AI),
	//  - keep the tornado player out of the vanilla ignoredCharacters list so the
	//    funnel never pushes its own rider.

	[HarmonyPatch(typeof(Tornado), "Update")]
	[HarmonyPrefix]
	private static bool TornadoUpdatePrefix(Tornado __instance, PhotonView ___view, ref float ___lifeTime)
	{
		if (!IsWindNetworkVisual(__instance, ___view))
		{
			return true;
		}

		// Keep the tornado alive indefinitely: the vanilla Update() that counts
		// lifeTime down is skipped below, and pinning it here (injected field, no
		// reflection) also covers the case where PhotonView ownership changes.
		___lifeTime = float.MaxValue;

		// Reproduce only the visual scale-up of the vanilla Update(), which is
		// required for the vanilla FixedUpdate() (local push simulation) to activate.
		__instance.transform.localScale = Vector3.Lerp(__instance.transform.localScale, Vector3.one, Time.deltaTime * 0.25f);
		return false;
	}

	[HarmonyPatch(typeof(Tornado), "FixedUpdate")]
	[HarmonyPrefix]
	private static bool TornadoFixedUpdatePrefix(Tornado __instance, PhotonView ___view, List<Character> ___ignoredCharacters)
	{
		if (!IsWindNetworkVisual(__instance, ___view))
		{
			return true;
		}

		// Keep the tornado rider out of the vanilla push simulation so they are
		// never captured/flung by their own funnel. Every client re-adds it every
		// physics frame, so even the vanilla "clear lists while scale is small"
		// branch can't leave it pushable for long.
		Character rider = TornadoController.ActiveTornadoCharacter;
		if (rider != null && ___ignoredCharacters != null && !___ignoredCharacters.Contains(rider))
		{
			___ignoredCharacters.Add(rider);
		}

		// Run the vanilla FixedUpdate() so the funnel pushes every other player
		// locally on this client — no per-player force RPCs needed.
		return true;
	}

	[HarmonyPatch(typeof(Tornado), "RPCA_TornadoDie")]
	[HarmonyPrefix]
	private static bool TornadoDiePrefix(Tornado __instance, PhotonView ___view)
	{
		return !IsWindNetworkVisual(__instance, ___view);
	}

	[HarmonyPatch(typeof(Tornado), "RPCA_SyncTornado")]
	[HarmonyPostfix]
	private static void TornadoSyncPostfix(Tornado __instance, Vector3 syncVel, PhotonView ___view)
	{
		if (!IsWindNetworkVisual(__instance, ___view))
		{
			return;
		}

		__instance.transform.position = syncVel;
		__instance.transform.localScale = Vector3.one;
	}

	[HarmonyPatch(typeof(BananaPeel), "Update")]
	[HarmonyPrefix]
	private static bool BananaPeelUpdatePrefix(BananaPeel __instance)
	{
		Item item = GetOrCacheBananaItem(__instance);
		if (item == null)
		{
			return false;
		}
		if (item.itemState != ItemState.Ground)
		{
			return false;
		}
		return HasUsableLocalCharacter();
	}

	[HarmonyPatch(typeof(VineShooter), "Update")]
	[HarmonyPrefix]
	private static bool VineShooterUpdatePrefix(VineShooter __instance)
	{
		return __instance != null
		       && __instance.item != null
		       && HasUsableLocalCharacter()
		       && MainCamera.instance != null;
	}

	[HarmonyPatch(typeof(DispelFogField), "Update")]
	[HarmonyPrefix]
	private static bool DispelFogFieldUpdatePrefix()
	{
		// Singleton<OrbFogHandler>.Instance is the game's own cached lookup (the same
		// one DispelFogField.Start/OnDisable use) — avoids a FindAnyObjectByType scene
		// scan every frame for every fog field.
		if (Singleton<OrbFogHandler>.Instance == null)
		{
			return false;
		}

		Character observed = Character.observedCharacter;
		return observed != null && observed.data != null;
	}

	[HarmonyPatch(typeof(WarpCompassVFX), "OnUpdatedFeedData")]
	[HarmonyPrefix]
	private static bool WarpCompassOnUpdatedFeedDataPrefix(WarpCompassVFX __instance)
	{
		return GameUtils.instance != null
		       && GetOrCacheItemVfxItem(__instance) != null
		       && HasUsableLocalCharacter();
	}

	// CharacterBackpackHandler.LateUpdate (and its activeBackpackVisuals getter) dereference
	// character.player.backpackSlot with no null check. When a character's Player isn't ready
	// yet (mid-spawn, or during special-form transitions) this throws a NullReferenceException
	// every frame — including from other mods' own LateUpdate prefixes (an older ImZombie build
	// did exactly this). Skip the whole update when the character isn't fully set up. Running at
	// high priority makes our guard evaluate before other mods' prefixes, so returning false
	// suppresses them too.
	[HarmonyPatch(typeof(CharacterBackpackHandler), "LateUpdate")]
	[HarmonyPriority(Priority.High)]
	[HarmonyPrefix]
	private static bool CharacterBackpackHandlerLateUpdatePrefix(CharacterBackpackHandler __instance, Character ___character)
	{
		Character character = ___character != null
			? ___character
			: (__instance != null ? __instance.GetComponent<Character>() : null);
		return character != null
		       && character.player != null
		       && character.player.backpackSlot != null;
	}

	// CharacterData.UpdateHasParachute dereferences character.player.itemSlots with no
	// null check. Character.player => PlayerHandler.GetPlayer(view.Owner) returns null
	// until the owning player is registered (mid-spawn/transitions), which NRE-spams
	// every frame (818 hits in one out.txt session). Skipping the refresh until the
	// player exists matches the vanilla NRE abort's net effect without the log spam.
	[HarmonyPatch(typeof(CharacterData), "UpdateHasParachute")]
	[HarmonyPriority(Priority.High)]
	[HarmonyPrefix]
	private static bool CharacterDataUpdateHasParachutePrefix(CharacterData __instance, Character ___character)
	{
		Character character = ___character != null
			? ___character
			: (__instance != null ? __instance.GetComponent<Character>() : null);
		return character != null && character.player != null;
	}

	// ReverbCapZone.Update dereferences refs.voicePositionProvider / refs.voice for
	// every character with no null check; both are only assigned during the character
	// setup pass, so mid-spawn characters NRE every frame (616 hits in one session).
	// Skip the frame while any character isn't fully set up.
	[HarmonyPatch(typeof(ReverbCapZone), "Update")]
	[HarmonyPriority(Priority.High)]
	[HarmonyPrefix]
	private static bool ReverbCapZoneUpdatePrefix()
	{
		List<Character> allCharacters = Character.AllCharacters;
		if (allCharacters == null)
		{
			return false;
		}

		for (int i = 0; i < allCharacters.Count; i++)
		{
			Character character = allCharacters[i];
			if (character == null)
			{
				continue;
			}
			if (character.refs == null
			    || character.refs.voicePositionProvider == null
			    || character.refs.voice == null)
			{
				return false;
			}
		}
		return true;
	}

	internal static bool CharacterRpcDiePrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}

	internal static bool CharacterRpcSetDeadPrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}

	internal static bool CharacterRpcPassOutPrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}

	internal static bool CharacterDieInstantlyPrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}

	internal static bool CharacterHandleDeathPrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}

	internal static bool CharacterWarpPlayerRpcPrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}

	internal static bool CharacterWarpPlayerPrefix(Character __instance)
	{
		return !IsTornado(__instance);
	}
}
