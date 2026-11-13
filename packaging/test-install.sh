#!/usr/bin/env bash
# Exercise install.sh upgrade and uninstall behavior in a throwaway HOME.
#
# Two synthetic package versions use the real install.sh. The test verifies
# that an upgrade keeps backend, settings, state and tf2vr data, and that
# --uninstall removes only app-owned files and prints resolved XDG paths.
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
install_script="$repo_root/packaging/install.sh"
[ -x "$install_script" ] || { echo "missing $install_script" >&2; exit 2; }

tmp=$(mktemp -d "${TMPDIR:-/tmp}/circuitlord-install-test.XXXXXX")
trap 'rm -rf "$tmp"' EXIT

export HOME="$tmp/home"
export XDG_DATA_HOME="$tmp/data"
export XDG_CONFIG_HOME="$tmp/config"
export XDG_STATE_HOME="$tmp/state"
export XDG_RUNTIME_DIR="$tmp/runtime"
mkdir -p "$HOME" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

data_root="$XDG_DATA_HOME/circuitlord-vrmodinstaller"
config_root="$XDG_CONFIG_HOME/circuitlord-vrmodinstaller"
state_root="$XDG_STATE_HOME/circuitlord-vrmodinstaller"
bin_root="$HOME/.local/bin"
app_dir="$data_root/app"
bin_link="$bin_root/circuitlord-vrmodinstaller"
desktop_file="$XDG_DATA_HOME/applications/circuitlord-vrmodinstaller.desktop"
icon_file="$XDG_DATA_HOME/icons/hicolor/256x256/apps/circuitlord-vrmodinstaller.png"

die() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

run_install() {
  local package=$1 log=$2
  if ! bash "$package/install.sh" >"$log" 2>&1; then
    printf 'install failed: %s\n' "$package" >&2
    cat "$log" >&2
    exit 1
  fi
}

make_package() {
  local package=$1 version=$2
  mkdir -p "$package/app" "$package/icons/hicolor/256x256/apps"
  cat > "$package/app/CircuitLordVRModInstaller" <<EOF
#!/usr/bin/env bash
printf '%s\n' '$version'
EOF
  chmod 0755 "$package/app/CircuitLordVRModInstaller"
  printf 'package %s\n' "$version" > "$package/icons/hicolor/256x256/apps/circuitlord-vrmodinstaller.png"
  cat > "$package/circuitlord-vrmodinstaller.desktop.in" <<EOF
[Desktop Entry]
Type=Application
Name=Test package $version
Exec=@EXEC@
Icon=circuitlord-vrmodinstaller
Terminal=false
Categories=Game;
EOF
  install -m 0755 "$install_script" "$package/install.sh"
  (
    cd "$package"
    find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS
  )
}

check_preserved() {
  [ "$(cat "$data_root/backend/marker")" = "backend-data" ] || die "backend data changed"
  [ "$(cat "$config_root/settings.json")" = '{"settings":"keep"}' ] || die "settings changed"
  [ "$(cat "$state_root/logs/operation.log")" = "state-data" ] || die "state changed"
  [ "$("$bin_root/tf2vr")" = "tf2vr-ok" ] || die "tf2vr launcher changed"
  [ "$(cat "$XDG_DATA_HOME/tf2vr/selection")" = "tf2vr-selection" ] || die "tf2vr state changed"
}

# Package A is the initial installation.
pkg_a="$tmp/package-a"
make_package "$pkg_a" "package-a"
run_install "$pkg_a" "$tmp/install-a.log"

grep -Fq "package-a" "$app_dir/CircuitLordVRModInstaller" || die "package A was not installed"
[ -L "$bin_link" ] || die "install did not create the command symlink"
[ -f "$desktop_file" ] || die "install did not create the desktop entry"
[ -f "$icon_file" ] || die "install did not create the icon"

# User data owned by other parts of the product must survive reinstalls.
mkdir -p "$data_root/backend" "$config_root" "$state_root/logs" "$bin_root" "$XDG_DATA_HOME/tf2vr" \
  "$XDG_DATA_HOME/applications" "$XDG_DATA_HOME/icons/hicolor/256x256/apps"
printf 'backend-data\n' > "$data_root/backend/marker"
printf '{"settings":"keep"}\n' > "$config_root/settings.json"
printf 'state-data\n' > "$state_root/logs/operation.log"
cat > "$bin_root/tf2vr" <<'EOF'
#!/usr/bin/env bash
printf 'tf2vr-ok\n'
EOF
chmod 0755 "$bin_root/tf2vr"
printf 'tf2vr-selection\n' > "$XDG_DATA_HOME/tf2vr/selection"
printf 'other desktop\n' > "$XDG_DATA_HOME/applications/other.desktop"
printf 'other icon\n' > "$XDG_DATA_HOME/icons/hicolor/256x256/apps/other.png"
printf 'other command\n' > "$bin_root/other-command"

# Package B replaces only the app-owned installation.
pkg_b="$tmp/package-b"
make_package "$pkg_b" "package-b"
run_install "$pkg_b" "$tmp/install-b.log"

grep -Fq "package-b" "$app_dir/CircuitLordVRModInstaller" || die "package B did not replace package A"
grep -Fq "Test package package-b" "$desktop_file" || die "desktop entry was not upgraded"
cmp -s "$pkg_b/icons/hicolor/256x256/apps/circuitlord-vrmodinstaller.png" "$icon_file" || die "icon was not upgraded"
[ "$(readlink "$bin_link")" = "$app_dir/CircuitLordVRModInstaller" ] || die "command symlink target is wrong"
check_preserved
printf 'PASS: upgrade replaced app-owned files and preserved user data\n'

# Uninstall removes app-owned files and leaves unrelated files and user data.
if ! bash "$pkg_b/install.sh" --uninstall >"$tmp/uninstall.log" 2>&1; then
  cat "$tmp/uninstall.log" >&2
  die "uninstall failed"
fi
grep -Fqx "  $state_root  (logs, history, Steam journals)" "$tmp/uninstall.log" || die "state path was not resolved"
grep -Fqx "  $config_root  (settings)" "$tmp/uninstall.log" || die "config path was not resolved"
[ ! -e "$app_dir" ] || die "app directory was not removed"
[ ! -e "$bin_link" ] || die "command symlink was not removed"
[ ! -e "$desktop_file" ] || die "desktop entry was not removed"
[ ! -e "$icon_file" ] || die "app icon was not removed"
[ -f "$XDG_DATA_HOME/applications/other.desktop" ] || die "unrelated desktop entry was removed"
[ -f "$XDG_DATA_HOME/icons/hicolor/256x256/apps/other.png" ] || die "unrelated icon was removed"
[ -f "$bin_root/other-command" ] || die "unrelated command was removed"
check_preserved
printf 'PASS: uninstall removed only app-owned files and printed resolved XDG paths\n'
