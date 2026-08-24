using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Peak;
using Photon.Pun;
using UnityEngine;

namespace ImZombie;

[HarmonyPatch]
public static class ZombieHarmonyPatches
{
    private static readonly HashSet<int> CollisionIgnoredInstances = new HashSet<int>();
    private static readonly HashSet<string> LoggedPatchFailures = new HashSet<string>();

    // Attack bite-expression hold (seconds). The vanilla UpdateMouth only sets the bite texture while
    // the state is exactly Lunging; AnimatedMouth.Update then overwrites it with the talking mouth
    // every frame. We mark the hold when the lunge starts and re-apply the bite expression in
    // LateUpdate (after AnimatedMouth) so the expression actually shows.
    private const float AttackMouthHoldSeconds = 0.85f;
    private static readonly Dictionary<int, float> AttackMouthUntilById = new Dictionary<int, float>();

    private static readonly MethodInfo UpdateMushroomGrowthMethod = AccessTools.Method(typeof(MushroomZombie), "UpdateMushroomGrowth");
    private static readonly FieldInfo MushroomsGrowingField = AccessTools.Field(typeof(MushroomZombie), "mushroomsGrowing");
    private static readonly FieldInfo CharacterDataCharacterField = AccessTools.Field(typeof(CharacterData), "character");

    private static void LogPatchFailureOnce(string key, Exception ex)
    {
        if (LoggedPatchFailures.Add(key))
            ZombiePlugin.Log?.LogWarning("[I'm a Zombie] " + key + " skipped once: " + ex.Message);
    }

    // ---------------------------------------------------------------
    // Keep the parked original body out of fall/parachute updates while transformed.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(Character), "Update")]
    [HarmonyPrefix]
    private static bool CharacterUpdatePrefix(Character __instance)
    {
        return ZombieController.ParkedPlayerCharacter != __instance;
    }

    /// <summary>
    /// Keeps bite effects while ignoring the vanilla achievement NRE seen in player logs.
    /// </summary>
    [HarmonyPatch(typeof(MushroomZombie), "OnBitCharacter")]
    [HarmonyFinalizer]
    private static Exception OnBitCharacterFinalizer(Exception __exception)
    {
        return null; // swallow
    }

    [HarmonyPatch(typeof(CharacterData), "UpdateHasParachute")]
    [HarmonyPrefix]
    private static bool CharacterDataUpdateHasParachutePrefix(CharacterData __instance)
    {
        if (__instance == null) return false;
        if (CharacterDataCharacterField != null && CharacterDataCharacterField.GetValue(__instance) == null)
            return false;
        return true;
    }

    private static bool IsZombieVisual(MushroomZombie zombie)
    {
        if (zombie == null) return false;
        PhotonView view = zombie.GetComponent<PhotonView>();
        object[] data = view != null ? view.InstantiationData : null;
        return data != null
               && data.Length > 0
               && data[0] is string marker
               && marker == ZombieController.NetworkVisualMarker;
    }

    // Covers the offline fallback zombie (instantiated locally, no network marker)
    private static bool IsActiveLocalZombie(MushroomZombie zombie)
    {
        Character active = ZombieController.ActiveZombieCharacter;
        if (active == null || zombie == null) return false;
        Character c = zombie.GetComponent<Character>();
        return c != null && c == active;
    }

    private static bool IsPlayerControlledZombie(MushroomZombie zombie)
    {
        return IsZombieVisual(zombie) || IsActiveLocalZombie(zombie);
    }

    private static bool IsZombie(Character character)
    {
        return character != null && ZombieController.ActiveZombieCharacter == character;
    }

    // ---------------------------------------------------------------
    // MushroomZombie AI suppression for our zombie instance. The non-AI
    // parts of the vanilla Update (mushroom growth, mouth animation, bite
    // collider) still run on every client so remote players can be bitten
    // exactly like by an NPC zombie.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(MushroomZombie), "Update")]
    [HarmonyPrefix]
    private static bool MushroomZombieUpdatePrefix(MushroomZombie __instance)
    {
        if (!IsPlayerControlledZombie(__instance)) return true;
        TrySetupCollisionIgnore(__instance);
        if (ZombieController.GetZombieAppearance(__instance) == ZombiePlugin.ZombieAppearanceOption.MushroomMan)
            ZombieController.ForceMushroomManOnly(__instance);
        RunVisualUpdate(__instance);
        return false;
    }

