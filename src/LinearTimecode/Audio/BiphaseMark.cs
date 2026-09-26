namespace LinearTimecode.Audio;

/// <summary>
/// Biphase mark modulation (ST 12-1 §9.3): a transition at every bit-cell boundary, plus one at mid-cell for a logical one.
/// Works on half-cells, each represented as a logic level (true = high).
/// </summary>
public static class BiphaseMark
{
    /// <summary>
    /// Encodes <paramref name="bits"/> into <c>2 × bits.Length</c> half-cell levels.
    /// </summary>
    /// <param name="bits">Bits in transmission order.</param>
    /// <param name="previousLevel">Level of the last half-cell before these bits (the first boundary inverts it).</param>
    /// <param name="halfCells">Receives the levels.</param>
    /// <returns>The level of the final half-cell, to pass as <paramref name="previousLevel"/> next time.</returns>
    public static bool Encode(ReadOnlySpan<bool> bits, bool previousLevel, Span<bool> halfCells)
    {
        if (halfCells.Length < bits.Length * 2) throw new ArgumentException("Need two half-cells per bit.", nameof(halfCells));
        bool level = previousLevel;
        for (int i = 0; i < bits.Length; i++)
        {
            level = !level;                 // clock transition at the cell boundary
            halfCells[2 * i] = level;
            if (bits[i]) level = !level;    // extra mid-cell transition for a one
            halfCells[2 * i + 1] = level;
        }
        return level;
    }

    /// <summary>Encodes a codeword (bit 0 first, or bit 79 first when <paramref name="reverse"/>) into 160 half-cells.</summary>
    public static bool Encode(LtcCodeword codeword, bool previousLevel, Span<bool> halfCells, bool reverse = false)
    {
        Span<bool> bits = stackalloc bool[80];
        for (int i = 0; i < 80; i++) bits[i] = codeword.GetBit(reverse ? 79 - i : i);
        return Encode(bits, previousLevel, halfCells);
    }

    /// <summary>
    /// Decodes half-cell levels back into bits, assuming <paramref name="halfCells"/> starts on a cell boundary.
    /// </summary>
    public static bool[] Decode(ReadOnlySpan<bool> halfCells)
    {
        var bits = new bool[halfCells.Length / 2];
        for (int i = 0; i < bits.Length; i++) bits[i] = halfCells[2 * i] != halfCells[2 * i + 1];
        return bits;
    }
}
