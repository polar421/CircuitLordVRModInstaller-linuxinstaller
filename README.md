# CircuitLord's VR Mod Installer — Linux

A native Linux desktop port of [CircuitLord's VR Mod Installer](https://github.com/CircuitLord/CircuitLordVRModInstaller),
built with .NET 10 and Avalonia so the installer UI runs without Wine.

Titanfall 2 VR installation is delegated to [Monkellie's `tf2vr-linux`](https://github.com/Monkellie/tf2vr-linux):
this application manages a checkout of that repository, runs its real `install.sh`, and never
reimplements Proton setup, downloads, checksums or the mod file layout.

## What works today

| Area | Status |
|---|---|
| Native Linux UI (Avalonia, no Wine) | Implemented |
| Managed `tf2vr-linux` checkout with origin verification and safe update | Implemented |
| Live catalogue from `install.sh --list-versions` | Implemented, exercised against the real backend |
| Stable = choice 1 → `latest`, Beta = choice 2 → `beta`, other entries → exact token | Implemented and unit tested |
| Asynchronous install / reinstall / uninstall through `install.sh` | Implemented (real backend run not performed on this machine) |
| Preflight (`install.sh --check`) with captured output | Implemented |
| Launch through the installed native `tf2vr` launcher | Implemented |
| WiVRn desktop entry with `Categories=Game;X-WiVRn-VR;` | Implemented, reconciled after install |
| Steam shortcut + graceful close / write / reopen transaction | Implemented and fixture tested; not tested against a live Steam client |
| Manifest release notes | Implemented (HTTPS + cache + validation) |
| Automated tests | 33 passing |
| Reproducible self-contained `linux-x64` release + user-level installer | Implemented (see `docs/RELEASE.md`) |

Not yet implemented: first-run wizard, dedicated diagnostics/integration pages, installation
history UI, settings UI, AppImage/Flatpak packages. See `docs/PROJECT_AUDIT.md` for the full
feature matrix and `docs/TEST_PLAN.md` for what has and has not been verified.

## Requirements

- Linux with a desktop session (Wayland or X11)
- .NET 10 SDK to build; the published binary needs only the .NET runtime (or publish self-contained)
- `git`, `bash`, `curl`, `python3` — the backend's own dependencies
- Steam / SteamVR / WiVRn / Monado only as needed for the integration you use

## Build, run, test

```bash
dotnet build CircuitLord.VRModInstaller.Linux.slnx
dotnet run --project src/CircuitLord.VRModInstaller.App
dotnet test tests/CircuitLord.VRModInstaller.Core.Tests

# self-contained release archive + installer script for the current user
packaging/build-release.sh
./artifacts/circuitlord-vrmodinstaller-1.0.0-linux-x64/install.sh
```

Packaging never bundles game files, mod packages or Proton builds; the app downloads and verifies
those through the backend at install time.

## Safety guarantees

- The release installer verifies every file against the archive's `SHA256SUMS` before installing and
  aborts on a mismatch, an unsafe path in the list, or a missing checksum file.
- The normal Titanfall 2 installation and its Steam entry are never modified.
- `shortcuts.vdf` is never written while the selected Steam client is running; it is backed up,
  written to a same-directory temporary file, re-parsed and atomically committed.
- If this application shuts Steam down, it attempts to reopen the same installation even when
  the shortcut write fails. Steam is never force-killed.
- The active OpenXR runtime is never changed. SteamVR is never forced.
- Only the backend's own uninstall operation removes game files; campaign saves are untouched.

## License

MIT for this installer. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Mod packages and game assets are not distributed with this application.
