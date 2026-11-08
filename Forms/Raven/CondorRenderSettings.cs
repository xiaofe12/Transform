using System.IO;

namespace ImRaven;

// Serialized rendering settings, kept separate from Unity object creation so tests
// can verify the same reader used by the game, including all material/VFX metadata.
internal static class CondorRenderSettings
{
    internal sealed class MaterialState
    {
        internal string[] Keywords, Tags, DisabledPasses;
        internal int Queue, LightmapFlags;
        internal bool Instancing, DoubleSided;
        internal MaterialState(BinaryReader r)
        {
            Keywords = Strings(r);
            Queue = r.ReadInt32(); LightmapFlags = r.ReadInt32();
            Instancing = r.ReadInt32() != 0; DoubleSided = r.ReadInt32() != 0;
            Tags = Strings(r, 2); DisabledPasses = Strings(r);
        }
    }

    internal sealed class TextureSampling
    {
        internal string Name;
        internal int Filter, Aniso, WrapU, WrapV, WrapW;
        internal float Bias;
        internal TextureSampling(BinaryReader r)
        {
            Name = CondorGameResources.ReadString(r);
            Filter = r.ReadInt32(); Aniso = r.ReadInt32();
            WrapU = r.ReadInt32(); WrapV = r.ReadInt32(); WrapW = r.ReadInt32(); Bias = r.ReadSingle();
        }
    }

    internal sealed class Trail
    {
        internal int Node, Corners, Caps, Alignment, TextureMode, CastShadows, LightProbes, ReflectionProbes, MotionVectors;
        internal int SortingLayer, SortingOrder, RenderingLayer, PreWrap, PostWrap, GradientMode, GradientColorSpace;
        internal bool Enabled, Emitting, Lighting, ActiveColorSpace, ReceiveShadows;
        internal float Time, MinDistance, Width, ScaleX, ScaleY, ShadowBias;
        internal float[][] WidthKeys, Colors, Alpha;
        internal int[] WeightedModes;
        internal Trail(BinaryReader r)
        {
            Node = r.ReadInt32(); Enabled = r.ReadInt32() != 0; Emitting = r.ReadInt32() != 0;
            Time = r.ReadSingle(); MinDistance = r.ReadSingle(); Width = r.ReadSingle();
            Corners = r.ReadInt32(); Caps = r.ReadInt32(); Alignment = r.ReadInt32(); TextureMode = r.ReadInt32();
            ScaleX = r.ReadSingle(); ScaleY = r.ReadSingle(); ShadowBias = r.ReadSingle();
            Lighting = r.ReadInt32() != 0; ActiveColorSpace = r.ReadInt32() != 0;
            CastShadows = r.ReadInt32(); ReceiveShadows = r.ReadInt32() != 0;
            LightProbes = r.ReadInt32(); ReflectionProbes = r.ReadInt32(); MotionVectors = r.ReadInt32();
            SortingLayer = r.ReadInt32(); SortingOrder = r.ReadInt32(); RenderingLayer = r.ReadInt32();
            PreWrap = r.ReadInt32(); PostWrap = r.ReadInt32();
            int count = Count(r);
            WidthKeys = new float[count][]; WeightedModes = new int[count];
            for (int i = 0; i < count; i++) { WidthKeys[i] = Floats(r, 6); WeightedModes[i] = r.ReadInt32(); }
            GradientMode = r.ReadInt32(); GradientColorSpace = r.ReadInt32();
            int colors = Count(r), alpha = Count(r);
            Colors = new float[colors][]; Alpha = new float[alpha][];
            for (int i = 0; i < colors; i++) Colors[i] = Floats(r, 4);
            for (int i = 0; i < alpha; i++) Alpha[i] = Floats(r, 2);
        }
    }

    private static int Count(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("Invalid Condor render settings count.");
        return count;
    }
    private static string[] Strings(BinaryReader r, int stride = 1)
    {
        var result = new string[Count(r) * stride];
        for (int i = 0; i < result.Length; i++) result[i] = CondorGameResources.ReadString(r);
        return result;
    }
    private static float[] Floats(BinaryReader r, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = r.ReadSingle();
        return result;
    }
}
