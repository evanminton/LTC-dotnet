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
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        if (channels.Length == 0) throw new ArgumentException("At least one channel is required.", nameof(channels));
        if (channels.Length > ushort.MaxValue) throw new ArgumentException("Too many channels.", nameof(channels));
        foreach (var c in channels)
        {
            if (c is null) throw new ArgumentException("Channels cannot be null.", nameof(channels));
            if (c.Length != channels[0].Length) throw new ArgumentException("All channels must have the same length.", nameof(channels));
        }
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

    /// <summary>Reads a WAV stream. The stream does not need to be seekable.</summary>
    public static WavFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[12];
        if (!ReadFully(stream, header)) throw new InvalidDataException("Not a RIFF file.");
        if (!header[..4].SequenceEqual("RIFF"u8)) throw new InvalidDataException("Not a RIFF file.");
        if (!header[8..].SequenceEqual("WAVE"u8)) throw new InvalidDataException("Not a WAVE file.");

        int format = 0, channels = 0, sampleRate = 0, bits = 0, blockAlign = 0;
        bool haveFmt = false;
        byte[]? data = null;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (!haveFmt || data is null)
        {
            if (!ReadFully(stream, chunkHeader)) break;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var id = chunkHeader[..4];
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new InvalidDataException("WAV fmt chunk is too short.");
                if (size > 1024) throw new InvalidDataException("WAV fmt chunk is too long.");
                byte[] fmt = new byte[size];
                if (!ReadFully(stream, fmt)) throw new InvalidDataException("WAV file is truncated.");
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (format == 0xFFFE && fmt.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8) && data is null)
            {
                data = ReadData(stream, size);
                if (data.Length != size) break; // read to the end of the stream
            }
            else
            {
                Skip(stream, size);
            }
            if ((size & 1) != 0) Skip(stream, 1);
        }

        if (data is null || !haveFmt) throw new InvalidDataException("WAV file has no fmt or data chunk.");
        if (channels == 0 || sampleRate <= 0) throw new InvalidDataException("WAV fmt chunk has no channels or no sample rate.");
        if (format != 1 && format != 3) throw new NotSupportedException($"WAV format {format} is not supported (PCM and IEEE float only).");

        // Samples sit left-justified in containers of blockAlign / channels bytes (e.g. 12 or 20 valid bits in 16/24-bit containers).
        if (blockAlign == 0 || blockAlign % channels != 0) throw new InvalidDataException($"WAV block align {blockAlign} does not fit {channels} channel(s).");
        int bytesPer = blockAlign / channels;
        int container = bytesPer * 8;
        if (bits == 0 || bits > container) bits = container;
        int frames = data.Length / blockAlign;
        var ch = new float[channels][];
        for (int c = 0; c < channels; c++) ch[c] = new float[frames];

        var span = data.AsSpan();
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                var s = span.Slice((i * channels + c) * bytesPer, bytesPer);
                ch[c][i] = (format, container) switch
                {
                    (1, 8) => (s[0] - 128) / 128f,
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    (1, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    (3, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                    _ => throw new NotSupportedException($"{container}-bit {(format == 3 ? "float" : "PCM")} is not supported."),
                };
            }
        }
        return new WavFile(sampleRate, ch) { SourceBitsPerSample = bits };
    }

    // Reads a data chunk. Sizes of 0 or 0xFFFFFFFF (streamed files) or past the end mean "to the end of the stream".
    private static byte[] ReadData(Stream stream, uint size)
    {
        bool toEnd = size == 0 || size == uint.MaxValue;
        if (!toEnd && stream.CanSeek && size > stream.Length - stream.Position) toEnd = true;
        if (!toEnd)
        {
            if (size > Array.MaxLength) throw new NotSupportedException("WAV data larger than 2 GB is not supported.");
            byte[] buf = new byte[size];
            int n = ReadAtMost(stream, buf);
            return n == buf.Length ? buf : buf[..n];
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        if (ms.Length > Array.MaxLength) throw new NotSupportedException("WAV data larger than 2 GB is not supported.");
        return ms.ToArray();
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(Math.Min(count, Math.Max(0, stream.Length - stream.Position)), SeekOrigin.Current);
            return;
        }
        Span<byte> buf = stackalloc byte[4096];
        while (count > 0)
        {
            int n = stream.Read(buf[..(int)Math.Min(count, buf.Length)]);
            if (n == 0) return;
            count -= n;
        }
    }

    private static bool ReadFully(Stream stream, Span<byte> buffer) => ReadAtMost(stream, buffer) == buffer.Length;

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
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

    /// <summary>
    /// Writes this file. 16-bit PCM with one or two channels uses the plain PCM header; 24-bit PCM or more than two
    /// channels use WAVE_FORMAT_EXTENSIBLE; float uses WAVE_FORMAT_IEEE_FLOAT with a fact chunk.
    /// </summary>
    public void Write(Stream stream, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(bw, SampleRate, ChannelCount, SampleCount, format);
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < SampleCount; i++)
            for (int c = 0; c < ChannelCount; c++)
                WriteSample(bw, Channels[c][i], format, buf);
        WritePad(bw, ChannelCount, SampleCount, format);
    }

    /// <summary>
    /// Writes a mono stream of <paramref name="sampleCount"/> samples without holding them all in memory:
    /// <paramref name="fill"/> is called repeatedly with a buffer to fill (e.g. <see cref="LtcGenerator.Read(Span{float})"/>).
    /// </summary>
    public static void Write(Stream stream, int sampleRate, long sampleCount, Action<Span<float>> fill, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fill);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(bw, sampleRate, 1, sampleCount, format);
        var chunk = new float[Math.Min(sampleCount, 65_536)];
        Span<byte> buf = stackalloc byte[4];
        for (long done = 0; done < sampleCount;)
        {
            var span = chunk.AsSpan(0, (int)Math.Min(chunk.Length, sampleCount - done));
            fill(span);
            foreach (float v in span) WriteSample(bw, v, format, buf);
            done += span.Length;
        }
        WritePad(bw, 1, sampleCount, format);
    }

    private static void WriteHeader(BinaryWriter bw, int sampleRate, int channelCount, long sampleCount, WavSampleFormat format)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        int bits = (int)format;
        int blockAlign = bits / 8 * channelCount;
        long dataSize = blockAlign * sampleCount;
        bool isFloat = format == WavSampleFormat.Float32;
        bool extensible = !isFloat && (bits > 16 || channelCount > 2);
        int fmtSize = extensible ? 40 : isFloat ? 18 : 16;
        int factSize = isFloat ? 12 : 0;
        long riffSize = 4 + (8 + fmtSize) + factSize + (8 + dataSize + (dataSize & 1));
        if (riffSize > uint.MaxValue) throw new NotSupportedException("The audio is too long for a WAV file (4 GB limit).");

        bw.Write("RIFF"u8);
        bw.Write((uint)riffSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(fmtSize);
        bw.Write((ushort)(extensible ? 0xFFFE : isFloat ? 3 : 1));
        bw.Write((ushort)channelCount);
        bw.Write(sampleRate);
        bw.Write(sampleRate * blockAlign);
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)bits);
        if (extensible)
        {
            bw.Write((ushort)22);                                   // cbSize
            bw.Write((ushort)bits);                                 // valid bits per sample
            bw.Write(channelCount switch { 1 => 0x4u, 2 => 0x3u, _ => 0u }); // channel mask: FC, FL|FR, unassigned
            bw.Write((ushort)1);                                    // KSDATAFORMAT_SUBTYPE_PCM
            bw.Write([0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71]);
        }
        else if (isFloat)
        {
            bw.Write((ushort)0);                                    // cbSize
        }
        if (isFloat)
        {
            bw.Write("fact"u8);
            bw.Write(4);
            bw.Write((uint)sampleCount);
        }
        bw.Write("data"u8);
        bw.Write((uint)dataSize);
    }

    private static void WriteSample(BinaryWriter bw, float v, WavSampleFormat format, Span<byte> buf)
    {
        v = float.IsNaN(v) ? 0 : Math.Clamp(v, -1f, 1f);
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

    // RIFF chunks are word-aligned: an odd-sized data chunk is followed by a pad byte.
    private static void WritePad(BinaryWriter bw, int channelCount, long sampleCount, WavSampleFormat format)
    {
        if (((int)format / 8 * channelCount * sampleCount & 1) != 0) bw.Write((byte)0);
    }
}
