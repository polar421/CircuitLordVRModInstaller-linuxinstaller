# Build and packaging

## Build

```bash
dotnet restore CircuitLord.VRModInstaller.Linux.slnx
dotnet build CircuitLord.VRModInstaller.Linux.slnx -c Release
dotnet test tests/CircuitLord.VRModInstaller.Core.Tests -c Release
```

Verified locally: SDK 10.0.114, Avalonia 11.3.9, Debug build succeeds and the app runs on a
Wayland session (`DISPLAY=:0`, `WAYLAND_DISPLAY=wayland-1`).

## Publish

Framework-dependent:

```bash
dotnet publish src/CircuitLord.VRModInstaller.App -c Release -r linux-x64 --self-contained false \
  -o publish/linux-x64
```

Self-contained single directory (no .NET runtime required on the target):

```bash
dotnet publish src/CircuitLord.VRModInstaller.App -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -o publish/linux-x64
```

## Installer desktop entry (optional, for the installer app itself)

`packaging/circuitlord-vrmodinstaller.desktop` targets `publish/linux-x64/CircuitLordVRModInstaller`
and uses a distinct application id. Copy it to `~/.local/share/applications/` only after the binary
exists at that path. This entry is for the installer UI, not for the VR game; it deliberately does
not carry the WiVRn category.

## Not done yet

- AppImage / Flatpak / distribution packages.
- Icon installation into the hicolor theme.
- CI pipeline.

These are honest gaps: the release build is reproducible but not yet distributed in a packaged
format, and package-specific desktop/Steam permissions have not been evaluated.
