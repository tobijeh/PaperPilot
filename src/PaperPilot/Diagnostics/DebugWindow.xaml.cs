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
using PaperPilot.UI;

namespace PaperPilot;

/// <summary>
/// Debug window (hidden behind a setting once Milestone 5 ships): see what Paper Pilot reads,
/// test scrolling and calibration, and switch the pill through its states.
/// </summary>
public partial class DebugWindow : Window
{
    private const int MaxLogLines = 300;

    private readonly AppController _app;
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Queue<string> _logLines = new();
    private BoxOverlayWindow? _overlay;
    private ScreenReadResult? _lastResult;
    private bool _reading;
    private readonly DispatcherTimer _voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Random _rng = new();
    private double _voicePhase;

    public DebugWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        Title = $"Paper Pilot {Updater.CurrentVersion} — Debug";
        Log.LineWritten += OnLogLine;
        _autoTimer.Tick += async (_, _) => await ReadOnceAsync();
        _app.StateChanged += OnStateChanged;
        _app.TargetChanged += OnTargetChanged;
        _app.ManualInputDetected += OnManualInput;
        _app.Notice += msg => SetStatus(msg, isError: true);
        _app.Changed += UpdateHotkeyText;
        _voiceTimer.Tick += (_, _) =>
        {
            // Speech-like bursts, so the bubble's trail can be checked before the microphone arrives.
            _voicePhase += 0.3 + _rng.NextDouble() * 0.12;
            _app.Bubble.PushLevel(Math.Max(0, Math.Sin(_voicePhase)) * (0.4 + _rng.NextDouble() * 0.6));
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateHotkeyText();
        if (_app.Reader is null)
            SetStatus("Problem: " + (_app.ReaderError ?? "screen reader failed to start."), isError: true);
        else
            SetStatus($"Ready. OCR language: {_app.Reader.OcrLanguage}. Click into your document and press {HotkeyRun.Text}.");
        if (_app.Hotkeys.Errors.Count > 0)
            SetStatus(string.Join(" ", _app.Hotkeys.Errors.Values) + " Open Hotkeys… to pick another.", isError: true);

    }

    private void UpdateHotkeyText()
    {
        var toggle = _app.Settings.Hotkeys.GetValueOrDefault(HotkeyAction.Toggle);
        HotkeyRun.Text = toggle is null || _app.Hotkeys.Errors.ContainsKey(HotkeyAction.Toggle)
            ? "your toggle hotkey (none set — open Hotkeys…, or use “Turn on in 3 s”)"
            : toggle.ToString();
    }

    // ---- Controller events ---------------------------------------------------------------------

    private void OnStateChanged(PilotState state) => StateText.Text = $"State: {state}";

    private async void OnTargetChanged(TargetWindow? target)
    {
        bool on = target is not null;
        ReadButton.IsEnabled = CalibrateButton.IsEnabled = ScrollButton.IsEnabled = on;
        ScrollInfo.Text = "Not calibrated.";
        UpdateTargetText(null);
        if (!on)
        {
            _overlay?.Hide();
            return;
        }
        await ReadOnceAsync();
    }

    private void OnManualInput(ManualInputKind kind, string detail) =>
        ManualText.Text = $"Manual input: {detail} at {DateTime.Now:HH:mm:ss.f}";

    private void SimulateVoice_Changed(object sender, RoutedEventArgs e)
    {
        if (SimulateVoiceCheck.IsChecked == true) _voiceTimer.Start();
        else _voiceTimer.Stop();
    }

