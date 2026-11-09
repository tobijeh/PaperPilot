# Paper Pilot — Build Spec

You are helping me build **Paper Pilot**, a small Windows desktop tool. Read this whole spec before writing code. Build it in the milestones at the end, one at a time, and stop after each milestone so I can test it on my laptop before moving on.

---

## 1. What it is

I'm a pastor. I preach from sermon notes open full-screen (zoomed in) in Google Docs on a Windows laptop. I glance down at the notes occasionally and look up to engage the audience most of the time.

Paper Pilot sits on top of whatever window I'm already reading from (Google Docs in Chrome/Edge, a PDF reader, Word, anything), **listens to my voice offline**, works out where I am in the visible text, and **scrolls that window for me** so my current line stays in a consistent spot. It is an autopilot: it flies the page when it can, and the moment I touch the controls, I'm the pilot.

There is **no separate reader**. Paper Pilot never displays my script. It reads the screen and scrolls the existing window.

## 2. Non-negotiable principles

1. **Manual always wins.** Any scroll, key, or clicker input from me instantly stops automatic scrolling, even mid-animation.
2. **Auto resumes from wherever the screen is.** After I scroll manually, Paper Pilot forgets its previous position, treats what's currently on screen as the starting point, and picks up when it hears me speak text that's visible.
3. **Failure means stopping, never drifting.** When unsure, do nothing. The worst case must equal scrolling manually, which is what I do today.
4. **Never jump away from where I put the page.** Only match against text that's on screen. No whole-document searching.
5. **Auto never scrolls up.** Only forward (down). Going back is always manual.
6. **Never steal focus** from the window I'm preaching from.
7. **Fully offline.** No network calls of any kind, ever.
8. **Quiet.** The only visible UI during use is a tiny status pill with no text.

## 3. Platform and stack

