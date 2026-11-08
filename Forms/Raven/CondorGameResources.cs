using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ImRaven;

/// <summary>
/// Reads only the indexed parts of installed game assets. No scene is loaded and no
/// AI or network objects are instantiated. Hashes reject changed/truncated assets.
/// Kept independent of Unity so the actual disk reader/decoders can be regression tested.
/// </summary>
internal sealed class CondorGameResources : IDisposable
{
    private readonly string _dataDirectory;
    private readonly Dictionary<string, FileStream> _files = new Dictionary<string, FileStream>();

    internal CondorGameResources(string dataDirectory) { _dataDirectory = dataDirectory; }

    internal static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 4096) throw new InvalidDataException("Invalid Condor index string.");
        return Encoding.UTF8.GetString(ReadExactly(reader, length));
    }

    internal byte[] ReadBlob(BinaryReader index)
    {
        string name = ReadString(index);
        long offset = index.ReadInt64();
        int count = index.ReadInt32();
        byte[] expected = ReadExactly(index, 32);
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
            || offset < 0 || count <= 0 || count > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid Condor game resource range.");
        if (!_files.TryGetValue(name, out FileStream stream))
        {
            stream = new FileStream(Path.Combine(_dataDirectory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _files.Add(name, stream);
        }
        if (offset > stream.Length || count > stream.Length - offset)
            throw new InvalidDataException("Condor resource is truncated or incompatible: " + name);
        stream.Position = offset;
        byte[] data;
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) data = ReadExactly(reader, count);
        using (var hash = SHA256.Create())
        {
            byte[] actual = hash.ComputeHash(data);
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i])
                    throw new InvalidDataException("Condor resource changed: " + name
                        + ". This game build needs an updated resource index; an already loaded scene Condor can still be used.");
        }
        return data;
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException("Incomplete Condor resource.");
        return bytes;
    }

    internal byte[] ReadAudioBundle(BinaryReader index)
    {
        int prefixLength = index.ReadInt32();
        if (prefixLength <= 0 || prefixLength > 65536) throw new InvalidDataException("Invalid Condor audio header.");
        byte[] prefix = ReadExactly(index, prefixLength);
        int streams = index.ReadInt32();
        if (streams != 2) throw new InvalidDataException("Invalid Condor audio stream count.");
        using (var buffer = new MemoryStream())
        {
            buffer.Write(prefix, 0, prefix.Length);
            for (int i = 0; i < streams; i++)
            {
                byte[] fsb = ReadBlob(index);
                if (fsb.Length < 4 || Encoding.ASCII.GetString(fsb, 0, 4) != "FSB5")
                    throw new InvalidDataException("Invalid Condor native audio stream.");
                buffer.Write(fsb, 0, fsb.Length);
            }
            return buffer.ToArray();
        }
    }

    internal static float[] ReadChannel(BinaryReader index, byte[] data, int vertices, int expectedDimension)
    {
        int offset = index.ReadInt32(), stride = index.ReadInt32(), format = index.ReadInt32(), dimension = index.ReadInt32();
        int size = format == 1 ? 2 : 4;
        if ((format != 0 && format != 1 && format != 10) || dimension != expectedDimension || vertices <= 0
            || offset < 0 || stride < dimension * size || (long)offset + (long)(vertices - 1)*stride + dimension*size > data.Length)
            throw new InvalidDataException("Unsupported Condor vertex channel.");
        var values = new float[vertices * dimension];
        for (int v = 0; v < vertices; v++)
            for (int c = 0; c < dimension; c++)
            {
                int at = offset + v * stride + c * size;
                values[v * dimension + c] = format == 0 ? BitConverter.ToSingle(data, at)
                    : format == 10 ? BitConverter.ToInt32(data, at) : HalfToSingle(BitConverter.ToUInt16(data, at));
            }
        return values;
    }

    private static float HalfToSingle(ushort value)
    {
        int exponent = (value >> 10) & 31, mantissa = value & 1023;
        if (exponent == 31) throw new InvalidDataException("Non-finite Condor UV.");
        double magnitude = exponent == 0 ? mantissa * Math.Pow(2, -24) : (1024 + mantissa) * Math.Pow(2, exponent - 25);
        return (float)((value & 32768) != 0 ? -magnitude : magnitude);
    }

    internal sealed class Clip
    {
        internal string Name;
        internal float Duration;
        internal float[][] Frames;
        internal int[] TrailNodes;
        internal float[][] TrailTimes;
        internal int[] AudioNodes;
        internal float[][] AudioVolumes;
    }

    private struct Key
    {
        internal float Time, A, B, C, D;
    }

    internal Clip ReadClip(BinaryReader index, float[] defaultPose)
    {
        string name = ReadString(index);
        float duration = index.ReadSingle(), start = index.ReadSingle();
        int frames = index.ReadInt32(), curveCount = index.ReadInt32();
        if (duration <= 0 || frames < 2 || frames > 36000 || curveCount < 0 || curveCount > 4096)
            throw new InvalidDataException("Invalid Condor animation header.");
        byte[] streamed = ReadBlob(index);
        var curves = new List<Key>[curveCount];
        for (int i = 0; i < curves.Length; i++) curves[i] = new List<Key>();
        using (var reader = new BinaryReader(new MemoryStream(streamed, false)))
        {
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                float time = reader.ReadSingle();
                int count = reader.ReadInt32();
                if (count < 0 || count > curveCount) throw new InvalidDataException("Invalid Condor animation keys.");
                for (int k = 0; k < count; k++)
                {
                    int curve = reader.ReadInt32();
                    if (curve < 0 || curve >= curveCount) throw new InvalidDataException("Invalid Condor animation curve.");
                    curves[curve].Add(new Key { Time = time, A = reader.ReadSingle(), B = reader.ReadSingle(),
                        C = reader.ReadSingle(), D = reader.ReadSingle() });
                }
            }
        }
        int constantCount = index.ReadInt32();
        byte[] constants = ReadBlob(index);
        if (constantCount < 0 || constants.Length != constantCount * 4)
            throw new InvalidDataException("Invalid Condor animation constants.");
        int bindings = index.ReadInt32();
        if (bindings < 0 || bindings > 4096) throw new InvalidDataException("Invalid Condor animation bindings.");
        var mapping = new int[bindings, 4];
        for (int i = 0; i < bindings; i++)
        {
            for (int c = 0; c < 4; c++) mapping[i, c] = index.ReadInt32();
            int node = mapping[i, 0], attribute = mapping[i, 1], first = mapping[i, 2], count = mapping[i, 3];
            if (node < 0 || node >= defaultPose.Length / 10 || attribute < 1 || attribute > 4
                || count != (attribute == 2 ? 4 : 3) || first < 0 || first + count > curveCount + constantCount)
                throw new InvalidDataException("Unsupported Condor animation binding.");
        }
        int trailCount = index.ReadInt32();
        if (trailCount < 0 || trailCount > 16) throw new InvalidDataException("Invalid Condor trail animation bindings.");
        var trailNodes = new int[trailCount];
        var trailCurves = new int[trailCount];
        for (int i = 0; i < trailCount; i++)
        {
            trailNodes[i] = index.ReadInt32(); trailCurves[i] = index.ReadInt32();
            if (trailNodes[i] < 0 || trailNodes[i] >= defaultPose.Length / 10 || trailCurves[i] < 0
                || trailCurves[i] >= curveCount + constantCount) throw new InvalidDataException("Invalid Condor trail animation curve.");
        }
        int audioCount = index.ReadInt32();
        if (audioCount != 2) throw new InvalidDataException("Invalid Condor audio animation bindings.");
        var audioNodes = new int[audioCount];
        var audioCurves = new int[audioCount];
        for (int i = 0; i < audioCount; i++)
        {
            audioNodes[i] = index.ReadInt32(); audioCurves[i] = index.ReadInt32();
            if (audioNodes[i] < 0 || audioNodes[i] >= defaultPose.Length / 10 || audioCurves[i] < 0
                || audioCurves[i] >= curveCount + constantCount) throw new InvalidDataException("Invalid Condor audio animation curve.");
        }
        var clip = new Clip { Name = name, Duration = duration, Frames = new float[frames][], TrailNodes = trailNodes,
            TrailTimes = new float[trailCount][], AudioNodes = audioNodes, AudioVolumes = new float[audioCount][] };
        for (int i = 0; i < trailCount; i++) clip.TrailTimes[i] = new float[frames];
        for (int i = 0; i < audioCount; i++) clip.AudioVolumes[i] = new float[frames];
        var values = new double[4];
        for (int f = 0; f < frames; f++)
        {
            double time = start + Math.Min(duration, f / 60.0);
            float[] pose = (float[])defaultPose.Clone();
            for (int i = 0; i < audioCount; i++)
            {
                int curve = audioCurves[i];
                clip.AudioVolumes[i][f] = curve >= curveCount ? BitConverter.ToSingle(constants, (curve - curveCount) * 4)
                    : (float)Sample(curves[curve], time);
            }
            for (int i = 0; i < trailCount; i++)
            {
                int curve = trailCurves[i];
                clip.TrailTimes[i][f] = curve >= curveCount ? BitConverter.ToSingle(constants, (curve - curveCount) * 4)
                    : (float)Sample(curves[curve], time);
            }
            for (int b = 0; b < bindings; b++)
            {
                int node = mapping[b, 0], attribute = mapping[b, 1], first = mapping[b, 2], count = mapping[b, 3];
                for (int c = 0; c < count; c++)
                {
                    int curve = first + c;
                    values[c] = curve >= curveCount ? BitConverter.ToSingle(constants, (curve - curveCount) * 4)
                        : Sample(curves[curve], time);
                }
                int at = node * 10;
                if (attribute == 1 || attribute == 3)
                {
                    at += attribute == 3 ? 7 : 0;
                    for (int c = 0; c < 3; c++) pose[at + c] = (float)values[c];
                }
                else
                {
                    if (attribute == 4) EulerToQuaternion(values);
                    double norm = Math.Sqrt(values[0]*values[0] + values[1]*values[1] + values[2]*values[2] + values[3]*values[3]);
                    if (norm < 1e-10) throw new InvalidDataException("Invalid Condor animation rotation.");
                    for (int c = 0; c < 4; c++) pose[at + 3 + c] = (float)(values[c] / norm);
                }
            }
            clip.Frames[f] = pose;
        }
        return clip;
    }

    private static double Sample(List<Key> curve, double time)
    {
        if (curve.Count == 0) throw new InvalidDataException("Empty Condor animation curve.");
        int low = 0, high = curve.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (curve[middle].Time <= time) low = middle + 1; else high = middle;
        }
        Key key = curve[Math.Max(0, low - 1)];
        double dt = key.Time < -1e20f ? 0 : time - key.Time;
        return ((key.A*dt + key.B)*dt + key.C)*dt + key.D;
    }

    private static void EulerToQuaternion(double[] v)
    {
        double x = v[0]*Math.PI/360, y = v[1]*Math.PI/360, z = v[2]*Math.PI/360;
        double sx = Math.Sin(x), cx = Math.Cos(x), sy = Math.Sin(y), cy = Math.Cos(y), sz = Math.Sin(z), cz = Math.Cos(z);
        v[0] = cy*sx*cz + sy*cx*sz;
        v[1] = sy*cx*cz - cy*sx*sz;
        v[2] = cy*cx*sz - sy*sx*cz;
        v[3] = cy*cx*cz + sy*sx*sz;
    }

    public void Dispose()
    {
        foreach (FileStream file in _files.Values) file.Dispose();
        _files.Clear();
    }
}
