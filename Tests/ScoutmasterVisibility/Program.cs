using System;
using ImScoutmaster;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string reason) { _checks++; if (!value) throw new Exception(reason); }
    private static Material Material(float ghost, float opacity = 1, float alpha = 1)
    {
        var m = new Material();
        m.SetFloat("_VertexGhost", ghost); m.SetFloat("_Opacity", opacity); m.SetFloat("_Alpha", alpha);
        return m;
    }
    private static void Main()
    {
        // Native first-person hiding is a shader setting while the renderer stays enabled.
        // The previous restore forced ghost=0, which reveals the face/hat inside the camera.
        foreach (var part in new[] { "face", "head", "hat", "body", "sash", "costume" })
        foreach (float ghost in new[] { 0f, 1f })
        foreach (bool enabled in new[] { false, true })
        foreach (bool forceOff in new[] { false, true })
        {
            var m = Material(ghost, 0.75f, 0.5f);
            var noProperties = new Material();
            var r = new Renderer { enabled = enabled, forceRenderingOff = forceOff, materials = new[] { m, null, noProperties } };
            for (int session = 0; session < 10; session++)
            {
                var state = new ScoutmasterRendererState(r);
                for (int frame = 0; frame < 5; frame++)
                {
                    r.enabled = true; // Native cosmetic refresh may reactivate a masked renderer.
                    state.Hide();
                    Check(!r.enabled && r.forceRenderingOff, part + " stays hidden throughout transformation");
                    Check(m.GetFloat("_VertexGhost") == 1 && m.GetFloat("_Opacity") == 0, "Material fallback also masks the source");
                }
                state.Restore();
                Check(r.enabled == enabled && r.forceRenderingOff == forceOff, "Renderer flags return to their captured values");
                Check(m.GetFloat("_VertexGhost") == ghost, "First/third-person visibility survives " + part + " restore");
                Check(m.GetFloat("_Opacity") == 0.75f && m.GetFloat("_Alpha") == 0.5f, "Preserve original transparency");
                state.Restore();
                Check(m.GetFloat("_VertexGhost") == ghost, "Repeated restoration cannot expose a hidden head");
                Check(!noProperties.HasProperty("_VertexGhost"), "Unsupported shader properties are not introduced");
            }
        }
        var original = Material(1);
        var swapped = Material(1, 0.4f);
        var renderer = new Renderer { materials = new[] { original } };
        var snapshot = new ScoutmasterRendererState(renderer);
        snapshot.Hide();
        renderer.materials = new[] { swapped };
        snapshot.Hide(); snapshot.Hide(); snapshot.Restore();
        Check(original.GetFloat("_VertexGhost") == 1 && swapped.GetFloat("_VertexGhost") == 1,
            "A hat/material replacement during transformation retains both original visibility states");
        Check(swapped.GetFloat("_Opacity") == 0.4f, "New material is captured before its first mask");
        Console.WriteLine("PASS: " + _checks + " Scoutmaster source-visibility checks: first-person face/hat/body masking, exact shader/renderer restore, repeated entry and cosmetic replacement.");
    }
}