    private void PillState_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<PilotState>(tag, out var state))
            _app.SetStateManually(state);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ShowSettings(SettingsWindow.PageId.Hotkeys);
    }

    // ---- Scrolling tests -----------------------------------------------------------------------

    private async void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Reader is null || _app.Target is null) return;
        if (!await CountdownAsync("Calibrating")) return;
        CalibrateButton.IsEnabled = false;
        SetStatus("Calibrating… (one notch down, then back)");
        try
        {
            var result = await _app.Scroller.CalibrateAsync(_app.Reader);
            ScrollInfo.Text = result.Success ? $"{result.Message}" : "Not calibrated.";
            SetStatus(result.Success ? "Calibrated: " + result.Message : "Calibration failed: " + result.Message, !result.Success);
        }
        finally
        {
            CalibrateButton.IsEnabled = _app.Target is not null;
            await ReadOnceAsync();
        }
    }

    private async void Scroll_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Reader is null || _app.Target is null) return;
        if (!int.TryParse(ScrollPxBox.Text, out int px) || px <= 0 || px > 5000)
        {
            SetStatus("Enter a number of pixels between 1 and 5000.", isError: true);
            return;
        }
        if (!_app.Scroller.IsCalibrated)
        {
            SetStatus("Calibrate first.", isError: true);
            return;
        }
        if (!await CountdownAsync("Scrolling")) return;

        ScrollButton.IsEnabled = false;
        try
        {
            var fgBefore = Interop.NativeMethods.GetForegroundWindow();
            var before = await _app.Reader.ReadAsync();
            int delta = -(int)Math.Round(px / _app.Scroller.PixelsPerDelta);
            int duration = SlowCheck.IsChecked == true ? 5000 : ScrollPlanner.DurationFor(px);
            var sw = Stopwatch.StartNew();

            SetStatus($"Scrolling {px} px over {duration} ms…");
            bool completed = await _app.Scroller.ScrollDeltaAsync(delta, duration, _app.Scroller.Method);
            long elapsed = sw.ElapsedMilliseconds;

            await Task.Delay(500); // let the page settle
            var after = await _app.Reader.ReadAsync(includeImage: PreviewCheck.IsChecked == true);
            var fgAfter = Interop.NativeMethods.GetForegroundWindow();
            var shift = before.IsOk && after.IsOk ? ScrollMeasurer.Measure(before.Lines, after.Lines) : null;
            if (completed && shift is { } s) _app.Scroller.Refine(delta, s.Pixels);

            string moved = shift is null ? "couldn't measure (page moved too far, or too little text)" : $"moved {shift.Value.Pixels} px";
            string focus = fgBefore == fgAfter ? "focus unchanged ✓" : "FOCUS CHANGED ✗";
            SetStatus(completed
                ? $"Requested {px} px → {moved}. {focus}. ({_app.Scroller.Method})"
                : $"Stopped by your input after {elapsed} ms → {moved}. {focus}.",
                isError: fgBefore != fgAfter);
            Log.Info($"Scroll test: requested {px}px, completed={completed}, {moved}, {focus}.");
            ScrollInfo.Text = $"{_app.Scroller.Method}: {_app.Scroller.PixelsPerDelta * Scroller.Notch:F0} px per notch.";
            _lastResult = after;
            ShowResult(after, PreviewCheck.IsChecked == true);
        }
        finally
        {
            ScrollButton.IsEnabled = _app.Target is not null;
        }
    }

    private async Task<bool> CountdownAsync(string what)
    {
        if (DelayCheck.IsChecked != true) return true;
        for (int i = 3; i > 0; i--)
        {
            SetStatus($"{what} in {i}… click into your document now.");
            await Task.Delay(1000);
        }
        return _app.Target is not null;
    }

    private async void LockDelayedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app.State != PilotState.Off) _app.TurnOff();
        for (int i = 3; i > 0; i--)
        {
            SetStatus($"Click into the window you want Paper Pilot to follow… {i}");
            await Task.Delay(1000);
        }
        _app.Toggle();
    }

    // ---- Reading -------------------------------------------------------------------------------

    private async Task ReadOnceAsync()
    {
        if (_app.Reader?.Target is null || _reading) return;
        _reading = true;
        try
        {
            bool wantImage = PreviewCheck.IsChecked == true;
            var result = await _app.Reader.ReadAsync(includeImage: wantImage);
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
        var target = _app.Target;
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
            sb.AppendLine($"Target: {_app.Target}");
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
        _app.StateChanged -= OnStateChanged;
        _app.TargetChanged -= OnTargetChanged;
        _app.ManualInputDetected -= OnManualInput;
        _app.Changed -= UpdateHotkeyText;
        _voiceTimer.Stop();
    }
}