- Windows 10 (19041+) and Windows 11.
- **C# / .NET 8**, **WPF**. Target framework `net8.0-windows10.0.19041.0` so WinRT APIs are available.
- **Screen capture:** `Windows.Graphics.Capture` (GraphicsCaptureItem from HWND). Do not use GDI BitBlt; it returns black frames for GPU-accelerated browsers.
- **OCR:** `Windows.Media.Ocr` (built in, offline). Use word bounding boxes and line grouping from the result.
- **Speech:** Vosk via the `Vosk` NuGet package, microphone via `NAudio`. Bundle `vosk-model-small-en-us-0.15` by default; allow switching to a larger English model in settings by pointing at a model folder.
- **Win32 via P/Invoke:** `RegisterHotKey`, `SetWindowsHookEx` (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`), `SendInput`, `PostMessage`, window style flags, DPI APIs.
- Per-monitor DPI aware (v2). All coordinates must be correct at 100–200% scaling and on multiple monitors.
- Settings stored as JSON in `%LOCALAPPDATA%\PaperPilot\settings.json`. Logs in `%LOCALAPPDATA%\PaperPilot\logs\`.

## 4. Architecture

Keep these as separate, testable components:

- **TargetWindow** — tracks the window I'm preaching from. On activation, lock to the current foreground window. Exposes HWND, client rect in screen coordinates, minimized/closed status.
- **ScreenReader** — captures the target window and runs OCR. Returns a list of lines: normalized words, each with its on-screen rectangle. Runs about every 700 ms while listening, and again right after any scroll settles.
- **Listener** — microphone → Vosk. Emits partial and final results as a rolling buffer of recent words with timestamps.
- **Matcher** — given recent spoken words and the current OCR lines, finds which visible line I'm on, with a confidence score.
- **Scroller** — moves the target window by a given number of pixels, smoothly, and can be cancelled instantly.
- **InputMonitor** — low-level hooks that detect my manual input and distinguish it from Paper Pilot's own injected input.
- **StateMachine** — owns the current state (Section 7) and all transitions, including debounce.
- **Pill** — the floating status indicator.
- **Hotkeys** — global hotkey registration and the hotkey recorder.
- **Settings window** and **tray icon**.
- **Logger** — local rolling log for troubleshooting.

## 5. Core loop

### 5.1 Text normalization (used for both OCR and speech)
- Lowercase, strip punctuation, collapse whitespace.
- Normalize numbers both ways so "3:16" on screen matches "three sixteen" spoken, and "1st" matches "first". Scripture references are common in sermons; handle "John 3:16" style references.
- Treat common contractions consistently ("don't" ↔ "do not").

### 5.2 Matching
- Keep a rolling buffer of the last ~8 recognized words (use Vosk partial results for low latency, corrected by final results).
- Flatten the visible OCR lines into a word sequence with line indices.
- Slide a window over the visible word sequence and score how well the last 6–8 spoken words align with it, in order, allowing small gaps and skipped/extra words. Word-level similarity uses a normalized edit-distance ratio (count ≥ 0.8 as a match).
- A match is **confident** when at least 4 of the last 6 spoken words align in order, and the best candidate beats the second-best by a clear margin.
- When multiple confident candidates exist (repeated phrases, refrains), prefer the nearest one **at or below** the current position.
- **Locking on from Holding** requires two consecutive confident matches that agree on position. Once locked (Following), single confident matches are enough to advance.
- No confident match for ~3 seconds → Holding. Off-script talking is normal; it is not an error.

### 5.3 Scroll policy
- **Anchor line:** a horizontal line at a configurable fraction of the target window's client height (default 33% from the top).
- When the matched line's top is more than about one line-height below the anchor, scroll down by the pixel difference so that line sits at the anchor.
- Scroll in small increments eased over ~300–500 ms so it glides rather than jumps. Prefer moving in steps as I finish lines/sentences rather than continuous motion.
- Never scroll up automatically.
- After each scroll settles, re-run OCR to confirm the new positions.

### 5.4 Scrolling another window without stealing focus
This is the riskiest technical part; validate it early (Milestone 2).
- **Primary method:** `PostMessage` `WM_MOUSEWHEEL` to the target window (for Chromium browsers, the `Chrome_RenderWidgetHostHWND` child), with the point set to the center of the document area. Use small wheel deltas (e.g., 20–40 units) for smooth movement.
- **Fallback:** `SendInput` mouse-wheel events, which go to the window under the cursor. If the fallback requires moving the cursor, move it to the document area and restore it immediately; never leave my cursor moved.
- **Calibration:** pixels-per-wheel-delta varies by app and zoom. On activation, send a small known delta, re-OCR, measure how far the lines moved, and store pixels-per-delta for that window. Recalibrate if line heights change (zoom changed).
- Use whichever method works for the target; log which one is in use.

### 5.5 Manual override detection
- Install `WH_MOUSE_LL` and `WH_KEYBOARD_LL` hooks. Ignore events flagged as injected (`LLMHF_INJECTED` / `LLKHF_INJECTED`) so Paper Pilot's own input never counts as mine.
- Treat as manual: mouse wheel, touchpad scrolling, arrow keys, Page Up/Down, Space, Home/End, any left-button press inside the target window (scrollbar drags, clicks), typing in the target window, and any key a clicker sends.
- Also treat as manual: OCR shows the content moved and Paper Pilot didn't cause it.
- On manual input: cancel any scroll in progress immediately, go to Holding, discard the matcher's previous position. After ~1 second without manual input, re-OCR and start matching from what's on screen.

## 6. Hotkeys

- All hotkeys are **customizable** in settings, using a recorder ("click the field, press the keys"). Supports single keys (e.g., F3), combinations, and whatever keys a presentation clicker sends.
- Actions:
  - **Toggle Paper Pilot** (default **F3**): shows the pill and starts listening; pressing again hides the pill and fully releases the microphone. Hidden = fully off.
  - **Pause / resume listening** (default unassigned): keeps the pill visible, stops listening and auto-scrolling.
  - **Nudge down / nudge up** (default unassigned): small manual scroll of the target window, counted as manual input.
- If `RegisterHotKey` fails because another app owns the combination, show a clear message in settings ("That hotkey is in use, pick another") instead of failing silently.
- "Reset to defaults" button.
- Hotkeys must work while the target window has focus and must not change focus.

## 7. States and the pill

### 7.1 States
| State | Meaning | Pill appearance |
|---|---|---|
| **Off** | Tool toggled off | Pill hidden, mic released |
| **Following** | Locked on, will scroll | Solid Paper Pilot Blue `#0059FD` |
| **Holding** | Listening, won't move (stories, prayer, after manual scroll, searching for place, just activated) | Soft periwinkle `#ABBFEA` fill |
| **Paused** | Listening paused by hotkey | Hollow: Navy `#0D2768` outline, transparent fill |
| **Problem** | Something is broken; scroll manually | Neutral grey `#8A8F98` fill with a **dashed** Navy `#0D2768` outline, one slow pulse on entering, then steady. The dashed outline is what distinguishes it from Holding. |

- Activation always starts in **Holding**, using the current screen as the starting point.
- **Problem** triggers: microphone missing or erroring; speech model failed to load; target window closed or minimized; OCR returns no usable text for ~5 seconds; scrolling has no effect after calibration.
- Recover from Problem automatically when the cause clears (e.g., mic reconnected).
- **Debounce:** a state must persist ~1 second before the pill changes (except Paused and Off, which are immediate because I triggered them). No flicker on pauses for breath.

### 7.2 Pill design
- Tiny rounded pill, roughly 36 × 14 px at 100% scaling, with a 1 px Navy `#0D2768` edge in every state so it reads over white pages and dark mode.
- **No text, numbers, icons, or transcript.** Shape and color only.
- Dim by default (~55% opacity). On a state change, brighten to 100% for ~1.5 s, then fade back.
- Calm animation only: soft cross-fades, no flashing, no red.
- Always on top. Window styles: `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. It must never take focus, including while being dragged.
- Draggable; position saved per monitor. Default: top-right corner of the target window's monitor, inset from the edge.
- No buttons. Right-click is not required.

## 8. Settings window and tray

### 8.1 Brand palette (from the app icon)
- Paper Pilot Blue `#0059FD` — accents, toggles, buttons, selected states
- Cream `#FEF7E6` — light-mode background
- Navy `#0D2768` — text, outlines, dark-mode background
- Deep Blue `#0142C0` — hover/pressed accents
- Periwinkle `#ABBFEA` — subtle borders, secondary surfaces
- Neutral Grey `#8A8F98` — Problem state only (with dashed outline)

Use blue as an accent, not as large filled panels. Follow the Windows light/dark setting: light = cream background, navy text; dark = navy background, cream text; blue accents in both.

### 8.2 Settings contents
- **Hotkeys** (recorder fields for each action, conflict warnings, reset)
- **Microphone** (device picker, simple input level meter)
- **Anchor position** (slider, 20–50% from top, default 33%)
- **Pill** (size small/medium, idle opacity)
- **Speech model** (bundled default, or choose a model folder)
- **Start with Windows** (off by default)
- **Open log folder** button

### 8.3 Tray icon
- Simplified icon: plane silhouette (wing and body only) in cream on Paper Pilot Blue, legible at 16 px. The full icon (`assets/icon.png`) is used only for the app icon and large sizes.
- Tray menu: Settings, Open log folder, Quit.

## 9. Logging

- Rolling daily log files, kept 7 days, local only.
- Log: state changes, recognized speech (partial/final), a short summary of OCR lines on each read, match candidates and scores, the decision taken (scroll N px / hold), manual-input events, scroll method used, calibration results, and errors.
- No UI for logs other than "Open log folder."

## 10. Performance targets

- Speech → scroll decision under ~1.5 s.
- Light CPU use on a typical laptop; OCR only while listening, and only at the stated interval.
- No noticeable lag in my own manual scrolling while Paper Pilot is running.

## 11. Out of scope (do not build)

- Any reader or script display window
- Google Docs sync or any cloud features
- Practice/rehearsal mode or on-screen transcript
- macOS support (for now)
- Buttons on the pill

## 12. Milestones

Build one at a time. After each, stop, tell me how to run it, and list what to test.

**Milestone 1 — See.** Lock onto the foreground window via a temporary hotkey, capture it with Windows.Graphics.Capture, run OCR, and show a debug window listing detected lines with their screen rectangles. *Test:* Google Docs in Chrome at my normal zoom reads accurately; coordinates are correct on my display scaling.

**Milestone 2 — Scroll.** Scroller with both methods (PostMessage and SendInput fallback), calibration, smooth eased scrolling, InputMonitor with injected-input filtering, the pill (all five states, triggered manually from the debug window for now), and customizable global hotkeys with the recorder. *Test:* Paper Pilot can scroll Google Docs by an exact number of pixels without focus changing; my own wheel/keys/clicker instantly cancel it; the pill never takes focus.

**Milestone 3 — Listen.** Microphone selection, Vosk listener, and a live transcript in the debug window. *Test:* recognition accuracy with my mic in the actual room.

**Milestone 4 — Match.** Normalization, matcher, scroll policy, the full state machine with debounce, and manual-override behavior from Section 5.5. *Test:* reading a sermon aloud keeps my line at the anchor; stories and ad-libs leave the page still (Holding); manual scrolling takes over instantly and auto picks up from the new screen position.

**Milestone 5 — Polish.** Settings window in the brand palette, tray icon and menu, Start with Windows, logging, Problem-state detection and recovery, and an installer (MSIX or a simple setup exe). Remove the debug window from normal use (keep it behind a hidden setting).

Test targets throughout: Google Docs in Chrome and Edge at 150–200% zoom, a PDF in Edge, and a Word document.
