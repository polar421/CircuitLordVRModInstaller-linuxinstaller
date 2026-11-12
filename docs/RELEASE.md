# Build and release

## Prerequisites

- Linux x86-64 (the only packaged runtime identifier: `linux-x64`)
- .NET SDK **10.0.114** — pinned in `global.json` (`rollForward: latestPatch`)
- Network access to NuGet during the build (packages are not vendored)
- No ImageMagick or icon tooling required: the hicolor PNG set is committed

## Exact commands

```bash
# 1. Verify the source and pinned dependencies
cd /path/to/repository
dotnet restore CircuitLord.VRModInstaller.Linux.slnx --locked-mode
dotnet build CircuitLord.VRModInstaller.Linux.slnx -c Release
dotnet test tests/CircuitLord.VRModInstaller.Core.Tests -c Release

# 2. Build the self-contained release (restores, publishes, stages, checksums, tars)
packaging/build-release.sh

# 3. Inspect the result
sha256sum -c artifacts/circuitlord-vrmodinstaller-1.0.5-linux-x64.tar.gz.sha256
tar -tzf artifacts/circuitlord-vrmodinstaller-1.0.5-linux-x64.tar.gz | head
```

`build-release.sh` reads the version from the application project, so bumping `<Version>` in
`src/CircuitLord.VRModInstaller.App/CircuitLord.VRModInstaller.App.csproj` renames the artifacts.

### Build stable and beta archives from one tree

`VERSION_OVERRIDE` stamps a build with another application version without editing the project.
Both archives contain the same source; only their packaged version and file names differ.

```bash
# Stable-labelled archive: 1.0.3
VERSION_OVERRIDE=1.0.3 packaging/build-release.sh

# Beta/newest archive: the project's current version (1.0.5)
packaging/build-release.sh
```

The normal tag and release-branch workflows never set `VERSION_OVERRIDE`; they always publish the
version in the project file. Use the override only for deliberate channel builds.

## What the archive contains

```
circuitlord-vrmodinstaller-1.0.5-linux-x64/
  app/                                   self-contained publish (no .NET runtime needed)
    CircuitLordVRModInstaller            native ELF apphost
    *.dll, libSkiaSharp.so, …            Avalonia and .NET runtime files
  icons/hicolor/<16…256>/apps/circuitlord-vrmodinstaller.png
  circuitlord-vrmodinstaller.desktop.in  template with @EXEC@ placeholder
  install.sh                             user-level install / --uninstall
  SHA256SUMS
  LICENSE
  licenses/LiberationSerif-OFL.txt         bundled serif font licence (SIL OFL 1.1)
  licenses/NotoSansCJK-OFL.txt             bundled CJK font licence (SIL OFL 1.1)
  THIRD-PARTY-NOTICES.md
  README.md
```

No game files, mod packages, Proton builds, downloads or credentials are included. The build
script fails if it finds `*.zip`, `*.pak`, `*.vpk`, `*.exe`, `BepInEx*`, `Northstar*`,
`Titanfall2*` or `Titanfall2VR*` anywhere in the staged tree.

## Install and uninstall

```bash
tar -xzf circuitlord-vrmodinstaller-1.0.5-linux-x64.tar.gz
./circuitlord-vrmodinstaller-1.0.5-linux-x64/install.sh

# later
./circuitlord-vrmodinstaller-1.0.5-linux-x64/install.sh --uninstall
```

Before writing anything, `install.sh` verifies every packaged file against the archive's
`SHA256SUMS` (257 entries for the current 1.0.5 build) and prints `Verified <n> files against SHA256SUMS`. It aborts
with a non-zero exit and a clear message if a checksum mismatches, if the list contains an absolute
or `..` path, or if `SHA256SUMS` is missing or empty. Nothing is created in the home directory on
those failure paths.

The application directory is staged at `app.new` and swapped into place with a rename. If the
swap or binary check fails, the previous `app` directory is restored. The self-updater uses this
same installer from a detached helper after the running application has exited, so an interrupted
download or failed update never leaves a partially replaced install.

Layout (XDG, no root):

| Item | Path |
|---|---|
| Application | `${XDG_DATA_HOME:-~/.local/share}/circuitlord-vrmodinstaller/app` |
| Command | `~/.local/bin/circuitlord-vrmodinstaller` |
| Menu entry | `${XDG_DATA_HOME:-~/.local/share}/applications/circuitlord-vrmodinstaller.desktop` |
| Icon | `${XDG_DATA_HOME:-~/.local/share}/icons/hicolor/<size>/apps/circuitlord-vrmodinstaller.png` |

