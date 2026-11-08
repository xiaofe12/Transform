using System.Collections.Generic;

namespace UnityEngine
{
    // Shader-float and renderer-state doubles; no GPU visibility/rendering is simulated.
    public sealed class Material
    {
        private readonly Dictionary<string, float> _values = new Dictionary<string, float>();
        public bool HasProperty(string name) => _values.ContainsKey(name);
        public float GetFloat(string name) => _values[name];
        public void SetFloat(string name, float value) { _values[name] = value; }
    }
    public sealed class Renderer
    {
        public bool enabled = true;
        public bool forceRenderingOff;
        public Material[] materials;
    }
}
