using System.Windows.Threading;
using PaperPilot.Diagnostics;
using PaperPilot.UI;

namespace PaperPilot.Core;

/// <summary>
/// Wires the components together and owns the current state. (Milestone 4 replaces the manual
/// state switching with the full state machine and debounce.)
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _ui;
    private TargetWindow? _lastTarget;

    public AppController()
    {
        _ui = Dispatcher.CurrentDispatcher;
        Settings = AppSettings.Load();

        Hotkeys = new HotkeyManager();
        Hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();

        Input = new InputMonitor();
        Input.ManualInput += OnManualInput;
        Touchpad = new TouchpadMonitor(Input);
        UpdateIgnoredKeys();

        Scroller = new Scroller();
        Pill = new PillWindow(Settings);

        try
        {
            Reader = new ScreenReader();
        }
        catch (Exception ex)
        {
            ReaderError = ex.Message;
            Log.Error("ScreenReader failed to start", ex);
        }
    }

    public AppSettings Settings { get; }
    public HotkeyManager Hotkeys { get; }
    public InputMonitor Input { get; }
    public TouchpadMonitor Touchpad { get; }
    public Scroller Scroller { get; }
    public PillWindow Pill { get; }
    public ScreenReader? Reader { get; }
    public string? ReaderError { get; }

    public PilotState State { get; private set; } = PilotState.Off;
    public TargetWindow? Target { get; private set; }

    public event Action<PilotState>? StateChanged;
    public event Action<TargetWindow?>? TargetChanged;
    /// <summary>Raised on the UI thread after manual input was detected.</summary>
    public event Action<ManualInputKind, string>? ManualInputDetected;
    /// <summary>Messages worth showing the user (e.g. "can't lock onto Paper Pilot itself").</summary>
    public event Action<string>? Notice;

    // ---- Hotkeys -------------------------------------------------------------------------------

    public void ApplyHotkeys()
    {
        Hotkeys.Apply(Settings.Hotkeys);
        UpdateIgnoredKeys();
    }

    private void UpdateIgnoredKeys() =>
        Input?.SetIgnoredKeys(Hotkeys.Active.Select(a => a.binding.VirtualKey));

    private void OnHotkey(HotkeyAction action)
    {
        Log.Info($"Hotkey: {action}.");
        switch (action)
        {
            case HotkeyAction.Toggle: Toggle(); break;
            case HotkeyAction.PauseResume: PauseResume(); break;
            case HotkeyAction.NudgeDown: _ = NudgeAsync(down: true); break;
            case HotkeyAction.NudgeUp: _ = NudgeAsync(down: false); break;
        }
    }

    // ---- Actions -------------------------------------------------------------------------------

    /// <summary>On: lock onto the foreground window, show the pill (Holding). Off: hide everything.</summary>
    public void Toggle()
    {
        if (State != PilotState.Off)
        {
            TurnOff();
            return;
        }

        var target = TargetWindow.FromForeground(out var reason);
        if (target is null && _lastTarget is { IsAlive: true })
        {
            target = _lastTarget; // e.g. toggled from the debug window: reuse the last document window
            Log.Info($"Toggle: foreground not usable ({reason}); reusing {target}.");
        }
        if (target is null)
        {
            Log.Info($"Toggle ignored: {reason}");
            Notice?.Invoke(reason ?? "No window to lock onto.");
            return;
        }

        Target = _lastTarget = target;
        Scroller.SetTarget(target);
        Input.TargetWindow = target.Hwnd;
        try
        {
            Reader?.Attach(target);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not capture {target}", ex);
            Notice?.Invoke($"Could not read that window: {ex.Message}");
        }
        Log.Info($"Activated on {target}.");
        TargetChanged?.Invoke(target);
        SetState(PilotState.Holding);
    }

    public void TurnOff()
    {
        Scroller.CancelNow();
        Input.TargetWindow = IntPtr.Zero;
        Reader?.Detach();
        Target = null;
        TargetChanged?.Invoke(null);
        SetState(PilotState.Off);
        Log.Info("Turned off.");
    }

    public void PauseResume()
    {
        if (State == PilotState.Off) return;
        if (State == PilotState.Paused)
        {
            SetState(PilotState.Holding);
        }
        else
        {
            Scroller.CancelNow();
            SetState(PilotState.Paused);
        }
    }

    /// <summary>A small manual scroll on the user's behalf. Counts as manual input.</summary>
    public async Task NudgeAsync(bool down)
    {
        if (State == PilotState.Off || Target is null) return;
        Input.Report(ManualInputKind.Nudge, down ? "nudge down" : "nudge up");
        int delta = Settings.NudgeWheelDelta * (down ? -1 : 1);
        var method = Scroller.Method == ScrollMethod.None ? ScrollMethod.PostToDocument : Scroller.Method;
        await Scroller.ScrollDeltaAsync(delta, 220, method);
    }

    /// <summary>Debug/testing: force a pill state (the real state machine arrives in Milestone 4).</summary>
    public void SetStateManually(PilotState state)
    {
        if (state == PilotState.Off) { TurnOff(); return; }
        if (State == PilotState.Off)
        {
            Notice?.Invoke("Turn Paper Pilot on first (hotkey), then try the pill states.");
            return;
        }
        SetState(state);
    }

    private void SetState(PilotState state)
    {
        if (State == state) return;
        Log.Info($"State: {State} -> {state}.");
        State = state;
        Pill.SetState(state, Target?.Hwnd ?? IntPtr.Zero);
        StateChanged?.Invoke(state);
    }

    // ---- Manual override -----------------------------------------------------------------------

    /// <summary>Runs on the hook thread: stop scrolling immediately, then update state on the UI thread.</summary>
    private void OnManualInput(ManualInputKind kind, string detail)
    {
        Scroller.CancelNow();
        _ui.BeginInvoke(() =>
        {
            if (State == PilotState.Following) SetState(PilotState.Holding);
            ManualInputDetected?.Invoke(kind, detail);
        });
    }

    public void Dispose()
    {
        Scroller.CancelNow();
        Pill.Close();
        Touchpad.Dispose();
        Input.Dispose();
        Hotkeys.Dispose();
        Reader?.Dispose();
    }
}
