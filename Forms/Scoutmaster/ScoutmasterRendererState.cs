using System.Collections.Generic;
using UnityEngine;

namespace ImScoutmaster;

// Source-player visibility is not the creature's "show everything" presentation.
// In first person _VertexGhost=1 hides the local face/body while keeping shadows.
internal sealed class ScoutmasterRendererState
{
    private sealed class MaterialState
    {
        internal Material Material;
        internal readonly Dictionary<string, float> Values = new Dictionary<string, float>();
    }

    private static readonly string[] Properties = { "_VertexGhost", "_Opacity", "_Alpha" };
    private readonly Renderer _renderer;
    private readonly bool _enabled;
    private readonly bool _forceRenderingOff;
    private readonly List<MaterialState> _materials = new List<MaterialState>();

    internal ScoutmasterRendererState(Renderer renderer)
    {
        _renderer = renderer;
        _enabled = renderer.enabled;
        _forceRenderingOff = renderer.forceRenderingOff;
    }

    internal void Hide()
    {
        if (_renderer == null) return;
        foreach (Material material in _renderer.materials)
        {
            if (material == null) continue;
            MaterialState saved = _materials.Find(state => state.Material == material);
            if (saved == null)
            {
                saved = new MaterialState { Material = material };
                foreach (string property in Properties)
                    if (material.HasProperty(property)) saved.Values[property] = material.GetFloat(property);
                _materials.Add(saved);
            }
            foreach (string property in saved.Values.Keys)
                material.SetFloat(property, property == "_VertexGhost" ? 1f : 0f);
        }
        _renderer.forceRenderingOff = true;
        _renderer.enabled = false;
    }

    internal void Restore()
    {
        foreach (MaterialState saved in _materials)
        {
            if (saved.Material == null) continue;
            foreach (var property in saved.Values)
                if (saved.Material.HasProperty(property.Key)) saved.Material.SetFloat(property.Key, property.Value);
        }
        if (_renderer == null) return;
        _renderer.enabled = _enabled;
        _renderer.forceRenderingOff = _forceRenderingOff;
    }
}
