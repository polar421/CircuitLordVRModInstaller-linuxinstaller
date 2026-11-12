# Test plan and current results

Commands:

```bash
dotnet test tests/CircuitLord.VRModInstaller.Core.Tests
dotnet build CircuitLord.VRModInstaller.Linux.slnx
```

## Automated (33 tests, all passing on 2026-11-12)

| Area | Coverage |
|---|---|
| Catalogue parser | Real-format output; ordinal 1=`latest`, 2=`beta`; installed/choice markers; reorder keeps token; CRLF + ANSI; empty/garbage output yields no tokens; numeric tokens treated as tokens; option-injection rejection |
| Manifest | Valid parse with channels/changelog; missing product; invalid JSON; malformed changelog entry tolerated |
| Desktop entry | Exact `Categories=Game;X-WiVRn-VR;`, XDG destination, Exec target, `Terminal=false`, repair after regeneration, missing launcher, spaces in path |
| VDF | Round trip, unknown-field preservation, idempotent upsert, same-name conflict refusal, owned-only removal |
| Steam transaction | Running → shutdown → backup → write → verify → reopen; refusal to restart; shutdown timeout writes nothing; write failure still reopens; reopen failure = recovery required; initially-closed stays closed |

## Packaging (verified 2026-11-12)

| Check | Command | Result |
|---|---|---|
| Release build | `dotnet build -c Release` | 0 warnings, 0 errors |
| Release tests | `dotnet test -c Release` | 33/33 |
| Two clean package builds | `packaging/build-release.sh` twice | identical SHA-256 (`fb8a07fa…`) |
| No game/mod payload | archive guard in the build script | pass |
| Menu entry validity | `desktop-file-validate` on the rendered entry | valid |
| Install | packaged `install.sh` with throwaway `HOME` | 231 files verified, app + symlink + 7 icons + entry |
| Tampered archive | modified byte in a packaged `.dll` | aborted, nothing written |
| Unsafe checksum list | absolute path added to `SHA256SUMS` | aborted, path rejected |
| Uninstall | `install.sh --uninstall` | app/icon/entry removed, `backend/` kept |
| Dependency audit | `dotnet list package --vulnerable --include-transitive` | no vulnerable packages |

## Not covered by automated tests

- Real backend install/update/uninstall on a machine with Titanfall 2 installed.
- Real Steam client shutdown/relaunch and library recognition.
- Real WiVRn discovery of the desktop entry.
- OpenXR runtime JSON parsing (service is implemented; no test yet).
- GUI interaction tests.

## Manual acceptance still required

1. Install Stable through the UI in a machine with Titanfall 2; confirm normal game untouched.
2. Switch to Beta; confirm the process received `beta` (see the operation log).
3. Verify `~/.local/share/applications/tf2vr.desktop` contents and WiVRn picker.
4. Add the Steam shortcut while Steam is running; confirm graceful close, backup, verified write,
   automatic reopen, and a separate library entry.
5. Uninstall; confirm campaign saves and unrelated files survive, and only the owned desktop
   entry and Steam shortcut are removed.

## Fixture policy

Tests never read or write the user's real Steam configuration; all Steam tests use temporary
directories and a fake process controller.
