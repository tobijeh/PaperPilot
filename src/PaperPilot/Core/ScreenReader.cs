using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using PaperPilot.Diagnostics;
using PaperPilot.Interop;
using Windows.Globalization;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PaperPilot.Core;

/// <summary>
/// Captures the target window (Windows.Graphics.Capture) and reads its document area with the
/// built-in offline Windows OCR. Returns lines with on-screen rectangles in physical pixels.
/// </summary>
public sealed class ScreenReader : IDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    private readonly OcrEngine _ocr;
    private readonly IDirect3DDevice _device;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private WindowCapture? _capture;
    private TargetWindow? _target;
    private string _lastMapping = string.Empty;

    public ScreenReader()
    {
        _ocr = CreateEngine() ?? throw new InvalidOperationException(
            "Windows OCR has no English language pack. Add \"English (United States)\" under " +
            "Settings > Time & language > Language & region, then restart Paper Pilot.");
        OcrLanguage = _ocr.RecognizerLanguage.LanguageTag;
        Log.Info($"OCR: engine ready ({OcrLanguage}), max image dimension {OcrEngine.MaxImageDimension}px.");

        _device = CaptureInterop.CreateDirect3DDevice();
    }

    public string OcrLanguage { get; }
    public TargetWindow? Target => _target;

    private static OcrEngine? CreateEngine()
    {
        var available = string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
        Log.Info($"OCR: available languages: {available}");

        foreach (var tag in new[] { "en-US", "en-GB", "en" })
        {
            try
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang))
                    return OcrEngine.TryCreateFromLanguage(lang);
            }
            catch
            {
                // try next
            }
        }
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (english is not null) return OcrEngine.TryCreateFromLanguage(english);
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>Start capturing a new target window (replaces any previous one).</summary>
    public void Attach(TargetWindow target)
    {
        Detach();
        _target = target;
        _capture = new WindowCapture(target.Hwnd, _device);
        _lastMapping = string.Empty;
        Log.Info($"ScreenReader: attached to {target}.");
    }

    public void Detach()
    {
        _capture?.Dispose();
        _capture = null;
        _target = null;
    }

    /// <summary>Capture + OCR once. Concurrent calls wait for the one in progress.</summary>
    public async Task<ScreenReadResult> ReadAsync(bool includeImage = false)
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            // Run off the UI thread: pixel copying and OCR shouldn't stall the debug window.
            return await Task.Run(() => ReadCoreAsync(includeImage)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("ScreenReader: read failed", ex);
            return new ScreenReadResult { Status = ReadStatus.Failed, Message = ex.Message };
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<ScreenReadResult> ReadCoreAsync(bool includeImage)
    {
        var target = _target;
        var capture = _capture;
        if (target is null || capture is null)
            return new ScreenReadResult { Status = ReadStatus.NoTarget, Message = "No target window." };
        if (!target.IsAlive || capture.IsClosed)
            return new ScreenReadResult { Status = ReadStatus.TargetClosed, Message = "The target window was closed." };
        if (target.IsMinimized)
            return new ScreenReadResult { Status = ReadStatus.TargetMinimized, Message = "The target window is minimized." };

        var sw = Stopwatch.StartNew();
        using var frame = await capture.GetLatestAsync(FrameTimeout).ConfigureAwait(false);
        if (frame is null)
            return new ScreenReadResult { Status = ReadStatus.NoFrame, Message = "No picture from the window yet." };

        // ---- Where is the captured image on screen? -----------------------------------------
        var bitmap = frame.Bitmap;
        int frameW = Math.Min(frame.ContentWidth, bitmap.PixelWidth);
        int frameH = Math.Min(frame.ContentHeight, bitmap.PixelHeight);
        var visible = target.VisibleBounds;
        var windowRect = target.WindowRect;
        int originX, originY;
        string mapping;
        if (Math.Abs(frameW - visible.Width) <= 1 && Math.Abs(frameH - visible.Height) <= 1)
        {
            originX = visible.Left; originY = visible.Top;
            mapping = "frame = visible bounds";
        }
        else if (Math.Abs(frameW - windowRect.Width) <= 1 && Math.Abs(frameH - windowRect.Height) <= 1)
        {
            originX = windowRect.Left; originY = windowRect.Top;
            mapping = "frame = window rect";
        }
        else
        {
            // Unexpected size: assume extra pixels are invisible borders on left/right/bottom.
            originX = visible.Left - (frameW - visible.Width) / 2;
            originY = visible.Top;
            mapping = $"frame {frameW}x{frameH} vs visible {visible.Width}x{visible.Height} (estimated)";
        }
        if (mapping != _lastMapping)
        {
            Log.Info($"ScreenReader: mapping {mapping}; visible {visible}, window {windowRect}, dpi {target.Dpi}.");
            _lastMapping = mapping;
        }

        // ---- Crop to the document area --------------------------------------------------------
        var content = target.GetContentArea();
        var c = content.ScreenRect;
        int cropX = Math.Clamp(c.Left - originX, 0, frameW);
        int cropY = Math.Clamp(c.Top - originY, 0, frameH);
        int cropR = Math.Clamp(c.Right - originX, 0, frameW);
        int cropB = Math.Clamp(c.Bottom - originY, 0, frameH);
        int cropW = cropR - cropX, cropH = cropB - cropY;
        if (cropW < 8 || cropH < 8)
            return new ScreenReadResult { Status = ReadStatus.Failed, Message = $"Document area is empty ({content.Kind} {c})." };

        int srcStride = bitmap.PixelWidth * 4;
        var src = new byte[srcStride * bitmap.PixelHeight];
        bitmap.CopyToBuffer(src.AsBuffer());

        int factor = (int)Math.Ceiling(Math.Max(cropW, cropH) / (double)OcrEngine.MaxImageDimension);
        if (factor < 1) factor = 1;
        var (pixels, imgW, imgH) = CropAndShrink(src, srcStride, cropX, cropY, cropW, cropH, factor);
        double captureMs = sw.Elapsed.TotalMilliseconds;

        // ---- OCR ------------------------------------------------------------------------------
        sw.Restart();
        OcrResult ocr;
        using (var ocrBitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, imgW, imgH, BitmapAlphaMode.Premultiplied))
        {
            ocr = await _ocr.RecognizeAsync(ocrBitmap);
        }
        double ocrMs = sw.Elapsed.TotalMilliseconds;

        // ---- Map back to screen ----------------------------------------------------------------
        int baseX = originX + cropX, baseY = originY + cropY;
        PixelRect ToScreen(PixelRect r) => new(baseX + r.X * factor, baseY + r.Y * factor, r.Width * factor, r.Height * factor);

        var raw = new List<(PixelRect img, List<ScreenWord> words, string text)>();
        foreach (var line in ocr.Lines)
        {
            var words = new List<ScreenWord>(line.Words.Count);
            var lineImg = default(PixelRect);
            foreach (var w in line.Words)
            {
                var b = w.BoundingRect;
                var img = new PixelRect((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
                lineImg = PixelRect.Union(lineImg, img);
                words.Add(new ScreenWord(w.Text, TextNormalizer.NormalizeWord(w.Text), ToScreen(img), img));
            }
            if (words.Count > 0) raw.Add((lineImg, words, line.Text));
        }

        // Reading order: top to bottom, then left to right.
        var lines = raw
            .OrderBy(l => l.img.Y)
            .ThenBy(l => l.img.X)
            .Select((l, i) => new ScreenLine(i, l.text, l.words, ToScreen(l.img), l.img))
            .ToList();

        var result = new ScreenReadResult
        {
            Status = ReadStatus.Ok,
            Lines = lines,
            ContentRect = new PixelRect(c.Left, c.Top, c.Width, c.Height),
            ContentKind = content.Kind,
            Mapping = mapping,
            Downscale = factor,
            CaptureMs = captureMs,
            OcrMs = ocrMs,
            Dpi = target.Dpi,
            ImagePixels = includeImage ? pixels : null,
            ImageWidth = imgW,
            ImageHeight = imgH,
        };

        Log.Info($"OCR: {lines.Count} lines in {content.Kind} {c} (capture {captureMs:F0} ms, ocr {ocrMs:F0} ms, x{factor})" +
                 (lines.Count > 0 ? $"; first \"{Trim(lines[0].Text)}\" @y={lines[0].Screen.Y}, last \"{Trim(lines[^1].Text)}\" @y={lines[^1].Screen.Y}" : ""));
        return result;
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>Crops a BGRA buffer and shrinks it by an integer factor (box filter) if needed.</summary>
    private static (byte[] pixels, int width, int height) CropAndShrink(
        byte[] src, int srcStride, int x0, int y0, int w, int h, int factor)
    {
        int outW = w / factor, outH = h / factor;
        var dst = new byte[outW * outH * 4];
        if (factor == 1)
        {
            for (int y = 0; y < outH; y++)
                Buffer.BlockCopy(src, (y0 + y) * srcStride + x0 * 4, dst, y * outW * 4, outW * 4);
            return (dst, outW, outH);
        }

        int n = factor * factor;
        for (int oy = 0; oy < outH; oy++)
        {
            for (int ox = 0; ox < outW; ox++)
            {
                int b = 0, g = 0, r = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y0 + oy * factor + dy) * srcStride + (x0 + ox * factor) * 4;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = row + dx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2];
                    }
                }
                int o = (oy * outW + ox) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = 255;
            }
        }
        return (dst, outW, outH);
    }

    public void Dispose()
    {
        Detach();
        _busy.Dispose();
    }
}
