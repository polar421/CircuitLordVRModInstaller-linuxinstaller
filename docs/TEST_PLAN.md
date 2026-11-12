# Test plan and current results

Commands:

```bash
dotnet test tests/CircuitLord.VRModInstaller.Core.Tests
dotnet build CircuitLord.VRModInstaller.Linux.slnx
```

## Automated (86 tests, all passing on 2026-11-12)

| Area | Coverage |
|---|---|
| Catalogue parser | Real-format output; ordinal 1=`latest`, 2=`beta`; installed/choice markers; reorder keeps token; CRLF + ANSI; empty/garbage output yields no tokens; numeric tokens treated as tokens; option-injection rejection |
| Manifest | Valid parse with channels/changelog; missing product; invalid JSON; malformed changelog entry tolerated |
| Release notes | Selected/installed exact version match; stable-only fallback keeps a stable label; empty notes remain unavailable |
| Desktop entry | Exact `Categories=Game;X-WiVRn-VR;`, XDG destination, Exec target, `Terminal=false`, repair after regeneration, missing launcher, spaces in path |
| VDF | Round trip, unknown-field preservation, idempotent upsert, same-name conflict refusal, owned-only removal |
| Steam transaction | Running → shutdown → backup → write → verify → reopen; refusal to restart; shutdown timeout writes nothing; write failure still reopens; reopen failure = recovery required; initially-closed stays closed |
| Appearance | KDE/GNOME/GTK theme detection, Dark fallback, explicit preference precedence, live system-theme and motion events; KDE/GNOME reduced-motion detection; scale clamping/snapping/factors; settings persistence |
| Localization | Ten complete catalogs (386 keys each), placeholder preservation, English fallback, unsupported-locale normalization, technical identifiers, unused-key detection, culture-aware formatting |
| Installer updater | Release archive/checksum discovery; installed-copy detection including symlinks; SHA-256 validation; safe extraction; verified package staging; mismatched archive rejection |

## Packaging (verified 2026-11-12)

| Check | Command | Result |
|---|---|---|
| Release build | `dotnet build -c Release` | 0 warnings, 0 errors |
| Release tests | `dotnet test -c Release` | 86/86 |
| Two clean package builds | `packaging/build-release.sh` twice | identical SHA-256 (`41479bfb…`) |
| No game/mod payload | archive guard in the build script | pass |
| Menu entry validity | `desktop-file-validate` on the rendered entry | valid |
| Install | packaged `install.sh` with throwaway `HOME` | 257 files verified, app + symlink + 7 icons + entry |
| Tampered archive | modified byte in a packaged `.dll` | aborted, nothing written |
| Unsafe checksum list | absolute path added to `SHA256SUMS` | aborted, path rejected |
| Uninstall | `install.sh --uninstall` | app/icon/entry removed, `backend/` kept |
| Packaged binary launch | installed entry via `gtk-launch`, see below | window opened; `Exec` and `Icon` resolve |
| Packaged UI assets | installed archive launched after the UI change | serif font, icon, badge and footer render from the package |
| Native Wayland | KWin session, packaged binary | rendered without an X11 window; `libwayland-client` loaded |
| X11 fallback | `WAYLAND_DISPLAY` unset | window rendered through X11 |
| Settings persistence | change theme, scale, motion, corners and language, restart | `settings.json` schema 4 records the choices and the UI reflects them after restart |
| Settings migration | schema 2 and 3 → 4 with existing Steam/VR/update preferences | defaults are added without discarding existing values; a backup is retained |
| Settings migration / backend rollback | schema 2 → 3; invalid remote commit | defaults migrate with Steam confirmation enabled; the failed helper update is rolled back to the preserved valid commit |
| Dependency audit | `dotnet list package --vulnerable --include-transitive` | no vulnerable packages |

### Packaged binary launch (v1.0.0 archive, verified 2026-11-12)

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
| Build after the UI change | succeeds, 0 warnings; 86/86 tests |
| 800×740 | icon + `CircuitLord VR Mod Install unofficial-port for Linux` + `1.0.5 · Linux · Unofficial` title bar, serif headings/cards, centred NEW badge, pinned footer |
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

## Appearance and localization verification (2026-11-12)

Avalonia headless run at 800×740 (plus a 560×520 stress window), with a temporary XDG home and a
GNOME-shaped `gsettings` stub. The default theme was Dark; System resolved to Light through the
GNOME detector.

| Check | Result |
|---|---|
| Themes | Dark window `#14171C`; Light window `#F3F5F9`; live Dark ↔ Light switch updated the Settings sheet, title bar, cards, hero, dropdown popup and installer without restart |
| Theme status | System selected with GNOME-shaped detection showed the localized desktop-following message and resolved to Light; explicit Dark/Light was not overwritten by system detection |
| Theme propagation | Code-assigned status/badge brushes refreshed through `Theme.Changed`; light-theme update text `#526173`, success badge `#1D7A4B` |
| UI scale | Transform factors verified for 80, 85, 90, 100, 110, 125, 135 and 150%; Settings inspected at 80/100/150%, installer at 80/100%, and the 150% small-window shell at 560×520 |
| Scale 150% | 800×740: root child 534×494, Settings border 510×470, scrollable content 452×367; 560×520: Settings 350×323 with a 292×220 scroll viewport |
| Scale 80% | Root logical size 1000×925, rendered into 800×740; no text truncation or invisible controls in the inspected Settings screen |
| Dropdowns / modals | Light and Dark dropdown popups, Settings sheet, confirm/runtime/log overlays and the scaled release picker inspected; the 150% release picker stayed inside the 800×740 window |
| Languages | All ten catalogs have 386/386 keys; English, Japanese and Simplified Chinese installer screens plus Japanese Settings were inspected; Japanese/Simplified Chinese rendered with Noto Sans CJK and Russian resolved to Liberation Serif |
| Live language switch | English → Japanese → Simplified Chinese → Russian → English updated the window title, Settings labels, closed Theme/Motion dropdown text, installer labels and release-picker labels |
| Formatting | Culture-aware number/date formatting is covered by tests; version strings and backend tokens remain unchanged |
| Reduced motion | Reduced motion set the `reduced-motion` window class and removed button transitions; Full animations restored them. Follow system, explicit Reduced and explicit Full precedence are covered by Core tests |

Raw backend/git output and release notes are intentionally shown unchanged; the translated UI
surrounds them. Actual KDE/GNOME sessions were simulated through their documented configuration
and command interfaces, not exercised on a live desktop in this environment.

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
