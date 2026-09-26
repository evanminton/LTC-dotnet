using System.Buffers.Binary;
using System.Text;

namespace LinearTimecode.Audio;

/// <summary>Sample formats <see cref="WavFile"/> can write.</summary>
public enum WavSampleFormat
{
    Pcm16 = 16,
    Pcm24 = 24,
    Float32 = 32,
}

/// <summary>
/// Minimal RIFF/WAVE reader and writer for LTC audio: reads 8/16/24/32-bit PCM and 32/64-bit float (including
/// WAVE_FORMAT_EXTENSIBLE), writes 16/24-bit PCM or 32-bit float.
/// </summary>
public sealed class WavFile
{
    public WavFile(int sampleRate, float[][] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length == 0) throw new ArgumentException("At least one channel is required.", nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }

    /// <summary>De-interleaved samples, −1…1.</summary>
    public float[][] Channels { get; }

    public int ChannelCount => Channels.Length;
    public int SampleCount => Channels[0].Length;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRate);

    /// <summary>Bits per sample of the source file (0 when created in memory).</summary>
    public int SourceBitsPerSample { get; private init; }

    /// <summary>Reads a WAV file.</summary>
    public static WavFile Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    /// <summary>Reads a WAV stream.</summary>
    public static WavFile Read(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        br.ReadUInt32();
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        byte[]? data = null;
        while (data is null)
        {
            byte[] id = br.ReadBytes(4);
            if (id.Length < 4) break;
            uint size = br.ReadUInt32();
            string chunk = Encoding.ASCII.GetString(id);
            if (chunk == "fmt ")
            {
                byte[] fmt = br.ReadBytes((int)size);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (format == 0xFFFE && fmt.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
            }
            else if (chunk == "data")
            {
                if (size == 0 || size == uint.MaxValue || size > stream.Length - stream.Position) size = (uint)(stream.Length - stream.Position);
                data = br.ReadBytes((int)size);
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
            }
            if ((size & 1) != 0 && chunk != "data" && stream.Position < stream.Length) stream.Seek(1, SeekOrigin.Current);
        }

        if (data is null || channels == 0) throw new InvalidDataException("WAV file has no fmt or data chunk.");
        if (format != 1 && format != 3) throw new NotSupportedException($"WAV format {format} is not supported (PCM and IEEE float only).");

        int bytesPer = bits / 8;
        int frames = data.Length / (bytesPer * channels);
        var ch = new float[channels][];
        for (int c = 0; c < channels; c++) ch[c] = new float[frames];

        var span = data.AsSpan();
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                var s = span.Slice((i * channels + c) * bytesPer, bytesPer);
                ch[c][i] = (format, bits) switch
                {
                    (1, 8) => (s[0] - 128) / 128f,
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    (1, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    (3, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                    _ => throw new NotSupportedException($"{bits}-bit {(format == 3 ? "float" : "PCM")} is not supported."),
                };
            }
        }
        return new WavFile(sampleRate, ch) { SourceBitsPerSample = bits };
    }

    /// <summary>Writes a mono file.</summary>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        using var fs = File.Create(path);
        Write(fs, samples, sampleRate, format);
    }

    /// <summary>Writes a mono stream.</summary>
    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Pcm16) =>
        new WavFile(sampleRate, [samples.ToArray()]).Write(stream, format);

    /// <summary>Writes this file.</summary>
    public void Write(Stream stream, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        int bits = (int)format;
        int bytesPer = bits / 8;
        int blockAlign = bytesPer * ChannelCount;
        int dataSize = blockAlign * SampleCount;

        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((ushort)(format == WavSampleFormat.Float32 ? 3 : 1));
        bw.Write((ushort)ChannelCount);
        bw.Write(SampleRate);
        bw.Write(SampleRate * blockAlign);
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)bits);
        bw.Write("data"u8);
        bw.Write(dataSize);

        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < SampleCount; i++)
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                float v = Math.Clamp(Channels[c][i], -1f, 1f);
                switch (format)
                {
                    case WavSampleFormat.Pcm16:
                        bw.Write((short)Math.Round(v * 32767));
                        break;
                    case WavSampleFormat.Pcm24:
                        int x = (int)Math.Round(v * 8388607);
                        buf[0] = (byte)x; buf[1] = (byte)(x >> 8); buf[2] = (byte)(x >> 16);
                        bw.Write(buf[..3]);
                        break;
                    default:
                        bw.Write(v);
                        break;
                }
            }
        }
    }
}
