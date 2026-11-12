using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperPilot.Core;
using PaperPilot.Diagnostics;

namespace PaperPilot.UI;

/// <summary>
/// Settings, in the style of Wirl: a sidebar, one calm page at a time, plain words, and every
/// change applies immediately (no Save buttons).
/// </summary>
public partial class SettingsWindow : Window
{
    public enum PageId { Home, Hotkeys, Listening, Scrolling, Bubble, About }

    private static readonly (PageId id, string label)[] Pages =
    {
        (PageId.Home, "Home"),
        (PageId.Hotkeys, "Hotkeys"),
        (PageId.Listening, "Listening"),
        (PageId.Scrolling, "Scrolling"),
        (PageId.Bubble, "Bubble"),
        (PageId.About, "About"),
    };

    private static readonly (HotkeyAction action, string title, string description)[] HotkeyRows =
    {
        (HotkeyAction.Toggle, "Turn Paper Pilot on or off", "Locks onto the window in front and shows the bubble."),
        (HotkeyAction.PauseResume, "Pause or resume", "The bubble stays, but nothing moves until you resume."),
        (HotkeyAction.NudgeDown, "Nudge down", "A small scroll down. Counts as you taking over."),
        (HotkeyAction.NudgeUp, "Nudge up", "A small scroll up. Counts as you taking over."),
    };

    private readonly AppController _app;
    private readonly Action _openDebug;
    private readonly Dictionary<PageId, RadioButton> _navItems = new();
    private PageId _page;
    private HotkeyAction? _recording;
    private readonly Dictionary<HotkeyAction, string> _hotkeyMessages = new();
    private int _versionClicks;
    private DispatcherTimer? _previewTimer;

    public SettingsWindow(AppController app, Action openDebug, PageId page = PageId.Home)
    {
        _app = app;
        _openDebug = openDebug;
        _page = page;
        InitializeComponent();
        BuildNav();

        _app.Changed += OnAppChanged;
        _app.Updates.Changed += OnUpdatesChanged;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => StopRecording();
        Closed += (_, _) =>
        {
            StopRecording();
            _previewTimer?.Stop();
            _app.Changed -= OnAppChanged;
            _app.Updates.Changed -= OnUpdatesChanged;
        };
        ShowPage(page);
    }

    public void ShowPage(PageId page)
    {
        _page = page;
        if (_navItems.TryGetValue(page, out var item)) item.IsChecked = true;
        Render();
        Scroller.ScrollToTop();
    }

    private void BuildNav()
    {
        foreach (var (id, label) in Pages)
        {
            var item = new RadioButton { Content = label, Style = Res<Style>("NavItem"), GroupName = "nav" };
            item.Checked += (_, _) =>
            {
                if (_page == id) return;
                StopRecording();
                _page = id;
                Render();
                Scroller.ScrollToTop();
            };
            _navItems[id] = item;
            Nav.Children.Add(item);
        }
    }

    private void OnAppChanged()
    {
        if (_recording is null) Render();
    }

    private void OnUpdatesChanged()
    {
        if (_recording is null && _page is PageId.About or PageId.Home) Render();
    }

    private void Render()
    {
        _previewTimer?.Stop();
        Page.Content = _page switch
        {
            PageId.Home => HomePage(),
            PageId.Hotkeys => HotkeysPage(),
            PageId.Listening => ListeningPage(),
            PageId.Scrolling => ScrollingPage(),
            PageId.Bubble => BubblePage(),
            PageId.About => AboutPage(),
            _ => null,
        };
    }

    // ===== Pages ===================================================================================

