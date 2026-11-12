#!/usr/bin/env bash
# Install CircuitLord's VR Mod Installer for the current user.
#
# No root, no package manager, no game or mod content: this installs the
# installer application only. Titanfall 2 VR itself is installed later by the
# app through Monkellie's tf2vr-linux backend.
#
#   ./install.sh              install for the current user
#   ./install.sh --uninstall  remove the app, its icon and its menu entry
#
# Paths follow the XDG base directories; HOME/XDG_DATA_HOME can be overridden
# for testing.
set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_ROOT="$DATA_HOME/circuitlord-vrmodinstaller"
APP_DIR="$APP_ROOT/app"
BIN_DIR="$HOME/.local/bin"
BIN_LINK="$BIN_DIR/circuitlord-vrmodinstaller"
DESKTOP_DIR="$DATA_HOME/applications"
DESKTOP_FILE="$DESKTOP_DIR/circuitlord-vrmodinstaller.desktop"
ICON_ROOT="$DATA_HOME/icons/hicolor"
DESKTOP_ID="circuitlord-vrmodinstaller"

die() { printf 'error: %s\n' "$1" >&2; exit 1; }

usage() {
  sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

refresh_caches() {
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f "$ICON_ROOT" 2>/dev/null || true
}

install_app() {
  [ -x "$HERE/app/CircuitLordVRModInstaller" ] || die "app/CircuitLordVRModInstaller is missing from the package."
  [ -f "$HERE/circuitlord-vrmodinstaller.desktop.in" ] || die "the desktop entry template is missing from the package."
  [ -d "$HERE/icons/hicolor" ] || die "the icon set is missing from the package."

  mkdir -p "$APP_DIR" "$BIN_DIR" "$DESKTOP_DIR"

  # Replace only this application's own directory; the backend checkout and
  # logs live next to it and are preserved.
  rm -rf "$APP_DIR"
  mkdir -p "$APP_DIR"
  cp -a "$HERE/app/." "$APP_DIR/"
  chmod 0755 "$APP_DIR/CircuitLordVRModInstaller"
  ln -sfn "$APP_DIR/CircuitLordVRModInstaller" "$BIN_LINK"

  local size source target
  for source in "$HERE"/icons/hicolor/*/apps/*.png; do
    [ -f "$source" ] || continue
    size=$(basename "$(dirname "$(dirname "$source")")")
    target="$ICON_ROOT/$size/apps"
    mkdir -p "$target"
    install -m 0644 "$source" "$target/$(basename "$source")"
  done

  local escaped
  escaped=$(printf '%s' "$APP_DIR/CircuitLordVRModInstaller" | sed 's/[&|\\]/\\&/g')
  sed -e "s|@EXEC@|$escaped|" "$HERE/circuitlord-vrmodinstaller.desktop.in" > "$DESKTOP_FILE.tmp"
  chmod 0644 "$DESKTOP_FILE.tmp"
  mv "$DESKTOP_FILE.tmp" "$DESKTOP_FILE"

  refresh_caches

  printf '\nInstalled CircuitLord'"'"'s VR Mod Installer\n'
  printf '  application  %s\n' "$APP_DIR/CircuitLordVRModInstaller"
  printf '  command      %s\n' "$BIN_LINK"
  printf '  menu entry   %s\n' "$DESKTOP_FILE"
  case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) printf '\nNote: %s is not on your PATH; run the app from the menu or with the full path.\n' "$BIN_DIR" ;;
  esac
  printf '\nTitanfall 2 VR is not installed by this step. Start the app to install it.\n'
}

uninstall_app() {
  rm -rf "$APP_DIR"
  rm -f "$BIN_LINK" "$DESKTOP_FILE"
  local icon
  for icon in "$ICON_ROOT"/*/apps/circuitlord-vrmodinstaller.png; do
    [ -f "$icon" ] && rm -f "$icon"
  done
  refresh_caches

  printf '\nRemoved the installer application, its menu entry and its icon.\n'
  printf 'Kept on purpose:\n'
  printf '  %s/backend  (managed tf2vr-linux checkout)\n' "$APP_ROOT"
  printf '  ${XDG_STATE_HOME:-$HOME/.local/state}/circuitlord-vrmodinstaller  (logs, history, Steam journals)\n'
  printf '  ${XDG_CONFIG_HOME:-$HOME/.config}/circuitlord-vrmodinstaller  (settings)\n'
  printf '  ~/.local/bin/tf2vr and the Titanfall 2 VR mod itself, if installed\n'
}

case "${1:-}" in
  "") install_app ;;
  --uninstall) uninstall_app ;;
  -h|--help) usage ;;
  *) die "unknown option '$1' (try --help)" ;;
esac
