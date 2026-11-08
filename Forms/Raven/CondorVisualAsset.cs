using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Audio;
using UnityObject = UnityEngine.Object;

namespace ImRaven;

/// <summary>
/// Reconstructs a model-only Condor from the installed game's checked asset ranges,
/// including in Airport. Only a small hierarchy/read-layout index is embedded.
/// Regenerate with Tools/index_condor_resources.py when game resources change.
/// </summary>
internal static class CondorVisualAsset
{
    private static readonly List<UnityObject> OwnedAssets = new List<UnityObject>();
    private static AssetBundle _audioBundle;

    internal static GameObject CreateTemplate(out Vector3 grabAnchor)
    {
        grabAnchor = Vector3.down;
        GameObject root = null;
        try
        {
            using (Stream resource = typeof(CondorVisualAsset).Assembly.GetManifestResourceStream("Transform.CondorResources"))
            using (var game = new CondorGameResources(Application.dataPath))
            {
                if (resource == null) throw new InvalidDataException("Condor game resource index is missing.");
                using (var compressed = new GZipStream(resource, CompressionMode.Decompress))
                using (var reader = new BinaryReader(compressed))
                {
                    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "CNR3")
                        throw new InvalidDataException("Unsupported Condor resource index.");
                    grabAnchor = ReadVector3(reader);
                    root = new GameObject("Transform.Condor.Template");
                    root.SetActive(false);
                    UnityObject.DontDestroyOnLoad(root);
                    int nodeCount = reader.ReadInt32();
                    var nodes = new UnityEngine.Transform[nodeCount];
                    var paths = new string[nodeCount];
                    var defaultPose = new float[nodeCount * 10];
                    for (int i = 0; i < nodeCount; i++)
                    {
                        int parent = reader.ReadInt32();
                        string name = ReadString(reader);
                        var node = new GameObject(name).transform;
                        node.SetParent(parent < 0 ? root.transform : nodes[parent], false);
                        node.localPosition = ReadVector3(reader);
                        node.localRotation = ReadQuaternion(reader);
                        node.localScale = ReadVector3(reader);
                        int p = i * 10;
                        defaultPose[p] = node.localPosition.x; defaultPose[p+1] = node.localPosition.y; defaultPose[p+2] = node.localPosition.z;
                        defaultPose[p+3] = node.localRotation.x; defaultPose[p+4] = node.localRotation.y;
                        defaultPose[p+5] = node.localRotation.z; defaultPose[p+6] = node.localRotation.w;
                        defaultPose[p+7] = node.localScale.x; defaultPose[p+8] = node.localScale.y; defaultPose[p+9] = node.localScale.z;
                        nodes[i] = node;
                        paths[i] = parent < 0 ? "" : (paths[parent].Length == 0 ? name : paths[parent] + "/" + name);
                    }
                    int rendererIndex = reader.ReadInt32();
                    int rootBone = reader.ReadInt32();
                    var bones = new UnityEngine.Transform[reader.ReadInt32()];
                    for (int i = 0; i < bones.Length; i++) bones[i] = nodes[reader.ReadInt32()];
                    Mesh mesh = Track(new Mesh { name = "Transform.Condor.Mesh" });
                    int vertexCount = reader.ReadInt32();
                    byte[] vertexData = game.ReadBlob(reader);
                    mesh.vertices = ToVectors3(CondorGameResources.ReadChannel(reader, vertexData, vertexCount, 3));
                    mesh.normals = ToVectors3(CondorGameResources.ReadChannel(reader, vertexData, vertexCount, 3));
                    mesh.tangents = ToVectors4(CondorGameResources.ReadChannel(reader, vertexData, vertexCount, 4));
                    mesh.uv = ToVectors2(CondorGameResources.ReadChannel(reader, vertexData, vertexCount, 2));
                    float[] boneWeights = CondorGameResources.ReadChannel(reader, vertexData, vertexCount, 4);
                    float[] boneIndices = CondorGameResources.ReadChannel(reader, vertexData, vertexCount, 4);
                    var bindPoses = new Matrix4x4[reader.ReadInt32()];
                    byte[] bindData = game.ReadBlob(reader);
                    if (bindData.Length != bindPoses.Length * 64) throw new InvalidDataException("Invalid Condor bind poses.");
                    for (int i = 0; i < bindPoses.Length; i++)
                    {
                        var matrix = new Matrix4x4();
                        for (int row = 0; row < 4; row++)
                            for (int col = 0; col < 4; col++) matrix[row, col] = BitConverter.ToSingle(bindData, i*64 + (row*4+col)*4);
                        bindPoses[i] = matrix;
                    }
                    mesh.bindposes = bindPoses;
                    var weights = new BoneWeight[vertexCount];
                    for (int i = 0; i < weights.Length; i++)
                    {
                        weights[i] = new BoneWeight
                        {
                            boneIndex0 = (int)boneIndices[i*4], boneIndex1 = (int)boneIndices[i*4+1],
                            boneIndex2 = (int)boneIndices[i*4+2], boneIndex3 = (int)boneIndices[i*4+3],
                            weight0 = boneWeights[i*4], weight1 = boneWeights[i*4+1],
                            weight2 = boneWeights[i*4+2], weight3 = boneWeights[i*4+3]
                        };
                    }
                    mesh.boneWeights = weights;
                    byte[] indexData = game.ReadBlob(reader);
                    int submeshes = reader.ReadInt32();
                    mesh.subMeshCount = submeshes;
                    for (int sub = 0; sub < submeshes; sub++)
                    {
                        int firstByte = reader.ReadInt32(), count = reader.ReadInt32(), baseVertex = reader.ReadInt32();
                        if (firstByte < 0 || count < 0 || (long)firstByte + count*2L > indexData.Length)
                            throw new InvalidDataException("Invalid Condor triangle range.");
                        var triangles = new int[count];
                        for (int i = 0; i < triangles.Length; i++) triangles[i] = BitConverter.ToUInt16(indexData, firstByte+i*2) + baseVertex;
                        mesh.SetTriangles(triangles, sub);
                    }
                    mesh.RecalculateBounds();
                    var materials = new Material[reader.ReadInt32()];
                    for (int i = 0; i < materials.Length; i++) materials[i] = ReadMaterial(reader, game);
                    var renderer = nodes[rendererIndex].gameObject.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.sharedMaterials = materials;
                    renderer.bones = bones;
                    renderer.rootBone = nodes[rootBone];
                    renderer.updateWhenOffscreen = true;
                    Bounds bounds = mesh.bounds;
                    bounds.Expand(40f);
                    renderer.localBounds = bounds;
                    var animation = nodes[0].gameObject.AddComponent<Animation>();
                    animation.playAutomatically = true;
                    animation.cullingType = AnimationCullingType.AlwaysAnimate;
                    int clipCount = reader.ReadInt32();
                    for (int i = 0; i < clipCount; i++)
                    {
                        AnimationClip clip = ReadAnimation(game.ReadClip(reader, defaultPose), paths);
                        animation.AddClip(clip, clip.name);
                        if (clip.name == "CondorFly") animation.clip = clip;
                    }
                    if (animation.clip == null) throw new InvalidDataException("Condor flight animation is missing.");
                    int trailCount = reader.ReadInt32();
                    for (int i = 0; i < trailCount; i++) ReadTrail(reader, game, nodes);
                    ReadAudio(reader, game, nodes);
                    if (reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Unexpected Condor payload data.");
                    return root;
                }
            }
        }
        catch
        {
            if (root != null) UnityObject.Destroy(root);
            Clear();
            throw;
        }
    }

