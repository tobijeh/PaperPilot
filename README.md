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
| 2 — Scroll (scroller, calibration, manual override, pill, hotkeys) | ✅ done |
| 2.5 — Wirl-style UX (tray, bubble with plane + voice trail, clean settings, background updates) | ✅ ready to test |
| 3 — Listen | not started |
| 4 — Match | not started |
| 5 — Polish | not started |

## Installing

Download **PaperPilot-Setup.exe** from the
[latest release](https://github.com/tobijeh/PaperPilot/releases/latest) and run it. It installs for
your Windows user only (no administrator needed) into `%LOCALAPPDATA%\Programs\Paper Pilot`, adds
Paper Pilot to the Start menu, and lists it in **Settings › Apps › Installed apps**, where you can
uninstall it. Windows may say *"Windows protected your PC"* the first time, because Paper Pilot isn't
code-signed: click **More info**, then **Run anyway**.

**Updates:** Paper Pilot checks on launch and once a day (never while it's on) and downloads the next
version in the background. When it's ready, choose **Restart to update** (Home page, About page or
the tray menu); it closes, installs and opens again in a few seconds. If you just quit, it installs
quietly instead. Your settings are kept.

Each release also has a plain `PaperPilot.exe` for running without installing.

**Build from source** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```
dotnet run --project src/PaperPilot -c Release
```

## Milestone 2.5 — how to test

Paper Pilot now lives in the **tray** (the blue plane by the clock). Left-click it for Settings,
right-click for the menu.

1. **Settings** opens the first time. Check Home, Hotkeys, Scrolling, Bubble and About in light and
   dark mode (Windows Settings › Personalization › Colors).
2. Click into your sermon notes and press **F3**. The **bubble** appears at the top right of that
   screen with the word *Listening*, then folds down to the plane. Press F3 again: it disappears.
3. **Bubble page**: the preview cycles through the states. Try Size, Position, the words switch
   and idle brightness, and drag the real bubble somewhere else (your notes must keep focus).
4. **Tray menu**: untick *Paper Pilot is on*: the icon turns grey and F3 goes back to Chrome.
5. **Updates**: the next release after this one should appear as *Restart to update* in the tray
   and About. Use it (or quit Paper Pilot) and check the new version starts.
6. The Milestone 2 scroll tests now live in the **debug window**: Settings › About, click the
   version 7 times, then *Open*. Tick *Simulate voice* there to see the trail move behind the plane.

Logs and snapshots live in `%LOCALAPPDATA%\PaperPilot\logs` (kept 7 days, never uploaded).

## Layout

```
src/PaperPilot/
  Core/         TargetWindow, WindowCapture (Windows.Graphics.Capture), ScreenReader (Windows OCR),
                Scroller, InputMonitor (low-level hooks), TouchpadMonitor (raw HID), HotkeyManager,
                AppSettings, AppController, Updater
  UI/           Bubble (plane + voice trail), tray icon, Settings window, theme and styles
  Interop/      Win32 / WinRT P/Invoke
  Diagnostics/  Log, DebugWindow, on-screen box overlay
src/PaperPilot.Core/   platform-independent logic (models, normalization, scroll planning/measuring)
tests/                 unit tests for PaperPilot.Core (`dotnet test`)
assets/                app icon, Manrope font (OFL, same as Wirl)
```