    [HarmonyPatch(typeof(MushroomZombie), "FixedUpdate")]
    [HarmonyPrefix]
    private static bool MushroomZombieFixedUpdatePrefix(MushroomZombie __instance)
    {
        if (!IsPlayerControlledZombie(__instance)) return true;
        TrySetupCollisionIgnore(__instance);
        return false;
    }

    /// <summary>
    /// Mirrors vanilla zombie SFX, including silent mushroom-man zombies.
    /// </summary>
    [HarmonyPatch(typeof(MushroomZombie), "RPC_PlaySFX")]
    [HarmonyPrefix]
    private static bool MushroomZombieRpcPlaySfxPrefix(MushroomZombie __instance, int index)
    {
        if (!IsPlayerControlledZombie(__instance)) return true;
        if (!__instance.gameObject.activeInHierarchy) return false;
        PhotonView view = __instance.GetComponent<PhotonView>();
        Character zombieCharacter = __instance.GetComponent<Character>();
        if ((view == null || view.IsMine) && ZombieController.ActiveZombieCharacter != zombieCharacter) return false;
        if (Time.frameCount <= ZombieController.ExitSfxSuppressedFrame) return false;
        if (ZombieController.GetZombieAppearance(__instance) == ZombiePlugin.ZombieAppearanceOption.MushroomMan) return false;
        return true;
    }

