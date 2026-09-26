# LinearTimecode

A .NET 10 library for **SMPTE Linear Time Code (LTC)**, implementing *SMPTE ST 12-1:2014 — Time and Control Code*, with the binary-group payloads of *SMPTE ST 309:2012 — Date and Time Zone*, *SMPTE ST 262 — Binary Groups: Storage and Transmission of Data* (page/line) and *SMPTE RP 169 — Auxiliary Time Address Data in Binary Groups*. It has no dependencies and no platform-specific code, is AOT- and trim-compatible, and runs in MAUI, desktop, server or CLI apps.

Every value the spec defines has a human-readable name and description. The `LtcOptions` catalog, the `LtcDescriber` "explain this codeword" utility, the `ltc` command-line tool and the LTC Explorer app all use them.

```
src/LinearTimecode          the library
tools/LinearTimecode.Cli    `ltc` command-line utility (dotnet tool)
tests/LinearTimecode.Tests  xUnit tests (spec tables, drop-frame arithmetic, audio round trips)
samples/LtcExplorer         .NET MAUI app (Generate / Read / Codeword / User Bits / Calculator / Reference)
```

## What's covered

| Spec section | API |
|---|---|
| §1, §5–7, §12 frame rates 23.98 – 60, NTSC time, frame pairs | `LtcFrameRate` (+ `.CodewordRate()`, `.BitRate()`, `.IsFramePair()`, `.Description()`, …) |
| §5.2 drop-frame / non-drop counting, 24-hour clock | `Timecode` (`AddFrames`, `TotalFrames`, `FromTotalFrames`, `ToTimeSpan`, `ConvertTo`, `FromTimeOfDay`) |
| §8.2 / Table 2 BCD time address | `LtcCodeword` field accessors, `LtcFrame` |
| §8.3 / Table 3 flags (DF, CF, polarity, BGF0–2) in 24/25/30-frame layouts | `LtcBits`, `LtcCodeword.*Flag(TimecodeBase)` |
| §8.4 / Table 1 binary group flags | `BinaryGroupFlags` (+ `.IsClockTime()`, `.Description()`) |
| §8.4.2 / Table 4 binary groups, 8-bit characters | `UserBits` (`FromText`, `ToText`, groups, hex) |
| §5.3 / §6.3 color frame identification (NTSC, PAL logical & arithmetic) | `ColorFraming` |
| §9.2.3 biphase mark polarity correction | `LtcCodeword.WithPolarityCorrection`, `LtcFrame.PolarityCorrection` |
| §9.2.5 / Table 5 sync word, direction | `LtcBits.SyncWord`, `LtcBits.ReverseSyncWord` |
| §9.3 biphase mark modulation | `BiphaseMark` |
| §9.4 bit rate, §9.5 timing datum, §9.6.1 rise time | `LtcGenerator` (sample-exact at any sample rate, 40 µs raised-cosine edges, varispeed, reverse) |
| ST 309 date and time zone: Table 2 time zone codes, DST, YYMMDD / MJD, local vs UTC, midnight rollover | `BinaryGroups.TimeZoneCode`, `BinaryGroups.DateTimeZone`, `frame.WithDateTimeZone()`, `LtcGenerator.AdvanceDateAtMidnight` |
| ST 262 page/line: directory index, categories, control codes, single-frame messages, message strings, checksum, priority, auxiliary time address | `BinaryGroups.DirectoryIndex`, `PageLineFrame`, `PageLineMessageLayout`, `frame.WithPageLine()`, `LtcGenerator.FrameHook` |
| RP 169 auxiliary time address: second time address in BG 1–8 with its own drop frame and color frame flags, hours = directory index | `BinaryGroups.AuxiliaryTimeAddress`, `PageLineFrame.ForAuxiliaryTimeAddress`, `AuxiliaryTimeAddress.RunningHook` |
| Reading LTC from audio | `LtcDecoder` (forward/reverse, varispeed, DC offset, noise, dropouts, rate detection) |
| WAV I/O | `WavFile` (reads 8/16/24/32-bit PCM and 32/64-bit float; writes 16/24-bit PCM and float) |

## Quick start

