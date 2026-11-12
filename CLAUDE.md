# Paper Pilot — notes for Claude

- Spec: `docs/PaperPilot_Build_Spec.md`. Build milestone by milestone; stop after each for the user to test.
- **Git:** the owner wants every change pushed straight to `main` (no PRs). Commit, then `git push origin HEAD:main`.
  Each push to `main` makes GitHub Actions publish a release (`v<version.txt>.<run number>`) that the app's
  built-in updater installs. Bump `version.txt` (e.g. 0.1 → 0.2) when a milestone ships.
- Builds on Linux too: `dotnet build -c Release` (EnableWindowsTargeting is on), but it can only *run* on Windows.
- Network: the only network access allowed is the update check/download (GitHub Releases), at startup or on
  request — never while Paper Pilot is listening/scrolling.
- UX follows the owner's Wirl app (github.com/tobijeh/Wirl-Desktop): lives in the tray; the floating bubble is the
  only thing on screen while on; Settings has a sidebar and calm cards; plain, warm, brief words; every change
  applies immediately (no Save buttons). Brand palette only (blue #0059FD, navy #0D2768, cream #FEF7E6,
  periwinkle #ABBFEA, grey #8A8F98 for "not listening"), following Windows light/dark. Font: Manrope.
- The bubble: plane = indicator (blue Following, navy/cream Holding, grey when not listening), voice waveform
  trails behind it like a cloud trail, brief state words. The debug window sits behind Developer options
  (click the version in About 7 times).
- Post-MVP: a short welcome tour (Wirl has one).
