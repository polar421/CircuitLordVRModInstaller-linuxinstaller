#!/usr/bin/env bash
# Build a reproducible, self-contained linux-x64 release of the installer.
#
#   packaging/build-release.sh
#   VERSION_OVERRIDE=1.0.3 packaging/build-release.sh
#
# VERSION_OVERRIDE builds the same source with a different application version.
# It is intended for channel builds (for example a stable 1.0.3 archive and a
# newer beta archive); the normal release workflow does not set it.
#
# Outputs, under artifacts/:
#   circuitlord-vrmodinstaller-<version>-linux-x64/        staged installable tree
#   circuitlord-vrmodinstaller-<version>-linux-x64.tar.gz  release archive
#   circuitlord-vrmodinstaller-<version>-linux-x64.tar.gz.sha256
#
# The archive contains the application, its icon, its desktop entry template,
# the user-level installer and the license notices. It never contains game
# files, mod packages or any downloaded payload; the app fetches those through
# the backend at install time.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
APP_PROJECT="$ROOT/src/CircuitLord.VRModInstaller.App/CircuitLord.VRModInstaller.App.csproj"
SOLUTION="$ROOT/CircuitLord.VRModInstaller.Linux.slnx"
RID="linux-x64"
ARTIFACTS="${ARTIFACTS:-$ROOT/artifacts}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

command -v dotnet >/dev/null 2>&1 || { echo "error: dotnet is not installed" >&2; exit 1; }

VERSION="${VERSION_OVERRIDE:-$(dotnet msbuild "$APP_PROJECT" -nologo -getProperty:Version | tr -d '\r\n')}"
[ -n "$VERSION" ] || { echo "error: could not read the application version" >&2; exit 1; }

PUBLISH_VERSION_ARGS=()
if [ -n "${VERSION_OVERRIDE:-}" ]; then
  PUBLISH_VERSION_ARGS+=("-p:Version=$VERSION")
fi

STAGE_NAME="circuitlord-vrmodinstaller-$VERSION-$RID"
STAGE="$ARTIFACTS/$STAGE_NAME"
TARBALL="$ARTIFACTS/$STAGE_NAME.tar.gz"

echo "==> Version $VERSION ($RID)"

# Locked restore for the packaged runtime: every transitive package version
# comes from packages.lock.json, including the linux-x64 runtime graph.
dotnet restore "$APP_PROJECT" --locked-mode --runtime "$RID"

rm -rf "$STAGE"
mkdir -p "$STAGE/app"

# Self-contained publish: the target machine does not need a .NET runtime.
dotnet publish "$APP_PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  --no-restore \
  -p:PublishSingleFile=false \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:ContinuousIntegrationBuild=true \
  "${PUBLISH_VERSION_ARGS[@]}" \
  --output "$STAGE/app"

# --- package contents -------------------------------------------------------
install -m 0755 "$ROOT/packaging/install.sh" "$STAGE/install.sh"
install -m 0644 "$ROOT/packaging/circuitlord-vrmodinstaller.desktop.in" "$STAGE/circuitlord-vrmodinstaller.desktop.in"
mkdir -p "$STAGE/icons"
cp -a "$ROOT/packaging/icons/." "$STAGE/icons/"
install -m 0644 "$ROOT/LICENSE" "$STAGE/LICENSE"
install -m 0644 "$ROOT/THIRD-PARTY-NOTICES.md" "$STAGE/THIRD-PARTY-NOTICES.md"
# Bundled font licence must travel with the font (SIL OFL 1.1).
mkdir -p "$STAGE/licenses"
install -m 0644 "$ROOT/src/CircuitLord.VRModInstaller.App/Assets/Fonts/LICENSE.txt" \
  "$STAGE/licenses/LiberationSerif-OFL.txt"
install -m 0644 "$ROOT/src/CircuitLord.VRModInstaller.App/Assets/Fonts/NOTO-OFL.txt" \
  "$STAGE/licenses/NotoSansCJK-OFL.txt"
install -m 0644 "$ROOT/README.md" "$STAGE/README.md"

# --- guards -----------------------------------------------------------------
# 1) Nothing game/mod-like may be bundled.
forbidden=$(find "$STAGE" \( -iname '*.zip' -o -iname '*.pak' -o -iname '*.vpk' \
  -o -iname 'BepInEx*' -o -iname 'Northstar*' -o -iname 'Titanfall2*' \
  -o -iname 'Titanfall2VR*' -o -iname '*.exe' \) -print)
if [ -n "$forbidden" ]; then
  echo "error: the package contains game or mod content:" >&2
  echo "$forbidden" >&2
  exit 1
fi

# 2) The shipped desktop entry must be a valid, installable menu entry.
if command -v desktop-file-validate >/dev/null 2>&1; then
  rendered=$(mktemp --suffix=.desktop)
  sed "s|@EXEC@|$STAGE/app/CircuitLordVRModInstaller|" "$STAGE/circuitlord-vrmodinstaller.desktop.in" > "$rendered"
  if ! desktop-file-validate "$rendered"; then
    rm -f "$rendered"
    echo "error: the packaged desktop entry is not valid" >&2
    exit 1
  fi
  rm -f "$rendered"
fi

# 3) Required runtime files must exist.
for required in "$STAGE/app/CircuitLordVRModInstaller" "$STAGE/install.sh" "$STAGE/icons/hicolor/256x256/apps/circuitlord-vrmodinstaller.png"; do
  [ -f "$required" ] || { echo "error: missing $required" >&2; exit 1; }
done

# --- checksums and archive --------------------------------------------------
(
  cd "$STAGE"
  find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS
)

# Fixed ownership and timestamps make the tarball byte-identical for the same
# commit, so a second build can be compared directly.
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --format=%ct 2>/dev/null || echo 0)}"
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$SOURCE_DATE_EPOCH" \
  -C "$ARTIFACTS" -czf "$TARBALL" "$STAGE_NAME"
sha256sum "$TARBALL" | sed "s|$ARTIFACTS/||" > "$TARBALL.sha256"

echo
echo "==> Release built"
du -sh "$STAGE" "$TARBALL" | sed 's/\t/  /'
echo
cat "$TARBALL.sha256"
echo
echo "Install with:"
echo "  tar -xzf $(basename "$TARBALL")"
echo "  ./$STAGE_NAME/install.sh"