```csharp
using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

// Time codes and drop-frame arithmetic
var tc = Timecode.Parse("00:09:59;29", LtcFrameRate.Fps29_97);   // ';' → drop-frame
Console.WriteLine(tc.Next());                                     // 00:10:00;00
Console.WriteLine(tc.ToTimeSpan());                               // real elapsed time

// Codewords
var frame = new LtcFrame(Timecode.Parse("10:00:00:00", LtcFrameRate.Fps25))
{
    UserBits = UserBits.FromText("REEL"),
    BinaryGroupFlags = BinaryGroupFlags.EightBitCharacters,
};
LtcCodeword cw = frame.ToCodeword();                              // polarity-corrected
Console.WriteLine(cw.ToHex());                                    // 10 bytes, sync word FC BF
Console.WriteLine(LtcDescriber.Explain(cw, LtcFrameRate.Fps25));  // every field explained

// Audio out: pull samples from any audio API callback
var gen = new LtcGenerator(frame, sampleRate: 48_000) { Amplitude = 0.5f };
float[] buffer = new float[480];
gen.Read(buffer);                               // fills the next 10 ms

// Audio in: push samples, get frames
var reader = new LtcDecoder(48_000);            // or new LtcDecoder(48_000, LtcFrameRate.Fps25)
reader.FrameDecoded += f => Console.WriteLine($"{f.Timecode} {f.Direction} x{f.Speed:0.00}");
float[] samples = LtcGenerator.Render(frame, 25); // or audio from a sound card / file
reader.Process(samples);

// Files
WavFile.Write("tc.wav", LtcGenerator.Render(frame, 250), 48_000);
var frames = LtcDecoder.DecodeAll(WavFile.Read("tc.wav").Channels[0], 48_000);

// ST 309: date + time zone in the user bits (BGF 100, or 110 for clock time); the generator rolls the date at midnight
var dated = frame.WithDateTimeZone(new DateTimeZone(new DateOnly(2026, 9, 26), TimeZoneCode.Parse("+01:00")), clockTime: true);
DateTimeOffset? when = dated.GetDateTimeZone()?.ToDateTimeOffset(dated.Timecode);

// ST 262: page/line data, one frame per codeword
var control = PageLineFrame.ControlCode(line: 3, 0x12, 0x34);           // page 15, checksum in byte 1
var message = PageLineMessageLayout.Default.EncodeText("SCENE 12");      // prefix, message frames, suffix
gen.FrameHook = (i, f) => f.WithPageLine(message[(int)(i % message.Count)]);
var received = PageLineMessageLayout.Default.Decode(frames.Select(f => f.Frame.GetPageLine()).OfType<PageLineFrame>());

// Human-readable reference
Console.WriteLine(LtcOptions.ToText());
```

Audio device I/O is platform-specific, so the library leaves it to you. Feed samples from any API (WASAPI, CoreAudio, Android `AudioRecord`, NAudio, …) to `LtcDecoder.Process`, and fill output buffers with `LtcGenerator.Read`.

## `ltc` utility

```
dotnet run --project tools/LinearTimecode.Cli -- <command>

ltc options [rates|counting|bits|flags|bgf|userbits|st309|timezones|st262|rp169|color|signal|generator|decoder] [--markdown]
ltc rates
ltc bits --rate 25
ltc explain 01:00:00;00 --rate 29.97
ltc explain "06 01 02 0D 07 03 01 00 FC BF"
ltc encode 10:00:00:00 --rate 25 --text REEL --table
ltc generate tc.wav --start 00:59:50:00 --rate 25 --seconds 30 --level -12 --format pcm24
ltc generate tod.wav --now --rate 29.97df --bgf 010
ltc read tc.wav [--rate 25] [--channel 2] [--csv | --summary]
ltc userbits 47260926 --bgf 100
ltc timezones
ltc date 2026-09-26 --tz -07:00 --dst            # → --ub 47260926 --bgf 100
ltc date today --mjd --tz +05:30 --clock
ltc pageline 3.0 41 42 43 --checksum
ltc pageline --control 3 12 34
ltc pageline --aux 21:45:12:07 --rate 25 [--aux-cf]
ltc generate aux.wav --start 01:00:00:00 --rate 29.97df --aux-start 10:00:00;00 --seconds 10
ltc message "HELLO WORLD" --id 7
ltc generate dated.wav --start 23:59:50:00 --rate 25 --date 2026-12-31 --tz +01:00 --clock --seconds 20
ltc generate msg.wav --rate 30 --message "HELLO WORLD" --seconds 5
ltc info 01:00:00;00 --rate 29.97
ltc add 00:09:59;29 1 --rate 29.97
ltc diff 01:00:00:00 02:00:00:00 --rate 23.98
ltc convert 00:00:10:00 --rate 25 --to 29.97df
```

