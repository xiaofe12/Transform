using System;
using System.Collections.Generic;
using UnityEngine;

namespace Transform.Core;

/// <summary>
/// Shared HUD filter for every transformation form. Two visibility policies:
///
///  - Forms driven by the PLAYER's real stamina (zombie variants — their
///    sprint/jump/climb consume the player's CharacterStats stamina, same as the original
///    zombie mod) keep the status bar visible: <see cref="TickHide()"/> hides everything
///    EXCEPT the stamina group (staminaCanvasGroup, falling back to the StaminaBar);
///  - All other forms (critters, ghost, tumbleweed, tornado, statue) do not use the player's
///    stamina, so <see cref="TickHide(bool)"/> with keepStatusBar:false hides the whole HUD
///    including the status bar.
///
///  - the Canvas_Names overlay (floating player names) is hidden as well, re-checked on a
///    throttled timer because it can spawn after the transformation (scene load);
///  - the aiming reticle is NOT handled here — vanilla GUIManager.UpdateReticle re-activates
///    reticle objects every frame, so a Harmony patch (zombie module, gated on
///    <see cref="TransformState.AnyFormActive"/>) suppresses them.
///
/// Idempotent: form controllers call <see cref="TickHide"/> every frame while active and
/// <see cref="Restore"/> on exit. Only one form is ever active, so the static bookkeeping is safe.
/// </summary>
internal static class TransformHud
{
    private static readonly Dictionary<GameObject, bool> HiddenChildren = new Dictionary<GameObject, bool>();
    private static readonly Dictionary<GameObject, bool> HiddenCanvases = new Dictionary<GameObject, bool>();
    private static float _nextCanvasSearchTime;

    /// <summary>Keeps the player's status bar visible (zombie forms).</summary>
    internal static void TickHide()
    {
        TickHide(true);
    }

    /// <summary>Called every frame by each form controller while its form is active.
    /// keepStatusBar=false also hides the stamina group — for forms that don't run on the
    /// player's real stamina.</summary>
    internal static void TickHide(bool keepStatusBar)
    {
        try
        {
            GUIManager gui = GUIManager.instance;
            if (gui != null && gui.hudCanvas != null)
            {
                GameObject keep = null;
                if (keepStatusBar)
                {
                    if (gui.staminaCanvasGroup != null) keep = gui.staminaCanvasGroup.gameObject;
                    else if (gui.bar != null) keep = gui.bar.gameObject;
                }

                foreach (UnityEngine.Transform child in gui.hudCanvas.transform)
                {
                    if (child == null) continue;
                    if (keep != null && (child == keep.transform || keep.transform.IsChildOf(child))) continue;
                    if (HiddenChildren.ContainsKey(child.gameObject)) continue;
                    HiddenChildren[child.gameObject] = child.gameObject.activeSelf;
                    if (child.gameObject.activeSelf) child.gameObject.SetActive(false);
                }
            }
        }
        catch (Exception)
        {
            // The HUD may not exist yet (main menu) — retry next frame handles it.
        }

        // GameObject.Find is a full scene-name scan; canvases may spawn late, so retry throttled.
        if (Time.unscaledTime < _nextCanvasSearchTime) return;
        _nextCanvasSearchTime = Time.unscaledTime + 0.5f;
        TryHideCanvas("Canvas_Names");
    }

    private static void TryHideCanvas(string canvasName)
    {
        try
        {
            GameObject canvas = GameObject.Find(canvasName);
            if (canvas == null || HiddenCanvases.ContainsKey(canvas)) return;
            HiddenCanvases[canvas] = canvas.activeSelf;
            if (canvas.activeSelf) canvas.SetActive(false);
        }
        catch (Exception)
        {
            // Ignore — retried on the next throttle tick.
        }
    }

    /// <summary>Restores every hidden HUD element to its previous state. Called on form exit.</summary>
    internal static void Restore()
    {
        foreach (KeyValuePair<GameObject, bool> pair in HiddenChildren)
        {
            if (pair.Key != null) pair.Key.SetActive(pair.Value);
        }
        HiddenChildren.Clear();

        foreach (KeyValuePair<GameObject, bool> pair in HiddenCanvases)
        {
            if (pair.Key != null) pair.Key.SetActive(pair.Value);
        }
        HiddenCanvases.Clear();
    }
}
