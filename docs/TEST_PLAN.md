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
| Two clean package builds | `packaging/build-release.sh` twice | identical SHA-256 (`136bd95d…`) |
| No game/mod payload | archive guard in the build script | pass |
| Menu entry validity | `desktop-file-validate` on the rendered entry | valid |
| Install | packaged `install.sh` with throwaway `HOME` | 232 files verified, app + symlink + 7 icons + entry |
| Tampered archive | modified byte in a packaged `.dll` | aborted, nothing written |
| Unsafe checksum list | absolute path added to `SHA256SUMS` | aborted, path rejected |
| Uninstall | `install.sh --uninstall` | app/icon/entry removed, `backend/` kept |
| Packaged binary launch | installed entry via `gtk-launch`, see below | window opened; `Exec` and `Icon` resolve |
| Packaged UI assets | installed archive launched after the UI change | serif font, icon, badge and footer render from the package |
| Dependency audit | `dotnet list package --vulnerable --include-transitive` | no vulnerable packages |

### Packaged binary launch (verified 2026-11-12)

Installed the archive into a throwaway `HOME`, then started the app through the generated menu
entry (not the dev tree and not the binary directly):

```bash
export HOME=/tmp/desk/home
export XDG_DATA_HOME=$HOME/.local/share XDG_CONFIG_HOME=$HOME/.config XDG_CACHE_HOME=$HOME/.cache
tar -xzf artifacts/circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz -C /tmp/desk
/tmp/desk/circuitlord-vrmodinstaller-1.0.0-linux-x64/install.sh

ENTRY=$XDG_DATA_HOME/applications/circuitlord-vrmodinstaller.desktop
desktop-file-validate "$ENTRY"                 # exit 0, no output
EXEC=$(sed -n 's/^Exec=//p' "$ENTRY")
test -x "$EXEC"                                 # Exec path exists and is executable
gtk-launch circuitlord-vrmodinstaller          # blocks while the app runs; run in background

WID=$(xdotool search --name "CircuitLord's VR Mod Installer" | head -1)
xwininfo -id "$WID" | grep -E 'Map State|Width|Height'
readlink -f "/proc/$(xdotool getwindowpid "$WID")/exe"   # the installed binary, not bin/Debug
```

Icon lookup through the real GTK theme path (with the throwaway `XDG_DATA_HOME` exported):

```bash
python3 - <<'PY'
import gi; gi.require_version('Gtk', '3.0')
from gi.repository import Gtk
print(Gtk.IconTheme.get_default().lookup_icon('circuitlord-vrmodinstaller', 48, 0).get_filename())
PY
```

Observed:

| Item | Result |
|---|---|
| `desktop-file-validate` | exit 0 |
| `Exec` | `/tmp/desk/home/.local/share/circuitlord-vrmodinstaller/app/CircuitLordVRModInstaller`, executable |
| `Icon` | `circuitlord-vrmodinstaller`; theme lookup returned `…/icons/hicolor/48x48/apps/circuitlord-vrmodinstaller.png` |
| `gtk-launch` | exit 0 after the app started |
| Window | title `CircuitLord's VR Mod Installer`, 800×740, `Map State: IsViewable` |
| Owning process | `/proc/<pid>/exe` → the installed binary inside the throwaway `HOME` |
| First run | cloned `tf2vr-linux` into `$XDG_DATA_HOME/circuitlord-vrmodinstaller/backend` |
| Shutdown | window closed, process exited, no error output |

## UI verification (2026-11-12)

Reference: original Windows XAML/theme at `64cf26c` and the supplied side-by-side screenshot. The
original declares `Segoe UI Variable Display` (proprietary, not in the repository); the reference
render is serif, so the port bundles OFL-licensed Liberation Serif in `Assets/Fonts` (notice in
`THIRD-PARTY-NOTICES.md`).

| Check | Result |
|---|---|
| Build after the UI change | succeeds, 0 warnings; 33/33 tests |
| 800×740 | icon + name + `1.0.0 · Linux` title bar, serif headings/cards, centred NEW badge, pinned footer |
| 640×560 and 560×520 (minimum) | footer stays at the bottom, cards and title bar intact, nothing overlaps or clips |
| Maximise / restore | 1920×1080 and back to 800×740 with content top-aligned and footer pinned |
| Window icon | title-bar 20 px icon and `_NET_WM_ICON` (128×128) present for the window manager |
| Typography | serif headings, card titles, subtitles and italic footer; no mixed sans/serif in content |
| NEW badge | white pill over the Titanfall artwork's top-left corner, clear of the title |
| Support button | red pill outline, heart, hover fill; activating it called `xdg-open https://ko-fi.com/circuitlord` (verified with a stub, no real browser opened) |
| Big Walk VR | disabled: not focusable, click is a no-op, `No backend` chip and `Not available on Linux` retained |
| Titanfall 2 VR | click and keyboard Enter both open the installer page; Back returns and restores the footer |
| Hover / focus | accent border on hover and keyboard focus; pressed opacity on cards |
| Resize | edge drag 800→913 px; `wmctrl` 640×560 and 560×520 |
| Minimise / close | `Map State: IsUnMapped` when minimised, process exits on close |
| Support destination | `https://ko-fi.com/circuitlord`, from `SupportUrl` in the original `MainWindow.xaml.cs` |

## Not covered by automated tests

- Real backend install/update/uninstall on a machine with Titanfall 2 installed.
- Real Steam client shutdown/relaunch and library recognition.
- Real WiVRn discovery of the desktop entry.
- OpenXR runtime JSON parsing (service is implemented; no test yet).
- GUI interaction tests beyond the packaged launch check above (the first screen was observed, no
  clicks through install/Steam flows).

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
