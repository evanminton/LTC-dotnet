using LinearTimecode;

namespace LtcExplorer.Pages;

/// <summary>Draws one codeword of LTC audio with its bit cells labelled.</summary>
internal sealed class WaveformDrawable : IDrawable
{
    public float[] Samples { get; set; } = [];
    public LtcCodeword? Codeword { get; set; }

    public void Draw(ICanvas canvas, RectF r)
    {
        canvas.StrokeColor = Colors.Gray.WithAlpha(0.35f);
        canvas.StrokeSize = 1;
        canvas.DrawLine(r.Left, r.Center.Y, r.Right, r.Center.Y);
        if (Samples.Length < 2) return;

        float waveTop = r.Top + 22, waveHeight = r.Height - 44;
        // Bit cell grid: 80 cells across the width.
        if (Codeword is { } cw)
        {
            float cell = r.Width / 80f;
            canvas.FontSize = 9;
            for (int i = 0; i < 80; i++)
            {
                float x = r.Left + i * cell;
                bool sync = i >= 64;
                canvas.FillColor = (sync ? Colors.Orange : cw[i] ? Colors.Teal : Colors.SlateGray).WithAlpha(i % 2 == 0 ? 0.10f : 0.18f);
                canvas.FillRectangle(x, waveTop, cell, waveHeight);
                canvas.FontColor = sync ? Colors.Orange : Colors.Gray;
                canvas.DrawString(cw[i] ? "1" : "0", x, r.Bottom - 20, cell, 18, HorizontalAlignment.Center, VerticalAlignment.Center);
                if (i % 8 == 0) canvas.DrawString(i.ToString(), x, r.Top, cell * 4, 18, HorizontalAlignment.Left, VerticalAlignment.Center);
            }
        }

        canvas.StrokeColor = Colors.DeepSkyBlue;
        canvas.StrokeSize = 1.5f;
        var path = new PathF();
        float peak = Math.Max(0.001f, Samples.Max(s => Math.Abs(s)));
        for (int i = 0; i < Samples.Length; i++)
        {
            float x = r.Left + r.Width * i / (Samples.Length - 1);
            float y = waveTop + waveHeight / 2 - Samples[i] / peak * (waveHeight / 2 - 4);
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        canvas.DrawPath(path);
    }
}

/// <summary>Draws the 80 bits of a codeword as a labelled 8 × 10 grid, colored by field.</summary>
internal sealed class BitGridDrawable : IDrawable
{
    public LtcCodeword Codeword { get; set; }
    public TimecodeBase Layout { get; set; } = TimecodeBase.Base30;

    public void Draw(ICanvas canvas, RectF r)
    {
        const int cols = 16;
        int rows = 80 / cols;
        float cw = r.Width / cols, ch = Math.Min(44, r.Height / rows);
        canvas.FontSize = 10;
        for (int i = 0; i < 80; i++)
        {
            float x = r.Left + (i % cols) * cw, y = r.Top + (i / cols) * ch;
            string name = LtcBits.NameOf(i, Layout);
            Color c = name.StartsWith("Sync", StringComparison.Ordinal) ? Colors.Orange
                : name.StartsWith("Binary group ", StringComparison.Ordinal) && !name.Contains("flag", StringComparison.Ordinal) ? Colors.MediumPurple
                : name.Contains("flag", StringComparison.OrdinalIgnoreCase) || name.Contains("polarity", StringComparison.OrdinalIgnoreCase) ? Colors.Crimson
                : name.StartsWith("Unassigned", StringComparison.Ordinal) ? Colors.Gray
                : Colors.Teal;
            bool on = Codeword[i];
            canvas.FillColor = c.WithAlpha(on ? 0.85f : 0.15f);
            canvas.FillRoundedRectangle(x + 1, y + 1, cw - 2, ch - 2, 4);
            canvas.FontColor = on ? Colors.White : Colors.Gray;
            canvas.DrawString($"{i}", x, y + 2, cw, ch / 2, HorizontalAlignment.Center, VerticalAlignment.Top);
            canvas.DrawString(on ? "1" : "0", x, y + ch / 2 - 2, cw, ch / 2, HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }
}
