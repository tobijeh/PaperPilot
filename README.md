# Paper Pilot

Auto-scroll for preaching and public speaking. Paper Pilot sits on top of the window you already
read from (Google Docs, a PDF, Word…), listens to your voice offline, works out where you are in the
visible text, and scrolls that window so your current line stays put. Touch the controls and you're
the pilot again.

See [`docs/PaperPilot_Build_Spec.md`](docs/PaperPilot_Build_Spec.md) for the full spec.

## Status

| Milestone | State |
|---|---|
| 1 — See (capture + OCR + debug window) | ✅ ready to test |
| 2 — Scroll | not started |
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

## Milestone 1 — how to test

1. Start Paper Pilot. The debug window opens.
2. Open your sermon notes in Google Docs (Chrome) at your normal zoom.
3. Click into the document, then press **F3**. (If another app already owns F3, the debug window
   tells you which fallback key it used.) Paper Pilot locks onto that window and reads it.
4. Check:
   - The line list matches the text on screen.
   - Tick **Show boxes on screen**: blue boxes should sit exactly on each line of text in the
     real window. The periwinkle outline should match the document area (no tabs/toolbar).
   - Tick **Auto-read every 700 ms** and scroll the document by hand: the list and boxes follow.
   - Repeat on each monitor / scaling you use (100–200%), maximized and not maximized.
5. If something looks wrong, click **Save snapshot**, then **Open log folder** and send me the
   newest `snapshot-*.png`, `snapshot-*.txt` and `paperpilot-*.log`.

Logs and snapshots live in `%LOCALAPPDATA%\PaperPilot\logs` (kept 7 days, never uploaded).

## Layout

```
src/PaperPilot/
  Core/         TargetWindow, WindowCapture (Windows.Graphics.Capture), ScreenReader (Windows OCR),
                GlobalHotkeys, TextNormalizer, data types
  Interop/      Win32 / WinRT P/Invoke
  Diagnostics/  Log, DebugWindow, on-screen box overlay
assets/         app icon
```
