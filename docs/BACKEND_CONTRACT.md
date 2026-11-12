# Backend contract (verified against tf2vr-linux @ 8b02b87)

Repository: `https://github.com/Monkellie/tf2vr-linux` (MIT).
All invocations go through `InstallScriptAdapter`, which passes `install.sh` and each option as
separate arguments to `bash`. Nothing is concatenated into a shell string.

## Commands

| Argument | Verified behaviour |
|---|---|
| `--check` | Preflight. Prints `ok/WARN/FAIL` lines. Changes nothing. |
| `--list-versions` | Prints catalogue and an install hint line. Exits 1 if the manifest or Titanfall entry cannot be read. |
| `--version latest` | Current release |
| `--version beta` | Beta when it is newer, otherwise the release (the catalogue states which) |
| `--version 1.0.24` / `beta-1.0.22` | Pinned release tokens |
| `--force` | Reinstall even when `$STATE/version` already matches |
| `--refresh-proton` | Rebuild Proton-TF2VR |
| `--uninstall` | Removes mod files, launcher, `.desktop` entry, Proton-TF2VR; keeps campaign saves |
| `--game DIR` / `--prefix DIR` / `--proton-base DIR` | Manual discovery overrides; values must be existing directories |

Unknown options exit 2. `--version` without a value exits 2 with a message.

## Paths owned by the backend

| Path | Meaning |
|---|---|
| `~/.local/bin/tf2vr` | Installed launcher (mode 755) |
| `${XDG_DATA_HOME}/applications/tf2vr.desktop` | Menu entry from `tf2vr.desktop` |
| `${XDG_DATA_HOME}/tf2vr/` | State: `selection`, `version`, `discord-presence.py`, `no-steam/` |
| `${XDG_CONFIG_HOME}/tf2vr/paths.env` | `STEAM_ROOT`, `GAME_DIR`, `PREFIX_DIR`, `PROTON_BIN`, `GAME_SOURCE`, … |
| `${XDG_CACHE_HOME}/tf2vr/` | Downloaded manifest/packages |

## Exit and output handling

- Exit code 0 is necessary but not sufficient: the app re-reads `${XDG_DATA_HOME}/tf2vr/version`
  and verifies the launcher before reporting success.
- stdout/stderr are captured line by line into the operation log; parsing of progress never
  depends on specific phrases.
- The catalogue parser tolerates ANSI sequences, CRLF and extra sections; a parse failure never
  fabricates tokens.

## What the app must not do

- Reimplement downloads, checksums, Proton building or file placement.
- Write into the game directory or the normal Titanfall 2 Steam entry.
- Run `install.sh` while a Steam shortcut transaction is mid-flight.
- Treat the checkout as trusted without verifying `origin` and required files.