    private UIElement HomePage()
    {
        var s = _app.Settings;
        var toggle = s.Hotkeys.GetValueOrDefault(HotkeyAction.Toggle);
        string title = s.Enabled ? "Paper Pilot is on" : "Paper Pilot is off";
        string sub;
        if (!s.Enabled) sub = "Switch it on to use your hotkeys.";
        else if (_app.State == PilotState.Off)
            sub = toggle is null ? "Set a hotkey to start." : $"Click into your sermon notes and press {toggle}. Press it again to stop.";
        else sub = $"{AppController.WordFor(_app.State)} in “{Shorten(_app.Target?.Title ?? "your notes", 50)}”.";

        var status = new StackPanel();
        status.Children.Add(Row(
            Stack(Text(title, size: 20, weight: FontWeights.ExtraBold), Muted(sub, top: 4)),
            Switch(s.Enabled, on => _app.SetEnabled(on))));
        status.Children.Add(Divider());
        var attention = _app.Attention;
        if (attention.Count == 0)
            status.Children.Add(Text("Everything is set up", color: "Ok", weight: FontWeights.SemiBold));
        else
            foreach (var item in attention)
                status.Children.Add(Text(item, color: "Attention", weight: FontWeights.SemiBold, wrap: true));

        var page = new StackPanel();
        page.Children.Add(Card(status));

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-6, 0, -6, 0) };
        grid.Children.Add(SummaryCard(PageId.Hotkeys, "Hotkeys", toggle is null ? "No on/off hotkey yet" : $"{toggle} turns it on and off"));
        grid.Children.Add(SummaryCard(PageId.Listening, "Listening", "Microphone arrives in the next update"));
        grid.Children.Add(SummaryCard(PageId.Scrolling, "Scrolling", $"Your line stays {s.AnchorFraction:P0} from the top"));
        grid.Children.Add(SummaryCard(PageId.Bubble, "Bubble",
            $"{(s.BubblePosition == BubblePosition.TopCenter ? "Top centre" : "Top right")} · words {(s.ShowStateWords ? "on" : "off")}"));
        grid.Children.Add(SummaryCard(PageId.About, "About", AboutSummary()));
        page.Children.Add(grid);
        return page;
    }

    private string AboutSummary() => _app.Updates.Status switch
    {
        UpdateStatus.Ready => $"Update ready · {_app.Updates.Available?.Version}",
        UpdateStatus.Downloading => "Downloading an update…",
        _ => $"Version {Updater.CurrentVersion}",
    };

    private UIElement HotkeysPage()
    {
        var page = Header("Hotkeys",
            "Click a key, then press the key or combination you want. A button on your presentation clicker works too. " +
            "Hotkeys work while your notes are in front, and never move focus.");

        var rows = new StackPanel();
        for (int i = 0; i < HotkeyRows.Length; i++)
        {
            var (action, title, description) = HotkeyRows[i];
            if (i > 0) rows.Children.Add(Divider());

            var binding = _app.Settings.Hotkeys.GetValueOrDefault(action);
            bool recording = _recording == action;
            var chip = new Button
            {
                Style = Res<Style>("KeyChip"),
                Content = recording ? "Press new keys…" : binding?.ToString() ?? "Not set",
                Opacity = recording || binding is not null ? 1 : 0.7,
            };
            if (recording) chip.SetResourceReference(Control.BorderBrushProperty, "Accent");
            chip.Click += (_, _) => StartRecording(action);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(chip);
            if (binding is not null && !recording)
            {
                var clear = LinkButton("Clear", () => SetHotkey(action, null));
                clear.Margin = new Thickness(14, 0, 0, 0);
                right.Children.Add(clear);
            }

            var text = Stack(Text(title, style: "H3"), Muted(description));
            string? message = _hotkeyMessages.GetValueOrDefault(action) ?? _app.Hotkeys.Errors.GetValueOrDefault(action);
            if (message is not null) text.Children.Add(Text(message, color: "Attention", weight: FontWeights.SemiBold, wrap: true, top: 6));
            rows.Children.Add(Row(text, right));
        }
        page.Children.Add(Card(rows));

        var reset = new Button { Content = "Reset to defaults", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            StopRecording();
            _hotkeyMessages.Clear();
            _app.Settings.Hotkeys = AppSettings.DefaultHotkeys();
            _app.Settings.Save();
            _app.ApplyHotkeys();
        };
        page.Children.Add(Row(Muted($"Default: {AppSettings.DefaultHotkeys()[HotkeyAction.Toggle]} turns Paper Pilot on and off."), reset));
        return page;
    }

    private UIElement ListeningPage()
    {
        var page = Header("Listening", "Paper Pilot listens on this computer only. Nothing you say leaves it.");
        page.Children.Add(Card(Stack(
            Text("Microphone and speech", style: "H3"),
            Muted("Choosing your microphone, a level meter and the speech model arrive in the next update."))));
        return page;
    }

    private UIElement ScrollingPage()
    {
        var s = _app.Settings;
        var page = Header("Scrolling",
            "Paper Pilot keeps the line you're reading at the same height, and only ever scrolls down. " +
            "Scroll yourself at any time and it steps aside.");

        // Where the current line sits, with a little page diagram.
        var label = Muted("");
        var marker = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        marker.SetResourceReference(Border.BackgroundProperty, "Accent");
        var diagram = new Border
        {
            Width = 74, Height = 96, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { Lines(), marker } },
            Margin = new Thickness(18, 0, 0, 0),
        };
        diagram.SetResourceReference(Border.BackgroundProperty, "Input");
        diagram.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        void UpdateAnchor(double f)
        {
            label.Text = $"Your line sits {f:P0} of the way down the window.";
            marker.Margin = new Thickness(6, 96 * f - 1.5, 6, 0);
        }
        var slider = Slider(20, 50, s.AnchorFraction * 100, 1, v =>
        {
            s.AnchorFraction = Math.Round(v) / 100.0;
            UpdateAnchor(s.AnchorFraction);
        });
        UpdateAnchor(s.AnchorFraction);
        page.Children.Add(Card(Row(Stack(Text("Where your line sits", style: "H3"), label, Spaced(slider, 14)), diagram)));

        page.Children.Add(Card(Stack(
            Text("Nudge size", style: "H3"),
            Muted("How far the nudge hotkeys move the page."),
            Spaced(Segmented(s.NudgeSize, new[] { (NudgeSize.Small, "Small"), (NudgeSize.Medium, "Medium"), (NudgeSize.Large, "Large") },
                v => { s.NudgeSize = v; s.Save(); }), 12))));
        return page;

        static UIElement Lines()
        {
            var p = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };
            foreach (var w in new[] { 1.0, 0.85, 0.95, 0.6, 1.0, 0.9, 0.75, 1.0, 0.8 })
            {
                var line = new Border { Height = 3, Width = 52 * w, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.35 };
                line.SetResourceReference(Border.BackgroundProperty, "Text2");
                p.Children.Add(line);
            }
            return p;
        }
    }

    private UIElement BubblePage()
    {
        var s = _app.Settings;
        var page = Header("Bubble",
            "The bubble is all you see while Paper Pilot is on. The plane is bright blue when it's following you, " +
            "solid when it's listening and holding still, and grey when it isn't listening. Your voice trails behind it.");

        // Live preview cycling through the states.
        var preview = new BubbleView();
        preview.ApplySize(s.PillSize);
        var stage = new Border
        {
            Height = 110, CornerRadius = new CornerRadius(12),
            Child = new Grid { Children = { preview } },
        };
        preview.HorizontalAlignment = HorizontalAlignment.Center;
        preview.VerticalAlignment = VerticalAlignment.Center;
        stage.SetResourceReference(Border.BackgroundProperty, "PreviewStage");
        page.Children.Add(Card(stage));
        StartPreview(preview, s.ShowStateWords);

        page.Children.Add(Card(Stack(
            Text("Size", style: "H3"),
            Spaced(Segmented(s.PillSize, new[] { (PillSize.Small, "Small"), (PillSize.Medium, "Medium") },
                v => { s.PillSize = v; s.Save(); _app.Bubble.ApplyAppearance(); Render(); }), 10))));

        var position = Stack(
            Text("Position", style: "H3"),
            Muted("Drag the bubble anywhere; it remembers the spot for each screen."),
            Spaced(Segmented(s.BubblePosition, new[] { (BubblePosition.TopRight, "Top right"), (BubblePosition.TopCenter, "Top centre") },
                v =>
                {
                    s.BubblePosition = v;
                    _app.Bubble.ResetPosition(_app.Target?.Hwnd ?? IntPtr.Zero);
                }), 12));
        if (s.PillPositions.Count > 0)
            position.Children.Add(Spaced(LinkButton("Put it back at the top", () =>
            {
                _app.Bubble.ResetPosition(_app.Target?.Hwnd ?? IntPtr.Zero);
                Render();
            }), 10));
        page.Children.Add(Card(position));

        page.Children.Add(Card(Row(
            Stack(Text("Show a word when it changes", style: "H3"), Muted("For a moment, e.g. “Following” or “Paused”.")),
            Switch(s.ShowStateWords, on => { s.ShowStateWords = on; s.Save(); Render(); }))));

        var opacityLabel = Muted("");
        void UpdateOpacity() => opacityLabel.Text = $"{s.PillIdleOpacity:P0}. It brightens for a moment whenever something changes.";
        UpdateOpacity();
        page.Children.Add(Card(Stack(
            Text("Brightness when idle", style: "H3"), opacityLabel,
            Spaced(Slider(30, 100, s.PillIdleOpacity * 100, 5, v =>
            {
                s.PillIdleOpacity = Math.Round(v) / 100.0;
                UpdateOpacity();
                _app.Bubble.ApplyAppearance();
            }), 14))));
        return page;
    }

    private void StartPreview(BubbleView preview, bool words)
    {
        var states = new[] { PilotState.Holding, PilotState.Following, PilotState.Following, PilotState.Holding, PilotState.Paused, PilotState.Problem };
        int index = 0, ticks = 0;
        var rng = new Random(7);
        double phase = 0;
        preview.SetState(states[0], "Listening", words);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _previewTimer.Tick += (_, _) =>
        {
            ticks++;
            // Speech-like levels: syllable bursts with short gaps.
            phase += 0.33 + rng.NextDouble() * 0.1;
            double level = Math.Max(0, Math.Sin(phase)) * (0.45 + rng.NextDouble() * 0.5);
            if ((ticks / 25) % 4 == 3) level *= 0.1; // breath
            preview.PushLevel(level);
            if (ticks % 75 == 0)
            {
                index = (index + 1) % states.Length;
                if (states[index] != preview.State) preview.SetState(states[index], AppController.WordFor(states[index]), words);
            }
        };
        _previewTimer.Start();
    }

    private UIElement AboutPage()
    {
        var s = _app.Settings;
        var page = new StackPanel();

        var version = new Button { Style = Res<Style>("LinkButton"), Content = $"Version {Updater.CurrentVersion}" };
        version.SetResourceReference(Control.ForegroundProperty, "Text2");
        version.Click += (_, _) =>
        {
            if (s.DeveloperOptions) return;
            if (++_versionClicks >= 7)
            {
                s.DeveloperOptions = true;
                s.Save();
                Render();
            }
        };
        var icon = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")), Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(icon);
        head.Children.Add(Stack(Text("Paper Pilot", size: 20, weight: FontWeights.ExtraBold), version));
        head.Children[1].SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        page.Children.Add(Card(Stack(head, Muted("Scrolls your notes while you preach, so you can look up.", top: 12))));

        // Updates
        var u = _app.Updates;
        string updateText = u.Status switch
        {
            UpdateStatus.Ready => $"Version {u.Available?.Version} is ready. It installs when you restart Paper Pilot or quit it.",
            UpdateStatus.Downloading => $"Downloading version {u.Available?.Version}… {u.Progress:P0}",
            UpdateStatus.Checking => "Checking…",
            UpdateStatus.UpToDate => "You have the latest version.",
            UpdateStatus.Failed => u.Error ?? "Couldn't check for updates.",
            UpdateStatus.NotAvailable => "This copy can't update itself. Download the latest PaperPilot.exe from GitHub.",
            _ => "Paper Pilot checks once a day, and never while it's on.",
        };
        Button? updateButton = u.Status switch
        {
            UpdateStatus.Ready => Primary("Restart to update", () =>
            {
                if (_app.State != PilotState.Off) _app.TurnOff();
                if (_app.Updates.RestartToUpdate()) Application.Current.Shutdown();
            }),
            UpdateStatus.Idle or UpdateStatus.UpToDate or UpdateStatus.Failed => Plain("Check now", () => _ = _app.Updates.CheckAsync(userRequested: true)),
            _ => null,
        };
        var updatesText = Stack(Text("Updates", style: "H3"),
            Text(updateText, color: u.Status == UpdateStatus.Ready ? "Ok" : "Text2", wrap: true,
                weight: u.Status == UpdateStatus.Ready ? FontWeights.SemiBold : FontWeights.Normal));
        page.Children.Add(Card(updateButton is null ? updatesText : Row(updatesText, updateButton)));

        page.Children.Add(Card(Row(
            Stack(Text("Start with Windows", style: "H3"), Muted("Paper Pilot waits quietly in the tray, ready for your hotkey.")),
            Switch(s.StartWithWindows, on => { s.StartWithWindows = on; s.Save(); StartWithWindows.Apply(on); }))));

        page.Children.Add(Card(Row(
            Stack(Text("Log files", style: "H3"), Muted("Kept on this computer for 7 days. Helpful if something goes wrong.")),
            Plain("Open log folder", () =>
            {
                Directory.CreateDirectory(Log.LogFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.LogFolder}\"") { UseShellExecute = true });
            }))));

        if (s.DeveloperOptions)
        {
            page.Children.Add(Text("Developer options", style: "H2"));
            var dev = new StackPanel();
            dev.Children.Add(Row(
                Stack(Text("Debug window", style: "H3"), Muted("What Paper Pilot reads, scroll tests and calibration.")),
                Plain("Open", _openDebug)));
            dev.Children.Add(Divider());
            dev.Children.Add(Row(
                Text("Open the debug window at start", style: "H3"),
                Switch(s.ShowDebugWindow, on => { s.ShowDebugWindow = on; s.Save(); })));
            dev.Children.Add(Divider());
            dev.Children.Add(Row(
                Muted("Hide developer options again."),
                LinkButton("Hide", () => { s.DeveloperOptions = false; s.ShowDebugWindow = false; s.Save(); _versionClicks = 0; Render(); })));
            page.Children.Add(Card(dev));
        }
        return page;
    }

    // ===== Hotkey recorder ========================================================================

    private void StartRecording(HotkeyAction action)
    {
        if (_recording is null) _app.Hotkeys.Suspend(); // so our own hotkeys don't swallow the keypress
        _recording = action;
        _hotkeyMessages.Remove(action);
        Render();
        Keyboard.Focus(this);
    }

    private void StopRecording()
    {
        if (_recording is null) return;
        _recording = null;
        _app.Hotkeys.Resume();
        _app.ApplyHotkeys(); // re-renders via Changed
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } action) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed || HotkeyBinding.IsModifierKey(key)) return;

        var binding = HotkeyBinding.FromWpf(Keyboard.Modifiers, key);
        var clash = _app.Settings.Hotkeys.FirstOrDefault(kv => kv.Key != action && kv.Value == binding);
        if (clash.Value is not null)
        {
            _hotkeyMessages[action] = $"{binding} is already used for “{HotkeyRows.First(r => r.action == clash.Key).title}”.";
            Render();
            return;
        }
        if (!_app.Hotkeys.CanRegister(binding, out var error))
        {
            _hotkeyMessages[action] = error ?? $"{binding} can't be used. Pick another.";
            Render();
            return;
        }
        _app.Settings.Hotkeys[action] = binding;
        _app.Settings.Save();
        StopRecording();
    }

    private void SetHotkey(HotkeyAction action, HotkeyBinding? binding)
    {
        StopRecording();
        _hotkeyMessages.Remove(action);
        _app.Settings.Hotkeys[action] = binding;
        _app.Settings.Save();
        _app.ApplyHotkeys();
    }

    // ===== Building blocks ========================================================================

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private static StackPanel Header(string title, string lead)
    {
        var p = new StackPanel();
        p.Children.Add(Text(title, style: "H1"));
        p.Children.Add(Text(lead, style: "Lead"));
        return p;
    }

    private static TextBlock Text(string text, string? style = null, double? size = null, FontWeight? weight = null,
        string? color = null, bool wrap = false, double top = 0)
    {
        var t = new TextBlock { Text = text };
        if (style is not null) t.Style = Res<Style>(style);
        if (size is { } sz) t.FontSize = sz;
        if (weight is { } w) t.FontWeight = w;
        if (color is not null) t.SetResourceReference(TextBlock.ForegroundProperty, color);
        else if (style is null) t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        if (top > 0) t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    private static TextBlock Muted(string text, double top = 0) => Text(text, style: "Muted", top: top);

    private static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private static FrameworkElement Spaced(FrameworkElement e, double top)
    {
        e.Margin = new Thickness(e.Margin.Left, top, e.Margin.Right, e.Margin.Bottom);
        return e;
    }

    private static Border Card(UIElement child) => new() { Style = Res<Style>("Card"), Child = child };

    private static Border Divider() => new() { Style = Res<Style>("Divider") };

    /// <summary>Text on the left, a control on the right.</summary>
    private static Grid Row(UIElement left, UIElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        if (right is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(16, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
        }
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private Border SummaryCard(PageId target, string title, string summary)
    {
        var card = new Border
        {
            Style = Res<Style>("ClickCard"),
            Margin = new Thickness(6, 0, 6, 12),
            Child = Stack(Text(title, style: "H3"), Muted(summary)),
        };
        card.MouseLeftButtonUp += (_, _) => ShowPage(target);
        return card;
    }

    private static CheckBox Switch(bool on, Action<bool> changed)
    {
        var c = new CheckBox { Style = Res<Style>("Switch"), IsChecked = on };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    private static Border Segmented<T>(T current, (T value, string label)[] options, Action<T> changed)
    {
        var bar = new Border { Style = Res<Style>("SegmentedBar") };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        string group = Guid.NewGuid().ToString();
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton
            {
                Content = label,
                Style = Res<Style>("Segment"),
                GroupName = group,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current),
            };
            rb.Checked += (_, _) => changed(value);
            panel.Children.Add(rb);
        }
        bar.Child = panel;
        return bar;
    }

    private Slider Slider(double min, double max, double value, double step, Action<double> changed)
    {
        var sl = new Slider
        {
            Style = Res<Style>("BrandSlider"),
            Minimum = min, Maximum = max, Value = value,
            SmallChange = step, LargeChange = step * 2,
            TickFrequency = step, IsSnapToTickEnabled = true,
            MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left, Width = 380,
        };
        sl.ValueChanged += (_, e) => changed(e.NewValue);
        // Save once when the user lets go, not on every pixel.
        sl.PreviewMouseLeftButtonUp += (_, _) => _app.Settings.Save();
        sl.LostKeyboardFocus += (_, _) => _app.Settings.Save();
        return sl;
    }

    private static Button LinkButton(string text, Action click)
    {
        var b = new Button { Style = Res<Style>("LinkButton"), Content = text };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Primary(string text, Action click)
    {
        var b = new Button { Style = Res<Style>("PrimaryButton"), Content = text };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Plain(string text, Action click)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => click();
        return b;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
