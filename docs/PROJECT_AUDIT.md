# Project audit

Date: 2026-11-12
Scope: native Linux port of CircuitLord's VR Mod Installer, using `tf2vr-linux` as the
Titanfall 2 VR installation backend.

## 1. What was audited

| Source | Reference used |
|---|---|
| Original Windows application | `CircuitLord/CircuitLordVRModInstaller` @ `64cf26c` (WPF, .NET, XAML; `src/Installer`) |
| Linux backend | `Monkellie/tf2vr-linux` @ `8b02b87` |
| Official manifest | `manifest-v3.json` retrieved over HTTPS (schemaVersion 3) |

The workspace itself had been emptied in an earlier step of this project, so this port was
rebuilt from scratch against the two upstream repositories; the original XAML, theme and artwork
were inspected from the upstream source before writing any UI code.

## 2. Original application inventory (verified from source)

- Window: 720×640, no resize, custom chrome, dark palette `#14171C` / `#1C2027` / `#4FA3E3`.
- Game selection screen with Big Walk VR and Titanfall 2 VR cards, status line and installed chip.
- Installer view built from numbered steps (find game → EA sign-in → install), each a card with a
  badge, status text and action button.
- Version dropdown (`InstallAction` control) with Stable/Beta channels plus changelog hyperlink.
- Changelog overlay, confirmation overlay, launch overlay with generation progress.
- Tabs: game view, add-ons, logs; support footer, crash report, campaign-save folder.
- Services: `RepoClient`, `PackageInstaller`, `BepInExInstaller`, `SteamGameLocator`,
  `GameLocator`, `GameLauncher`, `OwnedFileStore`, `AppSettings`, `SelfUpdater`.

## 3. Backend command inventory (verified against `install.sh`)

| Command | Effect |
|---|---|
| `--check` | Preflight only; changes nothing |
| `--list-versions` | Prints the installable catalogue |
| `--version <token>` | Install/update to `latest`, `beta`, or a pinned token |
| `--force` | Reinstall even when up to date |
| `--refresh-proton` | Rebuilds Proton-TF2VR |
| `--uninstall` | Removes mod files, launcher, menu entry and Proton-TF2VR; keeps campaign saves |
| `--game DIR`, `--prefix DIR`, `--proton-base DIR` | Manual discovery overrides |

Side effects verified by reading the script: launcher at `~/.local/bin/tf2vr`; desktop entry
written to `${XDG_DATA_HOME:-~/.local/share}/applications/tf2vr.desktop` from `tf2vr.desktop`
with `Categories=Game;` (no WiVRn category); state at `${XDG_DATA_HOME}/tf2vr` (`selection`,
`version`); config at `${XDG_CONFIG_HOME}/tf2vr/paths.env`; cache at `${XDG_CACHE_HOME}/tf2vr`.

## 4. Feature mapping

| Original feature | Linux status |
|---|---|
| Dark original layout, title bar, game cards | Implemented in Avalonia (palette and structure ported) |
| Big Walk VR | Shown, explicitly disabled: no Linux backend exists |
| Titanfall 2 VR install/update | Implemented via `install.sh` |
| Version selection | Implemented dynamically from `--list-versions` |
| Changelog | Implemented from the official manifest |
| Launch in VR | Implemented through installed `tf2vr` |
| Uninstall | Implemented via `install.sh --uninstall` |
| Add-ons | Not supported by the Linux backend; documented, not shown as installable |
| EA sign-in step | Not reproduced as a UI step: the backend detects the EA prefix itself and reports it in `--check` |
| Crash report / campaign-save browser | Not implemented yet |
| Self-updater | Not implemented yet |
| WiVRn desktop category | Implemented as a new Linux integration with exact `Categories=Game;X-WiVRn-VR;` |
| Steam non-Steam shortcut + auto restart | Implemented as a new Linux integration |

## 5. Constraints found

- The backend owns installation layout and Proton setup; the app must not duplicate it.
- The backend template's `Categories=Game;` does **not** satisfy the WiVRn requirement, so the app
  must reconcile the canonical file after every backend run.
- `shortcuts.vdf` is binary VDF and must only be touched while Steam is stopped.
- The backend catalogue token is the row's name (`latest`, `beta`, `1.0.24`, `beta-1.0.22`…),
  not the printed row number; the app assigns its own ordinals.
- Version catalogue output depends on GitHub API availability; older pinned releases disappear
  when that call fails, and the app must show that honestly.
