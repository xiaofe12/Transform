using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Transform.Core;

/// <summary>
/// Shared HUD filter: all forms hide the stamina group. External free cameras retain
/// control of other HUD elements; the status bar stays hidden until form exit.
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
    private static Coroutine _restorePulse;

    internal static void TickHide()
    {
        try
        {
            GUIManager gui = GUIManager.instance;
            if (gui != null)
            {
                HideStatusBar(gui);
                if (ThirdPartyCameras.ExternalCameraActive || gui.hudCanvas == null) return;

                foreach (UnityEngine.Transform child in gui.hudCanvas.transform)
                {
                    if (child == null) continue;
                    HideChild(child.gameObject);
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

    private static void HideStatusBar(GUIManager gui)
    {
        if (gui == null) return;
        if (gui.staminaCanvasGroup != null)
        {
            HideChild(gui.staminaCanvasGroup.gameObject);
            return;
        }
        if (gui.bar != null)
        {
            HideChild(gui.bar.gameObject);
        }
    }

    // Shared with optional HUD-mod guards; Restore() restores the captured active state.
    internal static void HideStatusObject(GameObject gameObject) { HideChild(gameObject); }

    private static void HideChild(GameObject gameObject)
    {
        if (gameObject == null) return;
        if (!HiddenChildren.ContainsKey(gameObject)) HiddenChildren[gameObject] = gameObject.activeSelf;
        if (gameObject.activeSelf) gameObject.SetActive(false);
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
        RestoreImmediate();
        StartRestorePulse();
    }

    private static void RestoreImmediate()
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

        if (!FormRegistry.AnyActive && !ThirdPartyCameras.ExternalCameraActive)
        {
            ForceHudRootsVisible();
        }
    }

    private static void StartRestorePulse()
    {
        global::Transform.TransformPlugin plugin = global::Transform.TransformPlugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled) return;

        if (_restorePulse != null)
        {
            try { plugin.StopCoroutine(_restorePulse); }
            catch (Exception) { }
            _restorePulse = null;
        }

        _restorePulse = plugin.StartCoroutine(RestorePulseRoutine());
    }

    private static IEnumerator RestorePulseRoutine()
    {
        for (int i = 0; i < 8; i++)
        {
            yield return null;
            RestoreImmediate();

            if (!FormRegistry.AnyActive && !ThirdPartyCameras.ExternalCameraActive)
            {
                ForceHudRootsVisible();
            }
        }

        _restorePulse = null;
    }

    private static void ForceHudRootsVisible()
    {
        try
        {
            GUIManager gui = GUIManager.instance;
            if (gui == null) return;

            if (gui.hudCanvas != null && !gui.hudCanvas.gameObject.activeSelf)
            {
                gui.hudCanvas.gameObject.SetActive(true);
            }

            if (gui.staminaCanvasGroup != null && !gui.staminaCanvasGroup.gameObject.activeSelf)
            {
                gui.staminaCanvasGroup.gameObject.SetActive(true);
            }
            else if (gui.bar != null && !gui.bar.gameObject.activeSelf)
            {
                gui.bar.gameObject.SetActive(true);
            }
        }
        catch (Exception)
        {
            // HUD may be rebuilding during scene load; the restore pulse retries on following frames.
        }
    }
}
