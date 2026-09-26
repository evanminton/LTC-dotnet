using System.Globalization;

namespace LinearTimecode.BinaryGroups;

/// <summary>What a SMPTE ST 309 time zone code (Table 2) stands for.</summary>
public enum TimeZoneCodeKind
{
    /// <summary>A defined offset from UTC.</summary>
    Offset,
    /// <summary>Undefined; reserved, do not use (26, 27, 33–37).</summary>
    Reserved,
    /// <summary>Undefined; formerly time precision, deprecated (28–31).</summary>
    Deprecated,
    /// <summary>User-defined time offset (38).</summary>
    UserDefined,
    /// <summary>Unknown offset (39).</summary>
    Unknown,
}

/// <summary>
/// A 6-bit SMPTE ST 309 time zone code (00–3F hex, Table 2), carried in binary groups 7 and 8.
/// </summary>
/// <remarks>
/// The code is the offset actually in use: with daylight saving in effect the code for the daylight offset is sent
/// (e.g. New York uses 05 in winter and 04 in summer) and the DST flag is set.
/// </remarks>
public readonly record struct TimeZoneCode
{
    public TimeZoneCode(int code)
    {
        if (code is < 0 or > 0x3F) throw new ArgumentOutOfRangeException(nameof(code), "Time zone codes are 00–3F hex.");
        Code = (byte)code;
    }

    /// <summary>The code, 0x00–0x3F.</summary>
    public byte Code { get; }

    public static TimeZoneCode Utc => new(0x00);
    public static TimeZoneCode UserDefinedOffset => new(0x38);
    public static TimeZoneCode UnknownOffset => new(0x39);

    /// <summary>All 64 codes.</summary>
    public static IReadOnlyList<TimeZoneCode> All { get; } = [.. Enumerable.Range(0, 64).Select(c => new TimeZoneCode(c))];

    /// <summary>All codes that stand for an offset, ordered from UTC−12 to UTC+13.</summary>
    public static IReadOnlyList<TimeZoneCode> Offsets { get; } = [.. All.Where(z => z.Offset is not null).OrderBy(z => z.Offset)];

    public TimeZoneCodeKind Kind => Code switch
    {
        0x26 or 0x27 or (>= 0x33 and <= 0x37) => TimeZoneCodeKind.Reserved,
        0x28 or 0x29 or 0x30 or 0x31 => TimeZoneCodeKind.Deprecated,
        0x38 => TimeZoneCodeKind.UserDefined,
        0x39 => TimeZoneCodeKind.Unknown,
        _ => TimeZoneCodeKind.Offset,
    };

    /// <summary>Offset from UTC (local = UTC + offset), or null when the code is not an offset.</summary>
    public TimeSpan? Offset
    {
        get
        {
            if (Kind != TimeZoneCodeKind.Offset) return null;
            int hi = Code >> 4, lo = Code & 0xF;
            if (Code == 0x32) return new TimeSpan(12, 45, 0);
            if (lo <= 9)
            {
                // "Decimal-looking" hex codes 00–25: 00 = UTC, 01–12 = UTC−1…−12, 13 = +13, 14–25 = +12…+1.
                int n = hi * 10 + lo;
                return n switch
                {
                    0 => TimeSpan.Zero,
                    <= 12 => TimeSpan.FromHours(-n),
                    _ => TimeSpan.FromHours(26 - n),
                };
            }
            // Hex-letter codes xA–xF: half-hour offsets.
            int k = lo - 0xA; // 0..5
            double hours = hi switch
            {
                0 => -(0.5 + k),        // 0A −00:30 … 0F −05:30
                1 => -(6.5 + k),        // 1A −06:30 … 1F −11:30
                2 => 11.5 - k,          // 2A +11:30 … 2F +06:30
                _ => 5.5 - k,           // 3A +05:30 … 3F +00:30
            };
            return TimeSpan.FromHours(hours);
        }
    }

    /// <summary>Two hex digits, e.g. "3A".</summary>
    public string Hex => Code.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>"UTC+05:30", "UTC", or the kind for non-offset codes.</summary>
    public string DisplayName => Offset switch
    {
        { } o when o == TimeSpan.Zero => "UTC",
        { } o => $"UTC{(o < TimeSpan.Zero ? '-' : '+')}{o.Duration():hh\\:mm}",
        null => Kind switch
        {
            TimeZoneCodeKind.UserDefined => "User defined time offset",
            TimeZoneCodeKind.Unknown => "Unknown",
            TimeZoneCodeKind.Deprecated => "Undefined (deprecated)",
            _ => "Undefined (reserved)",
        },
    };

    /// <summary>Example locations from Table 2 (informative), standard time.</summary>
    public string StandardTimeLocation => Code switch
    {
        0x00 => "Greenwich", 0x01 => "Azores", 0x02 => "Mid-Atlantic", 0x03 => "Buenos Aires", 0x04 => "Halifax",
        0x05 => "New York", 0x06 => "Chicago", 0x07 => "Denver", 0x08 => "Los Angeles", 0x09 => "Alaska",
        0x10 => "Hawaii", 0x11 => "Midway Island", 0x12 => "Kwajalein", 0x14 => "New Zealand", 0x15 => "Solomon Islands",
        0x16 => "Guam", 0x17 => "Tokyo", 0x18 => "Beijing", 0x19 => "Bangkok", 0x20 => "Dhaka", 0x21 => "Islamabad",
        0x22 => "Abu Dhabi", 0x23 => "Moscow", 0x24 => "Eastern Europe", 0x25 => "Central Europe",
        0x0D => "Newfoundland", 0x1D => "Marquesas Islands", 0x32 => "Chatham Island",
        _ => "",
    };

    /// <summary>Example locations from Table 2 (informative) that use this code during daylight saving time.</summary>
    public string DaylightSavingLocation => Code switch
    {
        0x03 => "Halifax", 0x04 => "New York", 0x05 => "Chicago", 0x06 => "Denver", 0x07 => "Los Angeles",
        0x13 => "New Zealand", 0x24 => "Central Europe", 0x25 => "United Kingdom", 0x0C => "Newfoundland",
        _ => "",
    };

    /// <summary>One-line description including example locations.</summary>
    public string Description
    {
        get
        {
            string s = Kind switch
            {
                TimeZoneCodeKind.Reserved => "Reserved; do not use.",
                TimeZoneCodeKind.Deprecated => "Formerly signalled time precision; deprecated in ST 309:2012.",
                TimeZoneCodeKind.UserDefined => "User-defined time offset.",
                TimeZoneCodeKind.Unknown => "Offset unknown.",
                _ => DisplayName,
            };
            if (StandardTimeLocation.Length > 0) s += $" — {StandardTimeLocation}";
            if (DaylightSavingLocation.Length > 0) s += $"{(StandardTimeLocation.Length > 0 ? ";" : " —")} {DaylightSavingLocation} (DST)";
            return s;
        }
    }

    /// <summary>The code for an offset, or null when ST 309 has none.</summary>
    public static TimeZoneCode? FromOffset(TimeSpan offset)
    {
        foreach (var z in All) if (z.Offset == offset) return z;
        return null;
    }

    /// <summary>Parses a hex code ("3A", "0x3A"), "UTC", or an offset ("+05:30", "UTC-8", "-03:30").</summary>
    public static TimeZoneCode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Trim();
        if (s.Equals("UTC", StringComparison.OrdinalIgnoreCase) || s.Equals("Z", StringComparison.OrdinalIgnoreCase) || s.Equals("GMT", StringComparison.OrdinalIgnoreCase)) return Utc;
        if (s.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) || s.StartsWith("GMT", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];

        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            int sign = s[0] == '-' ? -1 : 1;
            string[] parts = s[1..].Split(':');
            if (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h) &&
                (parts.Length == 1 || int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                int m = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
                var off = new TimeSpan(sign * h, sign * m, 0);
                return FromOffset(off) ?? throw new FormatException($"ST 309 has no code for UTC{s}.");
            }
        }
        if (s.Length is 1 or 2 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code) && code <= 0x3F)
            return new TimeZoneCode(code);
        throw new FormatException($"'{text}' is not a time zone code (00–3F hex) or an offset like +05:30.");
    }

    public override string ToString() => $"{Hex} {DisplayName}";
}