    private static void ReadAudio(BinaryReader reader, CondorGameResources game, UnityEngine.Transform[] nodes)
    {
        // The index holds only native clip metadata. The original compressed audio
        // bytes come from PEAK_Data and Unity decodes them through its own loader.
        _audioBundle = AssetBundle.LoadFromMemory(game.ReadAudioBundle(reader));
        if (_audioBundle == null) throw new InvalidDataException("Unity could not load the original Condor audio resources.");
        AudioMixerGroup[] mixers = Resources.FindObjectsOfTypeAll<AudioMixerGroup>();
        int count = reader.ReadInt32();
        if (count != 2) throw new InvalidDataException("Invalid Condor audio sources.");
        for (int i = 0; i < count; i++)
        {
            int node = reader.ReadInt32();
            string clipName = ReadString(reader), mixerName = ReadString(reader);
            if (node < 0 || node >= nodes.Length) throw new InvalidDataException("Invalid Condor audio node.");
            AudioClip clip = _audioBundle.LoadAsset<AudioClip>(clipName);
            if (clip == null || (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData()))
                throw new InvalidDataException("Condor audio clip could not be decoded: " + clipName);
            var source = nodes[node].gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = source.playOnAwake = true;
            foreach (AudioMixerGroup mixer in mixers)
                if (mixer != null && mixer.name == mixerName) { source.outputAudioMixerGroup = mixer; break; }
            if (source.outputAudioMixerGroup == null && SFX_Player.instance != null)
                source.outputAudioMixerGroup = SFX_Player.instance.defaultMixerGroup;
            source.volume = reader.ReadSingle(); source.pitch = reader.ReadSingle();
            source.minDistance = reader.ReadSingle(); source.maxDistance = reader.ReadSingle();
            source.dopplerLevel = reader.ReadSingle(); source.spatialBlend = reader.ReadSingle();
            source.spread = reader.ReadSingle(); source.reverbZoneMix = reader.ReadSingle();
            source.priority = reader.ReadInt32(); source.rolloffMode = (AudioRolloffMode)reader.ReadInt32();
        }
    }