`--uninstall` removes only those four items. It deliberately keeps the managed `tf2vr-linux`
checkout, settings, logs/history/Steam journals, the installed `tf2vr` launcher and the mod, and
says so in its output.

This is the installer's own menu entry (`Categories=Utility;`). The Titanfall 2 VR game entry with
`Categories=Game;X-WiVRn-VR;` is created by the app after the mod is installed and is not installed
by this package.

## Reproducibility

Pinned inputs:

- SDK version via `global.json`
- every direct and transitive package via `packages.lock.json` in all three projects, restored with
  `--locked-mode`
- Avalonia `12.1.6`, including the opt-in `Avalonia.Wayland` package; its
  `Tmds.DBus.Protocol` dependency is newer than the CVE-2026-39959 fix

Deterministic output:

- `Deterministic=true`, `ContinuousIntegrationBuild=true`, no PDBs in the package
- archive written with `tar --sort=name --owner=0 --group=0 --numeric-owner --mtime=@SOURCE_DATE_EPOCH`
  (`SOURCE_DATE_EPOCH` defaults to the last commit time)

Measured on 2026-11-12: two consecutive clean builds produced the byte-identical archive

```
41479bfb1168d8e08ec81edbcd458d3062422197cc22c9ca4ae22fd966fec92d  circuitlord-vrmodinstaller-1.0.5-linux-x64.tar.gz
```

Build is reproducible for the same commit, SDK and package sources; a NuGet package republished
with different content would change the hash, which is the intended detection.

## Publishing a release

Releases are built by `.github/workflows/release.yml` when an annotated tag matching `v*` is
pushed. The workflow runs `packaging/build-release.sh` and uploads the archive and its checksum.
The tag name is the release title and the tag annotation is the release body, so write the notes
in the tag message. Prerelease status is derived from the tag:

- tags with a suffix (anything after a hyphen, e.g. `v1.0.5-beta.1`, `v1.0.5-rc.1`) publish as
  **prereleases**;
- plain version tags (`v1.0.5`) publish as **normal releases**.

Lightweight tags are rejected with a clear error. Example:

```bash
git tag -a v1.0.5-beta.2 -m "What changed in this beta"
git push origin v1.0.5-beta.2
```

### Publishing from a release branch

If the environment can push branches but not tags, push a `release/v<version>` branch instead.
`release-branch.yml` builds the same archive, verifies that the branch matches the packaged
version and notes, then creates the GitHub Release and its tag with the repository token. The notes
live in `.github/release-notes/v<version>.md`.

```bash
git branch release/v1.0.5
git push origin release/v1.0.5
```

## Verified for this release

| Check | Result |
|---|---|
| `dotnet build -c Release` | succeeds, 0 warnings |
| `dotnet test -c Release` | 52/52 pass |
| Two clean package builds | identical SHA-256 (`41479bfb…`) |
| Archive contents guard | no game/mod payload |
| `desktop-file-validate` on the shipped menu entry | valid |
| Install into a throwaway `HOME` | 257 files verified, app, symlink, 7 icon sizes, menu entry created |
| Native Wayland launch (KWin) | window rendered with no X11 window; `libwayland-client` loaded |
| X11 fallback launch | window rendered when `WAYLAND_DISPLAY` is unset |
| Tampered archive (one byte changed) | install aborted before any write, clear integrity message |
| Absolute path injected into `SHA256SUMS` | install aborted, path rejected |
| Missing/empty `SHA256SUMS` | install aborted |
| `--uninstall` | app/icon/entry removed; `backend/` preserved |
| Launch from the installed menu entry (`gtk-launch circuitlord-vrmodinstaller` in a throwaway `HOME`) | window `CircuitLord VR Mod Install unofficial-port for Linux`, 800×740, `IsViewable`; owner's `/proc/<pid>/exe` is the installed binary; first run cloned the `tf2vr-linux` backend into `$XDG_DATA_HOME/circuitlord-vrmodinstaller/backend` |
| `Exec` and `Icon` of that entry | `Exec` absolute and executable; GTK theme lookup resolved `circuitlord-vrmodinstaller` to the installed 48×48 PNG |
| Vulnerability audit (`dotnet list package --vulnerable --include-transitive`) | none |

## Known limitations

- `linux-x64` only; no arm64 build.
- No AppImage, Flatpak or distribution package. A sandboxed package would need verified access to
  the game directory and Steam configuration before it could be offered.
- CI builds and publishes the archive, but does not run the test suite yet; run the commands above
  locally before tagging.
- The packaged app is self-contained but not code-signed.
