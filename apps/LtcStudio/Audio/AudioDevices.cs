using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LtcStudio.Audio;

/// <summary>A WASAPI endpoint: a capture device, a render device, or a render device captured as loopback.</summary>
/// <param name="Id">WASAPI endpoint id.</param>
/// <param name="Name">Friendly name shown in Windows sound settings.</param>
/// <param name="IsLoopback">True for "what this output is playing" (WASAPI loopback of a render device).</param>
/// <param name="Channels">Channel count of the shared-mode mix format.</param>
/// <param name="SampleRate">Sample rate of the shared-mode mix format (set in Windows sound settings → Advanced).</param>
/// <param name="IsDefault">True for the Windows default device of this kind.</param>
public sealed record AudioEndpoint(string Id, string Name, bool IsLoopback, int Channels, int SampleRate, bool IsDefault)
{
    public string DisplayName =>
        $"{(IsLoopback ? "Loopback: " : "")}{Name}{(IsDefault ? " (default)" : "")} — {Channels} ch, {SampleRate / 1000.0:0.#} kHz";

    public override string ToString() => DisplayName;
}

/// <summary>Enumerates and opens WASAPI endpoints.</summary>
public static class AudioDevices
{
    /// <summary>Capture endpoints (microphones, line inputs, interface inputs), then loopback of every output.</summary>
    public static IReadOnlyList<AudioEndpoint> Inputs()
    {
        var list = new List<AudioEndpoint>();
        list.AddRange(List(DataFlow.Capture, loopback: false));
        list.AddRange(List(DataFlow.Render, loopback: true));
        return list;
    }

    /// <summary>Render endpoints (speakers, line outputs, interface outputs).</summary>
    public static IReadOnlyList<AudioEndpoint> Outputs() => List(DataFlow.Render, loopback: false);

    /// <summary>Opens an endpoint by id. The caller owns (disposes) the device.</summary>
    public static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }

    private static List<AudioEndpoint> List(DataFlow flow, bool loopback)
    {
        var list = new List<AudioEndpoint>();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                using (var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)) defaultId = d.ID;
        }
        catch { /* no default device */ }

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                using var client = device.AudioClient;
                var mix = client.MixFormat;
                list.Add(new AudioEndpoint(device.ID, device.FriendlyName, loopback, mix.Channels, mix.SampleRate, device.ID == defaultId));
            }
            catch
            {
                // A device that can't be opened (in use exclusively, driver fault) is left out.
            }
            finally
            {
                device.Dispose();
            }
        }
        // Default first, then by name.
        return list.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>Converts interleaved WASAPI buffers to float samples of one channel.</summary>
internal static class SampleConverter
{
    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);

    public static bool IsPcm(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.Pcm || (f is WaveFormatExtensible x && x.SubFormat == Pcm);

    /// <summary>Describes a format for the UI, e.g. "48000 Hz, 32-bit float, 2 ch".</summary>
    public static string Describe(WaveFormat f) =>
        $"{f.SampleRate} Hz, {f.BitsPerSample}-bit {(IsFloat(f) ? "float" : "PCM")}, {f.Channels} ch";

    /// <summary>
    /// Copies channel <paramref name="channel"/> (0-based) of <paramref name="frames"/> interleaved frames to
    /// <paramref name="dest"/>, and returns the peak absolute value.
    /// </summary>
    public static float Extract(ReadOnlySpan<byte> buffer, WaveFormat f, int channel, Span<float> dest, int frames)
    {
        int block = f.BlockAlign;
        int bytes = f.BitsPerSample / 8;
        int offset = channel * bytes;
        float peak = 0;
        bool isFloat = IsFloat(f);

        for (int i = 0; i < frames; i++)
        {
            var s = buffer.Slice(i * block + offset, bytes);
            float v = (isFloat, bytes) switch
            {
                (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (true, 8) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                (false, 2) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (false, 3) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                (false, 4) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                (false, 1) => (s[0] - 128) / 128f,
                _ => 0f,
            };
            dest[i] = v;
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        return peak;
    }
}
