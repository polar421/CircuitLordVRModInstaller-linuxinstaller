#!/usr/bin/env bash
# Packaging smoke test: install the release archive into a throwaway HOME, launch
# the installed binary on a headless X server and assert the main window appears.
#
# Usage: packaging/smoke-test.sh [path/to/release.tar.gz]
# Exits 0 with "SKIP:" when Xvfb/xvfb-run/xauth/xdotool are not installed.

set -euo pipefail

EXPECTED_TITLE="CircuitLord's VR Mod Installer"

skip() {
  printf 'SKIP: %s\n' "$*"
  exit 0
}

die() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
archive=${1:-}
if [ -z "$archive" ]; then
  archive=$(ls -1t "$repo_root"/artifacts/circuitlord-vrmodinstaller-*-linux-x64.tar.gz 2>/dev/null | head -1 || true)
fi
[ -n "$archive" ] || die "no release archive found; run packaging/build-release.sh first or pass the .tar.gz path"
[ -f "$archive" ] || die "archive not found: $archive"

for tool in Xvfb xvfb-run xauth xdotool; do
  command -v "$tool" >/dev/null 2>&1 || skip "$tool is not installed; install xvfb, xauth and xdotool to run the packaging smoke test"
done

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

tar -xzf "$archive" -C "$tmp"
pkg=$(find "$tmp" -maxdepth 1 -mindepth 1 -type d -name 'circuitlord-vrmodinstaller-*' -print -quit)
[ -n "$pkg" ] || die "archive has no circuitlord-vrmodinstaller-* top-level directory"

export HOME="$tmp/home"
export XDG_DATA_HOME="$HOME/.local/share"
export XDG_CONFIG_HOME="$HOME/.config"
export XDG_CACHE_HOME="$HOME/.cache"
export XDG_RUNTIME_DIR="$tmp/runtime"
mkdir -p "$HOME" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

"$pkg/install.sh"

app="$XDG_DATA_HOME/circuitlord-vrmodinstaller/app/CircuitLordVRModInstaller"
[ -x "$app" ] || die "installed binary is missing or not executable: $app"

cat > "$tmp/check-window.sh" <<'CHECK'
set -uo pipefail
ulimit -c 0 2>/dev/null || true

"$SMOKE_APP" >"$SMOKE_LOG" 2>&1 &
app_pid=$!
trap 'kill "$app_pid" 2>/dev/null; wait "$app_pid" 2>/dev/null' EXIT

for _ in $(seq 1 60); do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    printf 'app exited before opening a window\n' >&2
    tail -n 20 "$SMOKE_LOG" >&2 || true
    exit 2
  fi

  wid=$(xdotool search --name "$SMOKE_TITLE" 2>/dev/null | head -1 || true)
  if [ -n "${wid:-}" ]; then
    name=$(xdotool getwindowname "$wid" 2>/dev/null || true)
    if [ "$name" = "$SMOKE_TITLE" ]; then
      printf 'window-found: %s (wid %s)\n' "$name" "$wid"
      exit 0
    fi
    printf 'unexpected window title: %s\n' "$name" >&2
    exit 3
  fi
  sleep 0.5
done

printf 'no window titled "%s" within 30s\n' "$SMOKE_TITLE" >&2
tail -n 20 "$SMOKE_LOG" >&2 || true
exit 4
CHECK

printf 'Launching %s under Xvfb...\n' "$EXPECTED_TITLE"
if xvfb-run -a -s "-screen 0 1280x1024x24" \
  env SMOKE_APP="$app" SMOKE_TITLE="$EXPECTED_TITLE" SMOKE_LOG="$tmp/app.log" \
  bash "$tmp/check-window.sh"; then
  printf 'PASS: %s opened under Xvfb\n' "$EXPECTED_TITLE"
else
  status=$?
  printf 'FAIL: launch check failed (exit %s)\n' "$status" >&2
  [ -f "$tmp/app.log" ] && tail -n 20 "$tmp/app.log" >&2
  exit "$status"
fi
