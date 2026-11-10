# Paper Pilot

Auto-scroll for preaching and public speaking. Paper Pilot sits on top of the window you already
read from (Google Docs, a PDF, Word…), listens to your voice offline, works out where you are in the
visible text, and scrolls that window so your current line stays put. Touch the controls and you're
the pilot again.

See [`docs/PaperPilot_Build_Spec.md`](docs/PaperPilot_Build_Spec.md) for the full spec.

## Status

| Milestone | State |
|---|---|
| 1 — See (capture + OCR + debug window) | ✅ done |
| 2 — Scroll (scroller, calibration, manual override, pill, hotkeys) | ✅ ready to test |
| 3 — Listen | not started |
| 4 — Match | not started |
| 5 — Polish | not started |

## Running it

**Option A — download the exe.** Grab `PaperPilot.exe` from the newest
[release](https://github.com/tobijeh/PaperPilot/releases/latest) (no .NET install needed) and put it
in a folder you own, e.g. `Documents\PaperPilot`. Windows SmartScreen may warn because the exe isn't
signed yet: *More info → Run anyway*.

**Updates:** every push to `main` publishes a new release. Paper Pilot checks once at startup (and
via *Check for updates*); when a newer version exists a banner appears — click *Update now* and it
downloads, verifies, replaces itself and restarts.

**Option B — build from source** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```
dotnet run --project src/PaperPilot -c Release
```

## Milestone 2 — how to test

1. Start Paper Pilot (it updates itself to the newest version first if you click *Update now*).
2. Click into your Google Doc and press **F3**. The small pill appears near the top-right of that
   screen in the soft periwinkle *Holding* colour. Press **F3** again: the pill disappears (off).
   Turn it back on.
3. **Calibrate** (leave *Start after 3 s* ticked and click into your document during the countdown).
   The page moves one wheel notch down and back; the debug window shows pixels per notch.
4. **Scroll down 200 px**: click into the document during the countdown. The status line should say
   it moved about 200 px and *focus unchanged ✓*. Try 50, 400 and 800 px too.
5. **Manual override**: tick *Slow (5 s)*, scroll down ~1000 px, and while it glides use your mouse
   wheel / touchpad / arrow keys / Page Down / clicker. It must stop instantly and the debug window
   shows *Manual input: …*. Repeat once per input type.
6. **Pill**: click *Following / Holding / Paused / Problem*. Check colours, the brief brighten-then-dim,
   and Problem's dashed outline with one slow pulse. Drag the pill somewhere else: your document must
   keep focus (keep typing in it). Turn off/on — the pill comes back where you left it.
7. **Hotkeys…**: change *Turn Paper Pilot on/off* to another key, assign *Nudge down/up* (try your
   clicker's buttons) and *Pause/resume*. Try a key another app already uses to see the warning.
   *Reset to defaults* restores F3. Nudges should scroll a notch and count as manual input.
8. Try Edge, a PDF in Edge and Word too. If something doesn't scroll or measure right, click
   *Save snapshot* and send me the snapshot files and today's log.

Logs and snapshots live in `%LOCALAPPDATA%\PaperPilot\logs` (kept 7 days, never uploaded).

## Layout

```
src/PaperPilot/
  Core/         TargetWindow, WindowCapture (Windows.Graphics.Capture), ScreenReader (Windows OCR),
                Scroller, InputMonitor (low-level hooks), TouchpadMonitor (raw HID), HotkeyManager,
                AppSettings, AppController, Updater
  UI/           Pill, Settings window
  Interop/      Win32 / WinRT P/Invoke
  Diagnostics/  Log, DebugWindow, on-screen box overlay
src/PaperPilot.Core/   platform-independent logic (models, normalization, scroll planning/measuring)
tests/                 unit tests for PaperPilot.Core (`dotnet test`)
assets/                app icon
```
