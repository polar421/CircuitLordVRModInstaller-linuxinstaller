# Third-party notices

## CircuitLord's VR Mod Installer (original Windows application)

- Source: https://github.com/CircuitLord/CircuitLordVRModInstaller
- License: MIT (installer source only; mod packages and release assets are excluded)
- Copyright (c) 2026 Jordan (CircuitLord)

This Linux port reuses the original application's visual identity, dark theme palette, window
structure and bundled artwork (`app.ico`, `Assets/titanfall-2.jpg`, `Assets/big-walk.jpg`) under
that license. Big Walk VR is shown as unavailable on Linux because no installation backend exists
for it; it is not presented as installable.

## tf2vr-linux

- Source: https://github.com/Monkellie/tf2vr-linux
- License: MIT
- Copyright (c) 2026 Monkellie

The application clones this repository at runtime into the user's XDG data directory and invokes
its `install.sh` / `tf2vr` launcher. It is not vendored or redistributed here.

## Avalonia

- Source: https://github.com/AvaloniaUI/Avalonia
- License: MIT

## .NET runtime

- Source: https://github.com/dotnet/runtime
- License: MIT

## Game and mod content

No Titanfall 2 game files, Big Walk files or CircuitLord mod packages are bundled or
redistributed. The Linux backend downloads official releases and verifies their SHA-256 checksums.
This application is not affiliated with or endorsed by Respawn Entertainment or Electronic Arts.
