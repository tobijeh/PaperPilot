using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperPilot.Core;

namespace PaperPilot.UI;

/// <summary>Settings (Milestone 2: hotkeys only; the rest arrives in Milestone 5).</summary>
public partial class SettingsWindow : Window
{
    private static readonly (HotkeyAction action, string label)[] Rows =
    {
        (HotkeyAction.Toggle, "Turn Paper Pilot on/off"),
        (HotkeyAction.PauseResume, "Pause / resume"),
        (HotkeyAction.NudgeDown, "Nudge down"),
        (HotkeyAction.NudgeUp, "Nudge up"),
    };

    private readonly AppController _app;
    private readonly Dictionary<HotkeyAction, (Border box, TextBlock text, TextBlock error)> _fields = new();
    private HotkeyAction? _recording;

    public SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        BuildRows();
        Refresh();
        ShowRegistrationErrors();
        Closed += (_, _) => StopRecording();
    }

    private void BuildRows()
    {
        int row = 0;
        foreach (var (action, label) in Rows)
        {
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(caption, row);

            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            var box = new Border
            {
                Child = text,
                Height = 32,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("Periwinkle"),
                Background = Brushes.White,
                Focusable = true,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 4, 8, 0),
            };
            box.MouseLeftButtonDown += (_, e) => { StartRecording(action); e.Handled = true; };
            box.PreviewKeyDown += (_, e) => OnRecorderKey(action, e);
            box.LostKeyboardFocus += (_, _) => { if (_recording == action) StopRecording(); };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);

            var clear = new Button
            {
                Content = "Clear",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 4, 0, 0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("Navy"),
                BorderBrush = (Brush)FindResource("Periwinkle"),
                ToolTip = "Remove this hotkey",
            };
            clear.Click += (_, _) => SetBinding(action, null);
            Grid.SetRow(clear, row);
            Grid.SetColumn(clear, 2);

            var error = new TextBlock
            {
                Foreground = (Brush)FindResource("DeepBlue"),
                FontStyle = FontStyles.Italic,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 4),
            };
            Grid.SetRow(error, row + 1);
            Grid.SetColumn(error, 1);
            Grid.SetColumnSpan(error, 2);

            HotkeyGrid.Children.Add(caption);
            HotkeyGrid.Children.Add(box);
            HotkeyGrid.Children.Add(clear);
            HotkeyGrid.Children.Add(error);
            _fields[action] = (box, text, error);
            row += 2;
        }
    }

    private void Refresh()
    {
        foreach (var (action, (box, text, _)) in _fields)
        {
            bool recording = _recording == action;
            var binding = _app.Settings.Hotkeys.GetValueOrDefault(action);
            text.Text = recording ? "Press a key…" : binding?.ToString() ?? "Not set";
            text.Opacity = recording || binding is not null ? 1 : 0.55;
            box.BorderBrush = (Brush)FindResource(recording ? "PilotBlue" : "Periwinkle");
            box.BorderThickness = new Thickness(recording ? 2 : 1);
        }
    }

    private void ShowError(HotkeyAction action, string? message)
    {
        var error = _fields[action].error;
        error.Text = message ?? "";
        error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowRegistrationErrors()
    {
        foreach (var action in _fields.Keys)
            ShowError(action, _app.Hotkeys.Errors.GetValueOrDefault(action));
    }

    // ---- Recorder ------------------------------------------------------------------------------

    private void StartRecording(HotkeyAction action)
    {
        if (_recording is null) _app.Hotkeys.Suspend(); // so our own hotkeys don't swallow the keypress
        _recording = action;
        ShowError(action, null);
        _fields[action].box.Focus();
        Keyboard.Focus(_fields[action].box);
        Refresh();
    }

    private void StopRecording()
    {
        if (_recording is null) return;
        _recording = null;
        _app.ApplyHotkeys(); // also resumes
        _app.Hotkeys.Resume();
        Refresh();
        ShowRegistrationErrors();
    }

    private void OnRecorderKey(HotkeyAction action, KeyEventArgs e)
    {
        if (_recording != action) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed || key == Key.DeadCharProcessed) return;
        if (HotkeyBinding.IsModifierKey(key))
        {
            _fields[action].text.Text = ModifierPreview(Keyboard.Modifiers) + "…";
            return;
        }

        var binding = HotkeyBinding.FromWpf(Keyboard.Modifiers, key);

        var clash = _app.Settings.Hotkeys.FirstOrDefault(kv => kv.Key != action && kv.Value == binding);
        if (clash.Value is not null)
        {
            ShowError(action, $"{binding} is already used for “{Rows.First(r => r.action == clash.Key).label}”.");
            return;
        }
        if (!_app.Hotkeys.CanRegister(binding, out var error))
        {
            ShowError(action, error);
            return;
        }

        _app.Settings.Hotkeys[action] = binding;
        _app.Settings.Save();
        Keyboard.ClearFocus();
        StopRecording();
    }

    private static string ModifierPreview(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join(" + ", parts) + (parts.Count > 0 ? " + " : "");
    }

    private void SetBinding(HotkeyAction action, HotkeyBinding? binding)
    {
        StopRecording();
        _app.Settings.Hotkeys[action] = binding;
        _app.Settings.Save();
        _app.ApplyHotkeys();
        Refresh();
        ShowRegistrationErrors();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        _app.Settings.Hotkeys = AppSettings.DefaultHotkeys();
        _app.Settings.Save();
        _app.ApplyHotkeys();
        Refresh();
        ShowRegistrationErrors();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
