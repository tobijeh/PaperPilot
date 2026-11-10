# Paper Pilot — notes for Claude

- Spec: `docs/PaperPilot_Build_Spec.md`. Build milestone by milestone; stop after each for the user to test.
- **Git:** the owner wants every change pushed straight to `main` (no PRs). Commit, then `git push origin HEAD:main`.
  Each push to `main` makes GitHub Actions publish a release (`v<version.txt>.<run number>`) that the app's
  built-in updater installs. Bump `version.txt` (e.g. 0.1 → 0.2) when a milestone ships.
- Builds on Linux too: `dotnet build -c Release` (EnableWindowsTargeting is on), but it can only *run* on Windows.
- Network: the only network access allowed is the update check/download (GitHub Releases), at startup or on
  request — never while Paper Pilot is listening/scrolling.
