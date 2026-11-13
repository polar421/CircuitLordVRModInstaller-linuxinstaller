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
CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
STATE_HOME="${XDG_STATE_HOME:-$HOME/.local/state}"
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

# Verifies every packaged file against the release's SHA256SUMS before anything
# is written to the user's home. A mismatch means the archive is corrupt or was
# tampered with, so installation stops with a clear message.
verify_package() {
  local sums="$HERE/SHA256SUMS"
  [ -f "$sums" ] || die "SHA256SUMS is missing from this package; refusing to install an unverified archive."
  command -v sha256sum >/dev/null 2>&1 || die "sha256sum (coreutils) is required to verify the package."

  # Only relative paths that stay inside the extracted archive are acceptable.
  if awk 'NF && ($2 ~ /^\// || $2 ~ /(^|\/)\.\.(\/|$)/)' "$sums" | grep -q .; then
    die "SHA256SUMS contains a path outside this package; refusing to install."
  fi

  local count
  count=$(awk 'NF' "$sums" | wc -l)
  [ "$count" -gt 0 ] || die "SHA256SUMS is empty; refusing to install."

  if ! ( cd "$HERE" && sha256sum --check --strict --quiet "$sums" ); then
    die "package integrity check failed: a file does not match SHA256SUMS. Re-download the release archive and try again."
  fi

  printf 'Verified %s files against SHA256SUMS\n' "$count"
}

refresh_caches() {
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f "$ICON_ROOT" 2>/dev/null || true
}

install_app() {
  verify_package

  [ -x "$HERE/app/CircuitLordVRModInstaller" ] || die "app/CircuitLordVRModInstaller is missing from the package."
  [ -f "$HERE/circuitlord-vrmodinstaller.desktop.in" ] || die "the desktop entry template is missing from the package."
  [ -d "$HERE/icons/hicolor" ] || die "the icon set is missing from the package."

  mkdir -p "$APP_ROOT" "$BIN_DIR" "$DESKTOP_DIR"

  # Stage beside the install and swap it in with a rename. If anything fails,
  # the previous app directory is restored instead of leaving a half-installed
  # application behind. The backend checkout and logs live next to this
  # directory and are always preserved.
  local stage="$APP_ROOT/app.new"
  local backup="$APP_ROOT/app.previous"
  rm -rf "$stage" "$backup"
  mkdir -p "$stage"
  cp -a "$HERE/app/." "$stage/"
  chmod 0755 "$stage/CircuitLordVRModInstaller"
  [ -x "$stage/CircuitLordVRModInstaller" ] || die "the staged application binary is not executable."

  if [ -e "$APP_DIR" ] || [ -L "$APP_DIR" ]; then
    mv "$APP_DIR" "$backup" || die "could not move the existing application aside."
  fi
  if ! mv "$stage" "$APP_DIR"; then
    [ -e "$backup" ] && mv "$backup" "$APP_DIR"
    die "could not activate the staged application."
  fi
  if [ ! -x "$APP_DIR/CircuitLordVRModInstaller" ]; then
    rm -rf "$APP_DIR"
    [ -e "$backup" ] && mv "$backup" "$APP_DIR"
    die "the installed application binary is missing or not executable."
  fi
  rm -rf "$backup" 2>/dev/null || true
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
  rm -rf "$APP_DIR" "$APP_ROOT/app.new" "$APP_ROOT/app.previous"
  rm -f "$BIN_LINK" "$DESKTOP_FILE"
  local icon
  for icon in "$ICON_ROOT"/*/apps/circuitlord-vrmodinstaller.png; do
    [ -f "$icon" ] && rm -f "$icon"
  done
  refresh_caches

  printf '\nRemoved the installer application, its menu entry and its icon.\n'
  printf 'Kept on purpose:\n'
  printf '  %s/backend  (managed tf2vr-linux checkout)\n' "$APP_ROOT"
  printf '  %s/circuitlord-vrmodinstaller  (logs, history, Steam journals)\n' "$STATE_HOME"
  printf '  %s/circuitlord-vrmodinstaller  (settings)\n' "$CONFIG_HOME"
  printf '  ~/.local/bin/tf2vr and the Titanfall 2 VR mod itself, if installed\n'
}

case "${1:-}" in
  "") install_app ;;
  --uninstall) uninstall_app ;;
  -h|--help) usage ;;
  *) die "unknown option '$1' (try --help)" ;;
esac
