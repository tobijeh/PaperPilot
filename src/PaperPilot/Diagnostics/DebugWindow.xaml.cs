using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PaperPilot.Core;
using PaperPilot.Diagnostics;

namespace PaperPilot;

/// <summary>
/// Milestone 1 debug window: lock onto a window, capture + OCR it, list the lines and their
/// on-screen rectangles, and optionally draw those rectangles on screen to verify coordinates.
/// </summary>
public partial class DebugWindow : Window
{
    private const int MaxLogLines = 300;

    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Queue<string> _logLines = new();
    private ScreenReader? _reader;
    private GlobalHotkeys? _hotkeys;
    private BoxOverlayWindow? _overlay;
    private ScreenReadResult? _lastResult;
    private bool _reading;
    private UpdateInfo? _pendingUpdate;

    public DebugWindow()
    {
        InitializeComponent();
        Title = $"Paper Pilot {Updater.CurrentVersion} — Debug (Milestone 1: See)";
        Log.LineWritten += OnLogLine;
        _autoTimer.Tick += async (_, _) => await ReadOnceAsync();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RegisterLockHotkey();

        try
        {
            await WindowCapture.RequestBorderlessAccessAsync();
            _reader = new ScreenReader();
            SetStatus($"Ready. OCR language: {_reader.OcrLanguage}. Click into your document and press {HotkeyRun.Text}.");
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            SetStatus("Problem: " + ex.Message, isError: true);
        }

        // Quietly look for a newer version once at startup (release builds only).
        if (Updater.IsReleaseBuild)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await CheckForUpdatesAsync(userRequested: false);
        }
    }

    // ---- Updates -------------------------------------------------------------------------------

    private async Task CheckForUpdatesAsync(bool userRequested)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            var info = await Updater.CheckAsync();
            if (info is null)
            {
                if (userRequested) SetStatus($"You have the latest version ({Updater.CurrentVersion}).");
                return;
            }
            _pendingUpdate = info;
            UpdateText.Text = $"Paper Pilot {info.Version} is available (you have {Updater.CurrentVersion}).";
            UpdateNowButton.IsEnabled = true;
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            if (userRequested) SetStatus("Couldn't check for updates (are you online?). " + ex.Message, isError: true);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userRequested: true);

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        UpdateNowButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = $"Downloading Paper Pilot {_pendingUpdate.Version}…";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress.Value = p);
            await Updater.InstallAsync(_pendingUpdate, progress);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            UpdateText.Text = "Update failed: " + ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateNowButton.IsEnabled = true;
        }
    }

    private void RegisterLockHotkey()
    {
        _hotkeys = new GlobalHotkeys();
        // Temporary hotkey for Milestone 1. Milestone 2 makes it customizable.
        var candidates = new (ModifierKeys mods, Key key)[]
        {
            (ModifierKeys.None, Key.F3),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.F3),
            (ModifierKeys.Control | ModifierKeys.Alt, Key.P),
        };
        foreach (var (mods, key) in candidates)
        {
            if (_hotkeys.TryRegister(mods, key, LockOntoForeground, out _, out var error))
            {
                HotkeyRun.Text = GlobalHotkeys.Describe(mods, key);
                return;
            }
            SetStatus(error ?? "Hotkey failed.", isError: true);
        }
        HotkeyRun.Text = "(no hotkey available — use “Lock in 3 s”)";
    }

    // ---- Locking onto a window -----------------------------------------------------------------

    private async void LockOntoForeground()
    {
        if (_reader is null)
        {
            SetStatus("Can't lock yet: the screen reader failed to start (see log).", isError: true);
            return;
        }

        var target = TargetWindow.FromForeground(out var reason);
        if (target is null)
        {
            Log.Info($"Lock ignored: {reason}");
            SetStatus(reason ?? "No window to lock onto.", isError: true);
            return;
        }

        try
        {
            _reader.Attach(target);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start capturing {target}", ex);
            SetStatus($"Could not capture that window: {ex.Message}", isError: true);
            return;
        }

        Log.Info($"Locked onto {target}.");
        ReadButton.IsEnabled = true;
        UpdateTargetText(null);
        await ReadOnceAsync();
    }

    private async void LockDelayedButton_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 3; i > 0; i--)
        {
            SetStatus($"Click into the window you want to lock onto… {i}");
            await Task.Delay(1000);
        }
        LockOntoForeground();
    }

    // ---- Reading -------------------------------------------------------------------------------

    private async Task ReadOnceAsync()
    {
        if (_reader?.Target is null || _reading) return;
        _reading = true;
        try
        {
            bool wantImage = PreviewCheck.IsChecked == true;
            var result = await _reader.ReadAsync(includeImage: wantImage);
            _lastResult = result;
            ShowResult(result, wantImage);
        }
        finally
        {
            _reading = false;
        }
    }

    private void ShowResult(ScreenReadResult result, bool showImage)
    {
        UpdateTargetText(result);

        if (!result.IsOk)
        {
            SetStatus($"{result.Status}: {result.Message}", isError: true);
            _overlay?.Hide();
            return;
        }

        int selected = LinesList.SelectedIndex;
        LinesList.ItemsSource = result.Lines;
        if (selected >= 0 && selected < result.Lines.Count) LinesList.SelectedIndex = selected;

        int words = result.Lines.Sum(l => l.Words.Count);
        SetStatus($"{result.Timestamp:HH:mm:ss}  {result.Lines.Count} lines, {words} words.  " +
                  $"Capture {result.CaptureMs:F0} ms, OCR {result.OcrMs:F0} ms" +
                  (result.Downscale > 1 ? $", image shrunk ×{result.Downscale} for OCR" : "") +
                  (result.Lines.Count == 0 ? "  — no text found." : ""));

        if (showImage) DrawPreview(result);
        else
        {
            PreviewImage.Source = null;
            PreviewCanvas.Children.Clear();
        }

        if (OverlayCheck.IsChecked == true)
        {
            _overlay ??= new BoxOverlayWindow();
            _overlay.ShowResult(result);
        }
    }

    private void UpdateTargetText(ScreenReadResult? result)
    {
        var target = _reader?.Target;
        if (target is null)
        {
            TargetText.Text = "No target yet.";
            return;
        }
        target.RefreshTitle();
        var sb = new StringBuilder();
        sb.AppendLine($"Target:   {target.Title}");
        sb.AppendLine($"Process:  {target.ProcessName} (pid {target.ProcessId}), class {target.ClassName}, hwnd 0x{target.Hwnd.ToInt64():X}");
        sb.AppendLine($"DPI:      {target.Dpi} ({target.Dpi * 100 / 96}% scaling)   Visible bounds {target.VisibleBounds}   Client {target.ClientRect}");
        if (result is { IsOk: true })
            sb.Append($"Reading:  {result.ContentKind} {result.ContentRect}   ({result.Mapping})");
        else
            sb.Append($"Reading:  {target.GetContentArea().Kind} {target.GetContentArea().ScreenRect}");
        TargetText.Text = sb.ToString();
    }

    private void DrawPreview(ScreenReadResult result)
    {
        PreviewCanvas.Children.Clear();
        if (result.ImagePixels is null || result.ImageWidth <= 0) return;

        var bmp = BitmapSource.Create(result.ImageWidth, result.ImageHeight, 96, 96, PixelFormats.Bgra32, null,
            result.ImagePixels, result.ImageWidth * 4);
        bmp.Freeze();
        PreviewImage.Source = bmp;
        PreviewGrid.Width = result.ImageWidth;
        PreviewGrid.Height = result.ImageHeight;

        var stroke = (Brush)FindResource("PilotBlue");
        double thickness = Math.Max(1, result.ImageWidth / 400.0);
        foreach (var line in result.Lines)
        {
            bool isSelected = LinesList.SelectedItem is ScreenLine sel && sel.Index == line.Index;
            var rect = new Rectangle
            {
                Width = Math.Max(1, line.Image.Width),
                Height = Math.Max(1, line.Image.Height),
                Stroke = stroke,
                StrokeThickness = isSelected ? thickness * 3 : thickness,
                Fill = isSelected ? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x59, 0xFD)) : null,
            };
            Canvas.SetLeft(rect, line.Image.X);
            Canvas.SetTop(rect, line.Image.Y);
            PreviewCanvas.Children.Add(rect);
        }
    }

    // ---- UI handlers ---------------------------------------------------------------------------

    private async void ReadButton_Click(object sender, RoutedEventArgs e) => await ReadOnceAsync();

    private void AutoReadCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoReadCheck.IsChecked == true) _autoTimer.Start();
        else _autoTimer.Stop();
    }

    private void OverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (OverlayCheck.IsChecked == true)
        {
            _overlay ??= new BoxOverlayWindow();
            if (_lastResult is { IsOk: true }) _overlay.ShowResult(_lastResult);
        }
        else
        {
            _overlay?.Hide();
        }
    }

    private void LinesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lastResult is { IsOk: true } && PreviewCheck.IsChecked == true) DrawPreview(_lastResult);
    }

    private void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var r = _lastResult;
        if (r is not { IsOk: true })
        {
            SetStatus("Nothing to save yet — read a window first.", isError: true);
            return;
        }
        try
        {
            Directory.CreateDirectory(Log.LogFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var txtPath = System.IO.Path.Combine(Log.LogFolder, $"snapshot-{stamp}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Target: {_reader?.Target}");
            sb.AppendLine($"DPI: {r.Dpi}, content: {r.ContentKind} {r.ContentRect}, mapping: {r.Mapping}, downscale x{r.Downscale}");
            foreach (var l in r.Lines)
                sb.AppendLine($"{l.Index,3}  [{l.Screen}]  {l.Text}");
            File.WriteAllText(txtPath, sb.ToString());

            if (r.ImagePixels is not null)
            {
                var bmp = BitmapSource.Create(r.ImageWidth, r.ImageHeight, 96, 96, PixelFormats.Bgra32, null, r.ImagePixels, r.ImageWidth * 4);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(System.IO.Path.Combine(Log.LogFolder, $"snapshot-{stamp}.png"));
                enc.Save(fs);
            }
            SetStatus($"Saved snapshot-{stamp} to the log folder.");
        }
        catch (Exception ex)
        {
            Log.Error("Save snapshot failed", ex);
            SetStatus("Could not save snapshot: " + ex.Message, isError: true);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.LogFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.LogFolder}\"") { UseShellExecute = true });
    }

    // ---- Misc ----------------------------------------------------------------------------------

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? (Brush)FindResource("DeepBlue") : (Brush)FindResource("Navy");
        StatusText.FontStyle = isError ? FontStyles.Italic : FontStyles.Normal;
    }

    private void OnLogLine(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
            LogBox.Text = string.Join(Environment.NewLine, _logLines);
            LogBox.ScrollToEnd();
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Log.LineWritten -= OnLogLine;
        _autoTimer.Stop();
        _overlay?.Close();
        _hotkeys?.Dispose();
        _reader?.Dispose();
    }
}
