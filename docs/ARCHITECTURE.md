# Architecture

```
Presentation (Avalonia) → Application services → Domain models → Infrastructure adapters
```

## Projects

- `src/CircuitLord.VRModInstaller.App` — Avalonia 11 desktop UI (`MainWindow`, `InstallerViewModel`).
- `src/CircuitLord.VRModInstaller.Core` — services and models; no Avalonia dependency.
- `tests/CircuitLord.VRModInstaller.Core.Tests` — xUnit tests for parsers, models and transactions.

## Services

| Service | Responsibility |
|---|---|
| `XdgPaths` | XDG data/config/cache/state + applications directories with documented fallbacks |
| `ProcessRunner` | Async child processes with argument arrays, streamed output, timeout, SIGTERM, exit code |
| `BackendManager` | Clone into a `.partial` directory, origin verification, commit tracking, validated update, rollback |
| `InstallScriptAdapter` | The only place that builds backend commands; capability probe from the script text |
| `VersionCatalogueService` | Runs `--list-versions`, parses, caches |
| `CatalogueParser` | Exact token extraction, ordinal assignment, installed/choice markers |
| `ManifestService` | HTTPS retrieval, ETag revalidation, JSON validation, cache fallback |
| `DesktopEntryManager` | Canonical `tf2vr.desktop`, exact WiVRn category, atomic write, duplicate detection, repair |
| `BinaryVdf` | Valve binary KeyValues parser/serializer plus CRC32 |
| `SteamLocator` | Native/Flatpak/custom installations and `userdata` accounts |
| `SteamProcessController` | Observable client state, graceful `-shutdown`, relaunch, startup verification |
| `SteamShortcutManager` | Backup → parse → upsert → temp write → verify → atomic commit → relaunch obligation |
| `LauncherService` | Launches the installed `tf2vr`; classifies missing/non-executable/config cases |
| `RuntimeService` | Read-only OpenXR runtime and WiVRn/Monado/SteamVR process inspection |
| `SettingsStore` | Versioned settings, exact-token persistence, atomic writes |
| `OperationLogger` | Per-operation log files and installation history under the state directory |

## Operation ownership

`InstallerViewModel` serialises backend operations with a non-blocking semaphore. While an
operation is active, install/uninstall/backend-update actions are disabled and the reason is
reported. Steam transactions carry their own durable journal under
`$XDG_STATE_HOME/circuitlord-vrmodinstaller/steam-transactions/`.

## XDG layout

| Purpose | Path |
|---|---|
| Backend checkout | `${XDG_DATA_HOME:-~/.local/share}/circuitlord-vrmodinstaller/backend` |
| Settings | `${XDG_CONFIG_HOME:-~/.config}/circuitlord-vrmodinstaller/settings.json` |
| Manifest/catalogue cache | `${XDG_CACHE_HOME:-~/.cache}/circuitlord-vrmodinstaller` |
| Logs, history, Steam journals | `${XDG_STATE_HOME:-~/.local/state}/circuitlord-vrmodinstaller` |
| Titanfall 2 VR desktop entry | `${XDG_DATA_HOME:-~/.local/share}/applications/tf2vr.desktop` |

The launcher path is not configurable: the backend installs it at `~/.local/bin/tf2vr`.

## Data flow (install)

1. Ensure/validate backend checkout → 2. `--list-versions` → 3. user selects a token →
4. `--version <token>` with streamed output and a real exit code → 5. re-read backend state →
6. reconcile desktop entry → 7. optional Steam shortcut transaction.
