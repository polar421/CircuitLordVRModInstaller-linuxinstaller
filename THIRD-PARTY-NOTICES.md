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

## Liberation Serif

- Source: https://github.com/liberationfonts/liberation-fonts
- License: SIL Open Font License 1.1 (`src/CircuitLord.VRModInstaller.App/Assets/Fonts/LICENSE.txt`)
- Copyright (c) 2012 Red Hat, Inc., with Reserved Font Name Liberation

The four serif faces (regular, bold, italic, bold italic) are bundled unmodified as Avalonia
resources. The original Windows installer specifies Segoe UI Variable Display, which is proprietary
and cannot be redistributed; Liberation Serif is the licensed stand-in used here to match the serif
typography of the reference build.

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
