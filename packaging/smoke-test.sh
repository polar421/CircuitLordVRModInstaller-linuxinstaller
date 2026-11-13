#!/usr/bin/env bash
# Download a published release, verify its checksum, install it into a
# throwaway HOME, and assert that its main window opens under Xvfb.
#
# Usage: packaging/smoke-test.sh v1.0.9-beta.3
#
# Requires curl, Xvfb, xvfb-run, xauth, xdotool and timeout.
set -euo pipefail

TITLE="CircuitLord VR Mod Installer"
REPO="${GITHUB_REPOSITORY:-polar421/CircuitLordVRModInstaller-linuxinstaller}"

die() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

tag=${1:-}
[ -n "$tag" ] || die "usage: $0 vTAG"
[[ "$tag" =~ ^v[0-9][0-9A-Za-z._-]*$ ]] || die "invalid release tag: $tag"
version=${tag#v}

for tool in curl sha256sum tar Xvfb xvfb-run xauth xdotool timeout; do
  command -v "$tool" >/dev/null 2>&1 || die "missing required tool: $tool"
done

tmp=$(mktemp -d "${TMPDIR:-/tmp}/circuitlord-smoke.XXXXXX")
trap 'rm -rf "$tmp"' EXIT

archive="circuitlord-vrmodinstaller-$version-linux-x64.tar.gz"
base="https://github.com/$REPO/releases/download/$tag"
printf 'Downloading %s...\n' "$archive"
curl -fsSL --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 20 \
  -o "$tmp/$archive" "$base/$archive"
curl -fsSL --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 20 \
  -o "$tmp/$archive.sha256" "$base/$archive.sha256"

printf 'Verifying archive checksum...\n'
(cd "$tmp" && sha256sum --check --strict "$archive.sha256")

tar -xzf "$tmp/$archive" -C "$tmp"
pkg=$(find "$tmp" -maxdepth 1 -mindepth 1 -type d -name 'circuitlord-vrmodinstaller-*' -print -quit)
[ -n "$pkg" ] || die "archive has no circuitlord-vrmodinstaller-* top-level directory"

export HOME="$tmp/home"
export XDG_DATA_HOME="$HOME/.local/share"
export XDG_CONFIG_HOME="$HOME/.config"
export XDG_CACHE_HOME="$HOME/.cache"
export XDG_STATE_HOME="$HOME/.local/state"
export XDG_RUNTIME_DIR="$tmp/runtime"
mkdir -p "$HOME" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

printf 'Installing into temporary HOME: %s\n' "$HOME"
"$pkg/install.sh"

app="$XDG_DATA_HOME/circuitlord-vrmodinstaller/app/CircuitLordVRModInstaller"
[ -x "$app" ] || die "installed binary is missing or not executable: $app"

printf 'Launching %s under Xvfb...\n' "$TITLE"
if ! xvfb-run -a -s "-screen 0 1280x1024x24" bash -c '
    "$1" >"$2" 2>&1 &
    pid=$!
    trap "kill \$pid 2>/dev/null || true; wait \$pid 2>/dev/null || true" EXIT
    timeout 30s xdotool search --sync --onlyvisible --name "$3" >/dev/null
  ' bash "$app" "$tmp/app.log" "$TITLE"; then
  printf 'FAIL: main window did not appear\n' >&2
  tail -n 20 "$tmp/app.log" >&2 || true
  exit 1
fi

printf 'PASS: %s opened under Xvfb\n' "$TITLE"