    private static void RunVisualUpdate(MushroomZombie zombie)
    {
        try
        {
            bool growing = MushroomsGrowingField == null || (bool)MushroomsGrowingField.GetValue(zombie);
            if (growing && UpdateMushroomGrowthMethod != null) UpdateMushroomGrowthMethod.Invoke(zombie, null);
            if (zombie.currentState == MushroomZombie.State.Lunging) MarkAttackMouth(zombie);
            UpdateControlledMouth(zombie);
            // Mirror the vanilla Update()'s bite-collider toggle (SetActive(state == Lunging)):
            // the collider's MushroomZombieBiteCollider.OnTriggerEnter applies the bite damage
            // (stun + injury + spores) exactly like an NPC zombie. It must stay OFF outside the
            // lunge, and the lunge charge is what carries the mouth into the victim.
            if (zombie.biteColliderObject != null)
            {
                bool shouldBeActive = zombie.currentState == MushroomZombie.State.Lunging;
                if (zombie.biteColliderObject.activeSelf != shouldBeActive)
                    zombie.biteColliderObject.SetActive(shouldBeActive);
            }
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(UpdateControlledMouth), ex);
        }
    }

    /// <summary>
    /// Marks that the zombie should show the biting expression from now on for a short hold time.
    /// </summary>
    internal static void MarkAttackMouth(MushroomZombie zombie)
    {
        if (zombie == null) return;
        int zombieId = zombie.GetInstanceID();
        float until = Time.unscaledTime + AttackMouthHoldSeconds;
        if (AttackMouthUntilById.TryGetValue(zombieId, out float existing))
            AttackMouthUntilById[zombieId] = Mathf.Max(existing, until);
        else
            AttackMouthUntilById.Add(zombieId, until);
    }

    internal static void ClearAttackMouth(MushroomZombie zombie)
    {
        if (zombie == null) return;
        AttackMouthUntilById.Remove(zombie.GetInstanceID());
    }

    /// <summary>
    /// Applies the mouth expression. During the attack hold it forces the open-bite texture, otherwise
    /// it clears the talk sprites. Call this from LateUpdate (after AnimatedMouth.Update) so the attack
    /// expression wins over the talking-mouth animation.
    /// </summary>
    internal static void UpdateControlledMouth(MushroomZombie zombie)
    {
        if (zombie == null || zombie.animatedMouth == null) return;
        try
        {
            AnimatedMouth mouth = zombie.animatedMouth;
            if (mouth.mouthRenderer == null) return;

            int zombieId = zombie.GetInstanceID();
            bool attackHeld = AttackMouthUntilById.TryGetValue(zombieId, out float until)
                              && Time.unscaledTime < until;
            if (attackHeld)
            {
                mouth.mouthRenderer.material.SetInt("_UseTalkSprites", 1);
                if (mouth.mouthTextures != null && mouth.mouthTextures.Length > 0)
                    mouth.mouthRenderer.material.SetTexture("_TalkSprite", mouth.mouthTextures[mouth.mouthTextures.Length - 1]);
                return;
            }
            if (until > 0f) AttackMouthUntilById.Remove(zombieId);
            mouth.mouthRenderer.material.SetInt("_UseTalkSprites", 0);
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(TrySetupCollisionIgnore), ex);
        }
    }

    private static void TrySetupCollisionIgnore(MushroomZombie zombie)
    {
        try
        {
            GameObject root = zombie.gameObject;
            int instanceId = root.GetInstanceID();
            if (CollisionIgnoredInstances.Contains(instanceId)) return;

            PhotonView view = zombie.GetComponent<PhotonView>();
            object[] data = view != null ? view.InstantiationData : null;
            if (data == null || data.Length < 2 || !(data[1] is int characterViewId)) return;

            PhotonView characterView = PhotonView.Find(characterViewId);
            Character character = characterView != null ? characterView.GetComponent<Character>() : null;
            if (character == null) return;

            ZombieController.IgnoreCollisionWithCharacter(root, character);
            CollisionIgnoredInstances.Add(instanceId);
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(TrySetupCollisionIgnore), ex);
        }
    }

    // ---------------------------------------------------------------
    // Zombie player protection: death, pass-out, warp, fall, zombify.
    // ---------------------------------------------------------------

    internal static bool CharacterRpcDiePrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterRpcSetDeadPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterRpcPassOutPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterDieInstantlyPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterHandleDeathPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterWarpPlayerRpcPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterWarpPlayerPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterFallPrefix(Character __instance) { return !IsZombie(__instance); }
    internal static bool CharacterRpcFallPrefix(Character __instance) { return !IsZombie(__instance); }

    // ---------------------------------------------------------------
    // Stop vanilla input sampling from double-driving the controlled zombie.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(CharacterInput), "Sample")]
    [HarmonyPrefix]
    private static bool CharacterInputSamplePrefix(CharacterInput __instance)
    {
        Character zombie = ZombieController.ActiveZombieCharacter;
        if (zombie == null) return true;
        if (__instance != null && __instance.GetComponent<Character>() == zombie) return false;
        return true;
    }

    // ---------------------------------------------------------------
    // Appearance style guard. Non-player zombie styles must stay exactly as the
    // networked style says on every modded client. Vanilla customization reads
    // the Photon owner (the transformed player) and can dress normal/mushroom-man
    // zombies in player cosmetics, so suppress vanilla owner-cosmetic refreshes
    // for non-player styles and re-apply our explicit appearance in postfixes.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(CharacterCustomization), "Start")]
    [HarmonyPrefix]
    private static bool CharacterCustomizationStartPrefix(CharacterCustomization __instance)
    {
        return !ShouldSuppressOwnerCustomization(__instance);
    }

    [HarmonyPatch(typeof(CharacterCustomization), "OnPlayerDataChange")]
    [HarmonyPrefix]
    private static bool CharacterCustomizationOnPlayerDataChangePrefix(CharacterCustomization __instance)
    {
        return !ShouldSuppressOwnerCustomization(__instance);
    }

    [HarmonyPatch(typeof(CharacterCustomization), "OnPlayerDataChange")]
    [HarmonyPostfix]
    private static void CharacterCustomizationOnPlayerDataChangePostfix(CharacterCustomization __instance)
    {
        MushroomZombie zombie = GetControlledZombieFromCustomization(__instance);
        if (zombie != null) ZombieController.ApplyNetworkedZombieAppearance(zombie);
    }

    private static bool ShouldSuppressOwnerCustomization(CharacterCustomization customization)
    {
        MushroomZombie zombie = GetControlledZombieFromCustomization(customization);
        if (zombie == null) return false;
        return ZombieController.GetZombieAppearance(zombie) != ZombiePlugin.ZombieAppearanceOption.Player;
    }

    private static MushroomZombie GetControlledZombieFromCustomization(CharacterCustomization customization)
    {
        if (customization == null) return null;
        try
        {
            MushroomZombie zombie = customization.GetComponent<MushroomZombie>();
            if (zombie == null || !IsZombieVisual(zombie)) return null;
            return zombie;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------
    // Keep player-zombie lower garments aligned with the owner's actual outfit.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(MushroomZombie), "RPC_SetOutfit")]
    [HarmonyPrefix]
    private static bool MushroomZombieRpcSetOutfitPrefix(MushroomZombie __instance, bool hasSkirt)
    {
        if (!IsPlayerControlledZombie(__instance)) return true;
        if (ZombieController.GetZombieAppearance(__instance) != ZombiePlugin.ZombieAppearanceOption.Player) return true;
        // Mirror the source player's fit lower-garment state; if the player can't be
        // resolved leave the current state untouched (OnPlayerDataChange already set
        // it from the owner's outfit data).
        ZombieController.ApplyPlayerFitLowerGarments(__instance);
        return false;
    }

    // ---------------------------------------------------------------
    // HideTheBody force-show for the controlled zombie. The vanilla
    // HideTheBody ghosts body/headRend/sash/costumes (_VertexGhost=1) for
    // ANY non-local character. On remote clients our zombie is non-local,
    // so the game would ghost the zombie AND the clothes/costumes it wears
    // ("zombie clothes" invisible on friends' screens = hide sync problem).
    // Force the controlled zombie's HideTheBody visible on every client,
    // mirroring the reference mod's patches. Other players' HideTheBody
    // (e.g. carried players that SHOULD be ghosted) are left untouched.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(HideTheBody), "SetShowing")]
    [HarmonyPrefix]
    private static bool HideTheBodySetShowingPrefix(HideTheBody __instance, Character ___character, ref float x)
    {
        if (ZombieController.IsControlledZombieHideTheBody(__instance, ___character))
            x = 0f;
        return true;
    }

    [HarmonyPatch(typeof(HideTheBody), "SetShowing")]
    [HarmonyPostfix]
    private static void HideTheBodySetShowingPostfix(HideTheBody __instance, Character ___character)
    {
        if (ZombieController.IsControlledZombieHideTheBody(__instance, ___character))
            ZombieController.RevealHideTheBody(__instance);
    }

    [HarmonyPatch(typeof(HideTheBody), "Update")]
    [HarmonyPostfix]
    private static void HideTheBodyUpdatePostfix(HideTheBody __instance, Character ___character)
    {
        if (ZombieController.IsControlledZombieHideTheBody(__instance, ___character))
            ZombieController.RevealHideTheBody(__instance);
    }

    // ---------------------------------------------------------------
    // Use zombie locomotion and prevent NPC sleep setup on controlled zombies.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(MushroomZombie), "Start")]
    [HarmonyPrefix]
    private static bool MushroomZombieStartPrefix(MushroomZombie __instance)
    {
        if (!IsPlayerControlledZombie(__instance)) return true;
        try
        {
            __instance.isNPCZombie = false;
            Character c = __instance.GetComponent<Character>();
            if (c?.refs?.animator != null)
            {
                RuntimeAnimatorController npcController = ZombieController.GetCachedNpcZombieAnimator();
                if (npcController != null)
                    c.refs.animator.runtimeAnimatorController = npcController;
            }
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(MushroomZombieStartPrefix), ex);
        }
        return true;
    }

    [HarmonyPatch(typeof(MushroomZombie), "Start")]
    [HarmonyPostfix]
    private static void MushroomZombieStartPostfix(MushroomZombie __instance)
    {
        if (IsPlayerControlledZombie(__instance))
            ZombieController.ApplyNetworkedZombieAppearance(__instance);
    }

    // ---------------------------------------------------------------
    // Keep UpdateHeadBob from swapping the controlled zombie back to player animators.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(CharacterAnimations), "UpdateHeadBob")]
    [HarmonyPrefix]
    private static bool CharacterAnimationsUpdateHeadBobPrefix(CharacterAnimations __instance, Character ___character)
    {
        if (___character != null && ZombieController.ActiveZombieCharacter == ___character) return false;
        return true;
    }

    // ---------------------------------------------------------------
    // Hide reticles while a transform form is active.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(GUIManager), "UpdateReticle")]
    [HarmonyPrefix]
    private static bool GUIManagerUpdateReticlePrefix(GUIManager __instance, Character ___character)
    {
        if (!global::TransformState.AnyFormActive) return true;
        try
        {
            GameObject[] reticles =
            {
                __instance.reticleDefault, __instance.reticleX, __instance.reticleClimb,
                __instance.reticleClimbJump, __instance.reticleThrow, __instance.reticleReach,
                __instance.reticleGrasp, __instance.reticleSpike, __instance.reticleRope,
                __instance.reticleClimbTry
            };
            foreach (GameObject reticle in reticles)
            {
                if (reticle != null) reticle.SetActive(false);
            }
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(GUIManagerUpdateReticlePrefix), ex);
        }
        return false;
    }

    // ---------------------------------------------------------------
    // End-game cleanup: restore the player and remove controlled zombie records.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(Character), "RPCEndGame")]
    [HarmonyPrefix]
    private static void CharacterRPCEndGamePrefix()
    {
        ZombieController.ForceExitForEndGame();
        PruneInvalidAllCharactersEntries();
    }

    [HarmonyPatch(typeof(CharacterStats), "Win")]
    [HarmonyPrefix]
    private static void CharacterStatsWinPrefix(CharacterStats __instance)
    {
        EnsureCharacterStatsTimelinePopulated(__instance);
    }

    // Timeline accessors require at least one entry during the end screen.
    [HarmonyPatch(typeof(CharacterStats), "GetFinalTimelineInfo")]
    [HarmonyPrefix]
    private static void CharacterStatsGetFinalTimelineInfoPrefix(CharacterStats __instance)
    {
        EnsureCharacterStatsTimelinePopulated(__instance);
    }

    [HarmonyPatch(typeof(CharacterStats), "GetFirstTimelineInfo")]
    [HarmonyPrefix]
    private static void CharacterStatsGetFirstTimelineInfoPrefix(CharacterStats __instance)
    {
        EnsureCharacterStatsTimelinePopulated(__instance);
    }

    [HarmonyPatch(typeof(PeakHandler), "EndCutscene")]
    [HarmonyPrefix]
    private static void PeakHandlerEndCutscenePrefix()
    {
        PruneEndCutsceneCharacters();
    }

    private static void PruneInvalidAllCharactersEntries()
    {
        try
        {
            List<Character> all = Character.AllCharacters;
            if (all == null || all.Count == 0) return;
            all.RemoveAll(c => c == null || c.Equals(null) || c.gameObject == null || !c.gameObject.activeInHierarchy);
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(PruneInvalidAllCharactersEntries), ex);
        }
    }

    private static void EnsureCharacterStatsTimelinePopulated(CharacterStats stats)
    {
        if (stats == null) return;
        try
        {
            if (stats.timelineInfo == null || stats.timelineInfo.Count > 0) return;
            // Win() reads the last timeline entry when the character is local.
            stats.timelineInfo.Add(new EndScreen.TimelineInfo(Biome.BiomeType.Peak, CharacterStats.peakHeightInUnits, 0f, EndScreen.TimelineNote.None));
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(EnsureCharacterStatsTimelinePopulated), ex);
        }
    }

    private static void PruneEndCutsceneCharacters()
    {
        try
        {
            List<Character> all = Character.AllCharacters;
            if (all == null) return;
            all.RemoveAll(ShouldExcludeFromEndCutscene);
        }
        catch (Exception ex)
        {
            LogPatchFailureOnce(nameof(PruneEndCutsceneCharacters), ex);
        }
    }

    private static bool ShouldExcludeFromEndCutscene(Character c)
    {
        if (c == null) return false;
        try
        {
            if (ZombieController.ActiveZombieCharacter == c) return true;
            if (c.data == null) return true;
            if (c.data.badgeStatus == null || c.data.badgeStatus.Length == 0) return true;
        }
        catch { return true; }
        return false;
    }

    // ---------------------------------------------------------------
    // Airport loads can bypass RPCEndGame, so restore before those entry points run.
    // ---------------------------------------------------------------

    internal static void GameOverHandlerBeginAirportLoadRPCPrefix()
    {
        ZombieController.ForceExitForEndGame();
    }

    internal static void EndScreenReturnToAirportPrefix()
    {
        ZombieController.ForceExitForEndGame();
    }

    // ---------------------------------------------------------------
    // Preserve the last valid reconnect record while the local player is transformed.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(ReconnectData), "CreateFromCharacter")]
    [HarmonyPrefix]
    private static bool ReconnectDataCreateFromCharacterPrefix(Character character)
    {
        if (ZombieController.ActiveZombieCharacter == null) return true;
        if (character == ZombieController.ActiveZombieCharacter || character == ZombieController.ParkedPlayerCharacter)
            return false;
        return true;
    }

    // ---------------------------------------------------------------
    // Keep the vanilla zombie AI manager away from our player zombie:
    // never register it (no distance-based auto destroy) and never let
    // ReadyToDisable return true for it.
    // ---------------------------------------------------------------

    [HarmonyPatch(typeof(ZombieManager), "RegisterZombie")]
    [HarmonyPrefix]
    private static bool ZombieManagerRegisterZombiePrefix(MushroomZombie zombie)
    {
        return !IsPlayerControlledZombie(zombie);
    }

    [HarmonyPatch(typeof(MushroomZombie), "ReadyToDisable")]
    [HarmonyPrefix]
    private static bool MushroomZombieReadyToDisablePrefix(MushroomZombie __instance, ref bool __result)
    {
        if (!IsPlayerControlledZombie(__instance)) return true;
        __result = false;
        return false;
    }
}