## LTC Explorer (MAUI)

* **Generate** — every generator and codeword option (rate, start or time-of-day, user bits as hex or text, all eight BGF combinations, color frame, polarity correction, sample rate, WAV format, level, rise time, speed, invert, reverse), a live waveform of the first codeword with its bit cells, and Save WAV.
* **Read** — open a WAV file (or the last generated one), choose rate/auto-detect, channel and minimum level, and get a summary plus every decoded frame.
* **Codeword** — type a time code, hex bytes or bits; see the 80-bit grid colored by field, a field-by-field explanation and the full bit table.
* **User Bits** — build the binary groups in every mode: raw, 4 characters, ST 309 date/time zone (date, all 64 time zone codes, DST, YYMMDD/MJD, clock time), ST 262 single frame, control code, auxiliary time address or a multi-frame message string; see them explained and send them to Generate.
* **Calculator** — add, difference, convert between rates, real time, color framing.
* **Reference** — the whole `LtcOptions` catalog.

On Windows, generated files go to `Documents\LtcExplorer`; on other platforms they are offered through the share sheet.

## Build

```
./build.ps1                     # Debug and Release: library, CLI, tests (and the MAUI app on Windows)
./build.ps1 -Configuration Release -SkipApp

dotnet test tests/LinearTimecode.Tests -c Release
dotnet build samples/LtcExplorer -c Release -f net10.0-windows10.0.19041.0   # needs the MAUI workload
```

`LinearTimecode.slnx` includes the MAUI app, so building the whole solution needs the MAUI workload (`dotnet workload install maui`).

## Notes on interpretation

* **Frame pairs.** At 47.95/48/50/59.94/60 fps the address and the codeword count frame pairs (§12). `Timecode.Frames` is the value in the codeword; `VideoFrameIndex` gives the first video frame of the pair. LTC alone can't distinguish 50 fps from 25 fps, so the decoder reports the 24/25/30 rate unless you pass one.
* **Rate detection.** Without a rate, `LtcDecoder` uses the measured codeword rate (24 vs 25 vs 30), the highest frame number seen and the drop-frame flag. 29.97 vs 30 NDF and 23.98 vs 24 are separated by the measured rate (0.1 %), which needs a clean, nominal-speed signal; pass the rate when you know it.
* **Unassigned flags.** Bits 10 (24/25-frame) and 11 (24-frame) are written as 0 and reported in `LtcFrame.UnassignedFlagBits` when a legacy source sets them (§9.2.2).
* **ST 309.** With YYMMDD the time address is local time; with MJD it is UTC and the time zone/DST are informational (§5.1.1). The time zone code is the offset in use (e.g. New York sends 05 in winter, 04 + DST in summer). Two-digit years use `DateTimeZone.CenturyPivot` (70 → 1970–2069).
* **ST 262.** Binary byte n is BG 2n−1 (low nibble) + BG 2n (high nibble), so byte 4 = the directory index (page in BG 8, line in BG 7). Message-string directory indexes and specifier bytes are dialect-specific; `PageLineMessageLayout.Default` uses 3.0 / 3.1 / 3.2 with specifier = message ID + frame count. The auxiliary time address follows SMPTE RP 169: the same layout as the primary address (frames, DF/CF flags, seconds, minutes, hours) moved into BG 1–8, so its hours are the ST 262 directory page/line; its drop frame and color frame flags refer only to the auxiliary address.
* **Timing.** `LtcGenerator` computes every edge from the absolute sample index, so 1/1.001 rates never drift, and the first sample is the §9.5 reference transition of bit 0. `LtcDecodedFrame.StartSample` reports the same point on input (sub-sample interpolated).
