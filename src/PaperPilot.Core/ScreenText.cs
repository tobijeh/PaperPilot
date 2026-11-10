namespace PaperPilot.Core;

/// <summary>Integer rectangle. Screen rectangles are physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect Union(PixelRect a, PixelRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    public override string ToString() => $"x={X} y={Y} w={Width} h={Height}";
}

/// <param name="Text">Word exactly as OCR read it.</param>
/// <param name="Normalized">Lowercased, punctuation stripped (full normalization comes in Milestone 4).</param>
/// <param name="Screen">Position on screen in physical pixels.</param>
/// <param name="Image">Position in the image that was fed to OCR.</param>
public sealed record ScreenWord(string Text, string Normalized, PixelRect Screen, PixelRect Image);

public sealed record ScreenLine(int Index, string Text, IReadOnlyList<ScreenWord> Words, PixelRect Screen, PixelRect Image)
{
    public string NormalizedText => string.Join(' ', Words.Select(w => w.Normalized).Where(w => w.Length > 0));
}

public enum ReadStatus
{
    Ok,
    NoTarget,
    TargetClosed,
    TargetMinimized,
    NoFrame,
    Failed,
}

public sealed class ScreenReadResult
{
    public required ReadStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public IReadOnlyList<ScreenLine> Lines { get; init; } = Array.Empty<ScreenLine>();

    /// <summary>Document area that was read, in screen pixels.</summary>
    public PixelRect ContentRect { get; init; }
    public string ContentKind { get; init; } = string.Empty;

    /// <summary>How capture pixels were mapped onto the screen (for troubleshooting).</summary>
    public string Mapping { get; init; } = string.Empty;

    /// <summary>Screen pixels per OCR image pixel (1 unless the image had to be shrunk for OCR).</summary>
    public int Downscale { get; init; } = 1;

    public double CaptureMs { get; init; }
    public double OcrMs { get; init; }
    public uint Dpi { get; init; }

    /// <summary>The BGRA image that went into OCR (only when requested, for the debug view).</summary>
    public byte[]? ImagePixels { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    public bool IsOk => Status == ReadStatus.Ok;
}
