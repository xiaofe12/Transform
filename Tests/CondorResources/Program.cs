using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ImRaven;

// Compare the production disk reader and decoders against an independently exported
// reference. Run export_condor_visual.py first; no Unity or loaded scene is required.
internal static class Program
{
    private static int _assertions;
    private static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) throw new Exception(message);
    }
    private static void Near(float actual, float expected, string field)
        => Check(float.IsFinite(actual) && Math.Abs(actual - expected) < 0.00002f, field + $": {actual} != {expected}");
    private static BinaryReader Open(string file)
        => new BinaryReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress));
    private static string String(BinaryReader r) => CondorGameResources.ReadString(r);
    private static int EqualInt(BinaryReader a, BinaryReader b)
    {
        int value = a.ReadInt32(); Check(value == b.ReadInt32(), "integer metadata mismatch"); return value;
    }
    private static void EqualFloats(BinaryReader a, BinaryReader b, int count)
    {
        for (int i = 0; i < count; i++) Near(a.ReadSingle(), b.ReadSingle(), "metadata");
    }
    private static void EqualString(BinaryReader a, BinaryReader b) => Check(String(a) == String(b), "string metadata mismatch");

    private static void Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: CondorResources <PEAK_Data> <index.bin> <reference.bin>");
        using var game = new CondorGameResources(args[0]);
        using var index = Open(args[1]);
        using var reference = Open(args[2]);
        Check(Encoding.ASCII.GetString(index.ReadBytes(4)) == "CNR3", "index magic");
        Check(Encoding.ASCII.GetString(reference.ReadBytes(4)) == "CND1", "reference magic");
        EqualFloats(index, reference, 3);
        int nodes = EqualInt(index, reference);
        var nodeNames = new string[nodes];
        var defaults = new float[nodes*10];
        for (int i = 0; i < nodes; i++)
        {
            int parent = EqualInt(index, reference); Check(parent >= -1 && parent < i, "parent order");
            nodeNames[i] = String(index); Check(nodeNames[i] == String(reference), "node name");
            for (int c = 0; c < 10; c++) { defaults[i*10+c] = index.ReadSingle(); Near(defaults[i*10+c], reference.ReadSingle(), "default pose"); }
        }
        EqualInt(index, reference); EqualInt(index, reference);
        int bones = EqualInt(index, reference);
        for (int i = 0; i < bones; i++) EqualInt(index, reference);
        int vertices = index.ReadInt32();
        byte[] vertexData = game.ReadBlob(index);
        foreach (int dimension in new[] { 3, 3, 4, 2 })
        {
            Check(reference.ReadInt32() == vertices, "vertex count");
            float[] channel = CondorGameResources.ReadChannel(index, vertexData, vertices, dimension);
            foreach (float value in channel) Near(value, reference.ReadSingle(), "vertex channel");
        }
        float[] weights = CondorGameResources.ReadChannel(index, vertexData, vertices, 4);
        float[] indices = CondorGameResources.ReadChannel(index, vertexData, vertices, 4);
        int bindCount = EqualInt(index, reference); Check(bindCount == bones, "bind poses match bones");
        byte[] bindData = game.ReadBlob(index); Check(bindData.Length == bindCount*64, "bind pose bytes");
        for (int i = 0; i < bindCount*16; i++) Near(BitConverter.ToSingle(bindData, i*4), reference.ReadSingle(), "bind matrix");
        Check(reference.ReadInt32() == vertices, "skin count");
        for (int i = 0; i < vertices; i++)
        {
            for (int c = 0; c < 4; c++) Check((int)indices[i*4+c] == reference.ReadInt32(), "bone index");
            for (int c = 0; c < 4; c++) Near(weights[i*4+c], reference.ReadSingle(), "bone weight");
        }
        byte[] triangles = game.ReadBlob(index);
        int submeshes = EqualInt(index, reference);
        for (int sub = 0; sub < submeshes; sub++)
        {
            int first = index.ReadInt32(), count = index.ReadInt32(), baseVertex = index.ReadInt32();
            Check(count == reference.ReadInt32(), "triangle count");
            for (int i = 0; i < count; i++) Check(BitConverter.ToUInt16(triangles, first+i*2) + baseVertex == reference.ReadInt32(), "triangle index");
        }
        int materials = EqualInt(index, reference), textures = 0;
        for (int m = 0; m < materials; m++)
        {
            EqualString(index, reference); EqualString(index, reference);
            var state = new CondorRenderSettings.MaterialState(index);
            Check(state.Tags.Length == 0 && state.DisabledPasses.Length == 0, "native material tags and passes");
            Check(state.LightmapFlags == 4 && !state.DoubleSided, "native material GI flags");
            Check(state.Keywords.Length == 1 && state.Keywords[0] == (m == 0 ? "_INTERACTABLE_ON" : "_ISEYES_ON"), "original shader variant keywords");
            Check(state.Queue == (m == 0 ? -1 : 3008) && state.Instancing == (m == 1), "native render queue and instancing");
            int floats = EqualInt(index, reference);
            for (int i = 0; i < floats; i++) { EqualString(index, reference); EqualFloats(index, reference, 1); }
            int colors = EqualInt(index, reference);
            for (int i = 0; i < colors; i++) { EqualString(index, reference); EqualFloats(index, reference, 4); }
            int count = EqualInt(index, reference);
            for (int i = 0; i < count; i++)
            {
                EqualString(index, reference); EqualFloats(index, reference, 4);
                int width = index.ReadInt32(), height = index.ReadInt32(), format = index.ReadInt32();
                int mipCount = index.ReadInt32(), colorSpace = index.ReadInt32();
                var sampling = new CondorRenderSettings.TextureSampling(index);
                Check(sampling.Filter == 1 && sampling.Aniso == 1 && sampling.Bias == 0, "native texture sampling");
                int wrap = m == 1 ? 1 : i == 1 ? 2 : 0;
                Check(sampling.WrapU == wrap && sampling.WrapV == wrap && sampling.WrapW == wrap, "native texture wrapping (repeat/mirror/clamp)");
                byte[] data = game.ReadBlob(index);
                int size = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
                    size += format == 10 ? Math.Max(1, (w+3)/4)*Math.Max(1, (h+3)/4)*8 : w*h*(format == 3 ? 3 : 4);
                }
                Check(data.Length == size && colorSpace == 1, "native texture mip layout");
                byte[] png = reference.ReadBytes(reference.ReadInt32());
                Check(png.Length > 24 && png[0] == 137, "reference PNG");
                Check(ReadBigEndian(png,16) == width && ReadBigEndian(png,20) == height, "texture dimensions");
                textures++;
            }
        }
        int clips = EqualInt(index, reference);
        int[] animatedTrailNodes = null;
        for (int i = 0; i < clips; i++)
        {
            CondorGameResources.Clip clip = game.ReadClip(index, defaults);
            Check(clip.Name == String(reference), "clip name"); Near(clip.Duration, reference.ReadSingle(), "clip duration");
            Check(clip.Frames.Length == reference.ReadInt32(), "clip frame count");
            foreach (float[] pose in clip.Frames)
                foreach (float value in pose) Near(value, reference.ReadSingle(), clip.Name + " pose");
            Check(clip.TrailNodes.Length == 2 && clip.TrailNodes[0] != clip.TrailNodes[1], "both wing trails animated");
            if (animatedTrailNodes == null) animatedTrailNodes = clip.TrailNodes;
            for (int t = 0; t < 2; t++)
            {
                Check(clip.TrailNodes[t] == animatedTrailNodes[t] && nodeNames[clip.TrailNodes[t]] == "Trail", "trail animation target");
                foreach (float time in clip.TrailTimes[t]) Near(time, clip.Name == "CondorFly" ? 1f : 0.2f, "native animated trail lifetime");
            }
            Check(clip.AudioNodes.Length == 2 && clip.AudioNodes[0] != clip.AudioNodes[1], "both native audio sources animated");
            for (int a = 0; a < 2; a++)
            {
                string node = nodeNames[clip.AudioNodes[a]];
                Check(node == "SFX_Idle" || node == "SFX_Flap", "native audio animation target");
                Console.WriteLine($"{clip.Name} {node}: volume {clip.AudioVolumes[a][0]} .. {clip.AudioVolumes[a][^1]}");
                foreach (float volume in clip.AudioVolumes[a])
                    Near(volume, (node == "SFX_Idle") == (clip.Name == "CondorFly") ? 1f : 0f, "native flight audio volume");
            }
        }
        int trails = index.ReadInt32(); Check(trails == 2, "two original wing effects");
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int t = 0; t < trails; t++)
        {
            var trail = new CondorRenderSettings.Trail(index);
            Check(seen.Add(trail.Node) && Array.IndexOf(animatedTrailNodes, trail.Node) >= 0, "wing effect matches animation binding");
            Check(trail.Enabled && trail.Emitting && !trail.Lighting && trail.ActiveColorSpace, "native trail rendering flags");
            Near(trail.Time, 1, "trail lifetime"); Near(trail.MinDistance, 0.2f, "trail vertex spacing"); Near(trail.Width, 0.2f, "trail width");
            Check(trail.Corners == 0 && trail.Caps == 3 && trail.Alignment == 0 && trail.TextureMode == 0, "trail geometry");
            Near(trail.ScaleX, 1, "trail texture scale"); Near(trail.ScaleY, 1, "trail texture scale"); Near(trail.ShadowBias, 0.5f, "trail shadow bias");
            Check(trail.CastShadows == 0 && trail.ReceiveShadows && trail.LightProbes == 0 && trail.ReflectionProbes == 0
                && trail.MotionVectors == 0 && trail.SortingLayer == 0 && trail.SortingOrder == 0 && trail.RenderingLayer == 1, "trail render layers/probes");
            Check(trail.PreWrap == 2 && trail.PostWrap == 2 && trail.WidthKeys.Length == 2, "native width curve wrap and keys");
            float[][] expected = { new[] {0f,1f,0f,0f,0.33333334f,0.33333334f}, new[] {1f,0f,-1.3507403f,-1.3507403f,0.11256552f,0f} };
            for (int k = 0; k < 2; k++)
            {
                Check(trail.WeightedModes[k] == 0, "native width weighting");
                for (int c = 0; c < 6; c++) Near(trail.WidthKeys[k][c], expected[k][c], "native width curve key");
            }
            Check(trail.GradientMode == 0 && trail.GradientColorSpace == 0 && trail.Colors.Length == 2 && trail.Alpha.Length == 2, "native gradient modes/counts");
            float[][] colors = {new[] {0.9426805f,0.8632076f,1f,0f},new[] {1f,1f,1f,1f}};
            for (int k = 0; k < 2; k++) for (int c = 0; c < 4; c++) Near(trail.Colors[k][c], colors[k][c], "native trail gradient");
            Near(trail.Alpha[0][0],1,"trail initial alpha"); Near(trail.Alpha[0][1],0,"trail initial alpha time");
            Near(trail.Alpha[1][0],0,"trail fade alpha"); Near(trail.Alpha[1][1],1,"trail fade time");
            Check(String(index) == "GliderTrail" && String(index) == "Particle_Transparent", "original transparent trail material/shader");
            var state = new CondorRenderSettings.MaterialState(index);
            Check(state.Queue == -1 && state.Keywords.Length == 0 && !state.Instancing && !state.DoubleSided && state.LightmapFlags == 4, "trail material states");
            int floats = index.ReadInt32(); for (int f = 0; f < floats; f++) { String(index); Check(float.IsFinite(index.ReadSingle()), "trail material float"); }
            int count = index.ReadInt32(); for (int c = 0; c < count; c++) { String(index); for (int f = 0; f < 4; f++) Check(float.IsFinite(index.ReadSingle()), "trail material color"); }
            Check(index.ReadInt32() == 0, "native trail has no texture dependency");
        }
        byte[] bundle = game.ReadAudioBundle(index);
        Check(Encoding.ASCII.GetString(bundle, 0, 7) == "UnityFS", "native audio bundle header");
        Check(bundle.Length < 900000 && bundle.Length > 854080, "audio comes from original resource ranges");
        // Allow an independent native-format reader to inspect the runtime-built bundle.
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(args[2])!, "condor-audio-runtime.bundle"), bundle);
        int audioSources = index.ReadInt32(); Check(audioSources == 2, "two flight loop sources");
        for (int a = 0; a < audioSources; a++)
        {
            string node = nodeNames[index.ReadInt32()];
            Check(String(index) == (node == "SFX_Idle" ? "glider loop" : "condor flap"), "original clip binding");
            Check(String(index) == "SFX", "original game audio mixer");
            Near(index.ReadSingle(), node == "SFX_Idle" ? 1 : 0, "initial source volume");
            Near(index.ReadSingle(), 1, "original source pitch");
            Near(index.ReadSingle(), 1, "source min distance");
            Near(index.ReadSingle(), node == "SFX_Idle" ? 50 : 200, "source max distance");
            Near(index.ReadSingle(), 1, "original doppler");
            Near(index.ReadSingle(), 1, "original spatial blend");
            Near(index.ReadSingle(), node == "SFX_Idle" ? 15 : 0, "original stereo spread");
            Near(index.ReadSingle(), 1, "original reverb mix");
            Check(index.ReadInt32() == 128, "source priority");
            Check(index.ReadInt32() == (node == "SFX_Idle" ? 1 : 0), "source distance rolloff");
        }
        Check(index.BaseStream.ReadByte() == -1 && reference.BaseStream.ReadByte() == -1, "consumed both files");
        TestFailures();
        Console.WriteLine($"PASS: {_assertions:N0} assertions; {nodes} nodes, {vertices} vertices, {bones} bones, {textures} native textures, {clips} original animations, {trails} wing trails.");
        Console.WriteLine("PASS: original shader keywords, face render queue, texture sampling/wrapping, trail geometry/gradient/width curves and animated lifetimes.");
        Console.WriteLine("PASS: actual game-file reads and runtime decoders match the independent reference; changed/missing/truncated assets and invalid paths are rejected.");
        Console.WriteLine("PASS: native flight AudioClip bundle assembled from checked game-file bytes, spatial sources, SFX routing and animated glide/flap volumes.");
    }

    private static int ReadBigEndian(byte[] data, int i) => data[i]<<24 | data[i+1]<<16 | data[i+2]<<8 | data[i+3];
    private static void TestFailures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Transform-Condor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "test.assets");
        byte[] content = {1, 2, 3, 4}; File.WriteAllBytes(file, content);
        try
        {
            byte[] hash = SHA256.HashData(content);
            void Case(string name, long offset, int count, byte[] digest, bool fails)
            {
                using var bytes = new MemoryStream();
                using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
                byte[] text = Encoding.UTF8.GetBytes(name);
                writer.Write(text.Length); writer.Write(text); writer.Write(offset); writer.Write(count); writer.Write(digest);
                bytes.Position = 0;
                using var reader = new BinaryReader(bytes);
                using var resource = new CondorGameResources(directory);
                bool failed = false;
                try { byte[] data = resource.ReadBlob(reader); Check(data.Length == count, "valid source range"); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { failed = true; }
                Check(failed == fails, "resource failure behavior: " + name);
            }
            Case("test.assets", 0, 4, hash, false);
            Case("test.assets", 0, 4, new byte[32], true);
            Case("test.assets", 0, 5, hash, true);
            Case("missing.assets", 0, 4, hash, true);
            Case("../test.assets", 0, 4, hash, true);
            Case("test.assets", -1, 4, hash, true);
        }
        finally { File.Delete(file); Directory.Delete(directory); }
    }
}
