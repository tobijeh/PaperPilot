using PaperPilot.Diagnostics;
using PaperPilot.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace PaperPilot.Core;

/// <summary>
/// Keeps a Windows.Graphics.Capture session open on one window and hands out a CPU copy of the
/// most recent frame on request. WGC only delivers frames when the window's content changes, so we
/// always hold on to the newest frame and copy it when asked.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int BufferCount = 3;

    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _latest;
    private Direct3D11CaptureFrame? _pending;
    private bool _copying;
    private bool _disposed;

    public WindowCapture(IntPtr hwnd, IDirect3DDevice device)
    {
        _device = device;
        _item = CaptureInterop.CreateItemForWindow(hwnd);
        _item.Closed += (_, _) =>
        {
            IsClosed = true;
            Log.Info("Capture: target window closed.");
        };

        _poolSize = _item.Size;
        if (_poolSize.Width <= 0 || _poolSize.Height <= 0) _poolSize = new SizeInt32 { Width = 1, Height = 1 };
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, BufferCount, _poolSize);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);
        ConfigureSession(_session);
        _session.StartCapture();
        Log.Info($"Capture: started, item size {_poolSize.Width}x{_poolSize.Height}.");
    }

    public bool IsClosed { get; private set; }

    /// <summary>Asks Windows (11+) for permission to capture without the yellow border. Call once at startup.</summary>
    public static async Task RequestBorderlessAccessAsync()
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                Log.Info($"Capture: borderless access = {status}.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Capture: borderless access request failed: {ex.Message}");
        }
    }

    private static void ConfigureSession(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                session.IsCursorCaptureEnabled = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Capture: could not disable cursor capture: {ex.Message}");
        }

        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            // Windows 10 always shows the border; that's fine.
            Log.Info($"Capture: border can't be hidden on this Windows version ({ex.Message}).");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        var size = frame.ContentSize;
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            // Window was resized: rebuild the pool at the new size. A fresh frame follows.
            frame.Dispose();
            if (size.Width <= 0 || size.Height <= 0) return;
            _poolSize = size;
            lock (_gate)
            {
                if (_disposed) return;
                sender.Recreate(_device, PixelFormat, BufferCount, size);
            }
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            if (_copying)
            {
                _pending?.Dispose();
                _pending = frame;
            }
            else
            {
                _latest?.Dispose();
                _latest = frame;
            }
        }
        _firstFrame.TrySetResult();
    }

    /// <summary>CPU copy of the newest frame, or null if none arrived within the timeout.</summary>
    public async Task<CapturedFrame?> GetLatestAsync(TimeSpan timeout)
    {
        var first = _firstFrame.Task;
        if (!first.IsCompleted && await Task.WhenAny(first, Task.Delay(timeout)) != first)
            return null;

        Direct3D11CaptureFrame frame;
        lock (_gate)
        {
            if (_disposed || _latest is null) return null;
            frame = _latest;
            _copying = true;
        }

        try
        {
            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                bitmap.Dispose();
                bitmap = converted;
            }
            return new CapturedFrame(bitmap, frame.ContentSize.Width, frame.ContentSize.Height);
        }
        finally
        {
            lock (_gate)
            {
                _copying = false;
                if (_pending is not null)
                {
                    _latest?.Dispose();
                    _latest = _pending;
                    _pending = null;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pool.FrameArrived -= OnFrameArrived;
            _latest?.Dispose();
            _pending?.Dispose();
            _latest = _pending = null;
        }
        try { _session.Dispose(); } catch { /* ignore */ }
        try { _pool.Dispose(); } catch { /* ignore */ }
        Log.Info("Capture: stopped.");
    }
}

/// <param name="Bitmap">BGRA8 copy of the frame surface (may be larger than the content).</param>
/// <param name="ContentWidth">Width of the valid window content inside the bitmap.</param>
/// <param name="ContentHeight">Height of the valid window content inside the bitmap.</param>
public sealed record CapturedFrame(SoftwareBitmap Bitmap, int ContentWidth, int ContentHeight) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}
