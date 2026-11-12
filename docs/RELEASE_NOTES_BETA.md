# CircuitLord's VR Mod Installer — Linux (Beta 1)

First public beta of the native Linux port. The original installer UI is recreated in Avalonia and
runs without Wine; Titanfall 2 VR installation is delegated to `tf2vr-linux`.

## Download

- `circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz` — self-contained x86-64 build (no .NET runtime needed)
- `circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz.sha256` — checksum for the archive

```bash
sha256sum -c circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz.sha256
tar -xzf circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz
./circuitlord-vrmodinstaller-1.0.0-linux-x64/install.sh
```

The installer verifies every packaged file against `SHA256SUMS`, installs entirely in your user
account, and adds a menu entry and icons. Remove it later with `install.sh --uninstall`; the managed
backend checkout, settings, logs, the `tf2vr` launcher and the mod are kept.

## What is included

- Native game-selection UI with the original layout, serif typography, icon title bar, NEW badge
  and “Made for the love of VR by CircuitLord” support footer.
- Managed `tf2vr-linux` checkout with origin verification and safe updates.
- Live release catalogue, stable/beta selection, install and uninstall through the backend.
- Preflight checks, launching through the installed `tf2vr` launcher, and WiVRn desktop-entry repair.
- Steam shortcut management with graceful shutdown, backup, atomic write and reopen.

## Known limitations

- **Beta:** a full install has not been exercised end-to-end against a real Titanfall 2 install, and
  Steam integration has only been fixture-tested. Back up anything you care about.
- x86-64 glibc Linux only; no arm64, AppImage or Flatpak builds.
- Tested through XWayland; native Wayland behavior is unverified.
- Big Walk VR remains unavailable on Linux (no backend) and is shown disabled.
- No first-run wizard, settings screen, diagnostics page or installation history.

## Requirements

glibc-based x86-64 Linux with a desktop session, plus `git`, `bash`, `curl` and `python3` for the
backend. Steam, SteamVR, WiVRn or Monado are only needed for the integrations you use.

Game names and artwork belong to their respective owners. This project is not affiliated with or
endorsed by Respawn Entertainment or Electronic Arts.