    private static Material ReadMaterial(BinaryReader reader, CondorGameResources game)
    {
        string name = ReadString(reader);
        string shaderName = ReadString(reader);
        var state = new CondorRenderSettings.MaterialState(reader);
        // Use the original shader/loaded material. A generic lit shader silently
        // renders face cards and transparent trails incorrectly.
        Material native = null;
        Shader shader = Shader.Find(shaderName);
        foreach (Material candidate in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (candidate == null || candidate.shader == null || candidate.shader.name != shaderName) continue;
            if (shader == null) shader = candidate.shader;
            if (candidate.name == name) { native = candidate; break; }
        }
        if (shader == null) throw new InvalidDataException("Original Condor shader is not loaded: " + shaderName);
        Material material = Track(native != null ? new Material(native) : new Material(shader));
        material.name = name;
        int floatCount = reader.ReadInt32();
        for (int i = 0; i < floatCount; i++)
        {
            string property = ReadString(reader);
            float value = reader.ReadSingle();
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }
        int colorCount = reader.ReadInt32();
        for (int i = 0; i < colorCount; i++)
        {
            string property = ReadString(reader);
            Color color = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (material.HasProperty(property)) material.SetColor(property, color);
        }
        int textureCount = reader.ReadInt32();
        for (int i = 0; i < textureCount; i++)
        {
            string property = ReadString(reader);
            Vector2 scale = ReadVector2(reader);
            Vector2 offset = ReadVector2(reader);
            int width = reader.ReadInt32(), height = reader.ReadInt32(), format = reader.ReadInt32();
            int mipCount = reader.ReadInt32(), colorSpace = reader.ReadInt32();
            var sampling = new CondorRenderSettings.TextureSampling(reader);
            byte[] data = game.ReadBlob(reader);
            Texture2D texture = Track(new Texture2D(width, height, (TextureFormat)format, mipCount, colorSpace == 0) { name = name + property });
            texture.LoadRawTextureData(data);
            texture.Apply(false, true);
            texture.filterMode = (FilterMode)sampling.Filter;
            texture.anisoLevel = sampling.Aniso;
            texture.mipMapBias = sampling.Bias;
            texture.wrapModeU = (TextureWrapMode)sampling.WrapU;
            texture.wrapModeV = (TextureWrapMode)sampling.WrapV;
            texture.wrapModeW = (TextureWrapMode)sampling.WrapW;
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
                material.SetTextureScale(property, scale);
                material.SetTextureOffset(property, offset);
            }
        }
        material.shaderKeywords = state.Keywords;
        material.renderQueue = state.Queue;
        material.globalIlluminationFlags = (MaterialGlobalIlluminationFlags)state.LightmapFlags;
        material.enableInstancing = state.Instancing;
        material.doubleSidedGI = state.DoubleSided;
        for (int i = 0; i < state.Tags.Length; i += 2) material.SetOverrideTag(state.Tags[i], state.Tags[i+1]);
        foreach (string pass in state.DisabledPasses) material.SetShaderPassEnabled(pass, false);
        return material;
    }

    private static void ReadTrail(BinaryReader reader, CondorGameResources game, UnityEngine.Transform[] nodes)
    {
        var settings = new CondorRenderSettings.Trail(reader);
        var trail = nodes[settings.Node].gameObject.AddComponent<TrailRenderer>();
        trail.time = settings.Time;
        trail.minVertexDistance = settings.MinDistance;
        trail.widthMultiplier = settings.Width;
        trail.numCornerVertices = settings.Corners;
        trail.numCapVertices = settings.Caps;
        trail.alignment = (LineAlignment)settings.Alignment;
        trail.textureMode = (LineTextureMode)settings.TextureMode;
        trail.textureScale = new Vector2(settings.ScaleX, settings.ScaleY);
        trail.shadowBias = settings.ShadowBias;
        trail.generateLightingData = settings.Lighting;
        trail.applyActiveColorSpace = settings.ActiveColorSpace;
        trail.shadowCastingMode = (UnityEngine.Rendering.ShadowCastingMode)settings.CastShadows;
        trail.receiveShadows = settings.ReceiveShadows;
        trail.lightProbeUsage = (UnityEngine.Rendering.LightProbeUsage)settings.LightProbes;
        trail.reflectionProbeUsage = (UnityEngine.Rendering.ReflectionProbeUsage)settings.ReflectionProbes;
        trail.motionVectorGenerationMode = (MotionVectorGenerationMode)settings.MotionVectors;
        trail.sortingLayerID = settings.SortingLayer;
        trail.sortingOrder = settings.SortingOrder;
        trail.renderingLayerMask = unchecked((uint)settings.RenderingLayer);
        var keys = new Keyframe[settings.WidthKeys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            float[] k = settings.WidthKeys[i];
            keys[i] = new Keyframe(k[0], k[1], k[2], k[3], k[4], k[5]) { weightedMode = (WeightedMode)settings.WeightedModes[i] };
        }
        // Serialized infinity modes use 0=constant, 1=linear, 2=cycle,
        // 3=cycle-relative, 4=oscillate; they are not Unity's WrapMode enum values.
        trail.widthCurve = new AnimationCurve(keys) { preWrapMode = CurveWrap(settings.PreWrap), postWrapMode = CurveWrap(settings.PostWrap) };
        var colors = new GradientColorKey[settings.Colors.Length];
        var alpha = new GradientAlphaKey[settings.Alpha.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            float[] c = settings.Colors[i];
            colors[i] = new GradientColorKey(new Color(c[0], c[1], c[2]), c[3]);
        }
        for (int i = 0; i < alpha.Length; i++) alpha[i] = new GradientAlphaKey(settings.Alpha[i][0], settings.Alpha[i][1]);
        var gradient = new Gradient { mode = (GradientMode)settings.GradientMode, colorSpace = (ColorSpace)settings.GradientColorSpace };
        gradient.colorKeys = colors;
        gradient.alphaKeys = alpha;
        trail.colorGradient = gradient;
        trail.sharedMaterial = ReadMaterial(reader, game);
        trail.autodestruct = false;
        trail.emitting = settings.Emitting;
        trail.enabled = settings.Enabled;
    }

    private static WrapMode CurveWrap(int infinity)
        => infinity == 2 || infinity == 3 ? WrapMode.Loop : infinity == 4 ? WrapMode.PingPong : WrapMode.ClampForever;

    private static AnimationClip ReadAnimation(CondorGameResources.Clip source, string[] paths)
    {
        string name = source.Name;
        float duration = source.Duration;
        float[][] frames = source.Frames;
        int frameCount = frames.Length;
        AnimationClip clip = Track(new AnimationClip { name = name, legacy = true, frameRate = 60f, wrapMode = WrapMode.Loop });
        string[] properties = { "localPosition.x", "localPosition.y", "localPosition.z",
            "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w",
            "localScale.x", "localScale.y", "localScale.z" };
        for (int node = 0; node < paths.Length; node++)
        {
            // Keep quaternion signs continuous before interpolating their components.
            for (int f = 1; f < frameCount; f++)
            {
                int q = node * 10 + 3;
                float dot = 0;
                for (int c = 0; c < 4; c++) dot += frames[f - 1][q + c] * frames[f][q + c];
                if (dot < 0) for (int c = 0; c < 4; c++) frames[f][q + c] = -frames[f][q + c];
            }
            for (int component = 0; component < 10; component++)
            {
                int valueIndex = node * 10 + component;
                var keys = new Keyframe[frameCount];
                for (int f = 0; f < frameCount; f++)
                {
                    float time = f * duration / (frameCount - 1);
                    int before = Math.Max(0, f - 1), after = Math.Min(frameCount - 1, f + 1);
                    float tangent = (frames[after][valueIndex] - frames[before][valueIndex])
                        / ((after - before) * duration / (frameCount - 1));
                    keys[f] = new Keyframe(time, frames[f][valueIndex], tangent, tangent);
                }
                clip.SetCurve(paths[node], typeof(UnityEngine.Transform), properties[component], new AnimationCurve(keys));
            }
        }
        for (int i = 0; i < source.TrailNodes.Length; i++)
        {
            var keys = new Keyframe[frameCount];
            for (int f = 0; f < frameCount; f++) keys[f] = new Keyframe(f * duration / (frameCount-1), source.TrailTimes[i][f]);
            clip.SetCurve(paths[source.TrailNodes[i]], typeof(TrailRenderer), "m_Time", new AnimationCurve(keys));
        }
        for (int i = 0; i < source.AudioNodes.Length; i++)
        {
            var keys = new Keyframe[frameCount];
            for (int f = 0; f < frameCount; f++) keys[f] = new Keyframe(f * duration / (frameCount-1), source.AudioVolumes[i][f]);
            clip.SetCurve(paths[source.AudioNodes[i]], typeof(AudioSource), "m_Volume", new AnimationCurve(keys));
        }
        clip.EnsureQuaternionContinuity();
        return clip;
    }

    private static T Track<T>(T asset) where T : UnityObject
    {
        OwnedAssets.Add(asset);
        return asset;
    }

    internal static void Clear()
    {
        foreach (UnityObject asset in OwnedAssets) if (asset != null) UnityObject.Destroy(asset);
        OwnedAssets.Clear();
        if (_audioBundle != null) _audioBundle.Unload(true);
        _audioBundle = null;
    }

    private static string ReadString(BinaryReader reader) => CondorGameResources.ReadString(reader);
    private static Vector2 ReadVector2(BinaryReader reader) => new Vector2(reader.ReadSingle(), reader.ReadSingle());
    private static Vector3 ReadVector3(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static Quaternion ReadQuaternion(BinaryReader reader) => new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static Vector2[] ToVectors2(float[] values)
    {
        var result = new Vector2[values.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector2(values[i*2], values[i*2+1]);
        return result;
    }
    private static Vector3[] ToVectors3(float[] values)
    {
        var result = new Vector3[values.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector3(values[i*3], values[i*3+1], values[i*3+2]);
        return result;
    }
    private static Vector4[] ToVectors4(float[] values)
    {
        var result = new Vector4[values.Length / 4];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector4(values[i*4], values[i*4+1], values[i*4+2], values[i*4+3]);
        return result;
    }
}
