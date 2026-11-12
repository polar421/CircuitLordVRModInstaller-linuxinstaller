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
sha256sum -c artifacts/circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz.sha256
tar -tzf artifacts/circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz | head
```

`build-release.sh` reads the version from the application project, so bumping `<Version>` in
`src/CircuitLord.VRModInstaller.App/CircuitLord.VRModInstaller.App.csproj` renames the artifacts.

## What the archive contains

```
circuitlord-vrmodinstaller-1.0.0-linux-x64/
  app/                                   self-contained publish (no .NET runtime needed)
    CircuitLordVRModInstaller            native ELF apphost
    *.dll, libSkiaSharp.so, …            Avalonia and .NET runtime files
  icons/hicolor/<16…256>/apps/circuitlord-vrmodinstaller.png
  circuitlord-vrmodinstaller.desktop.in  template with @EXEC@ placeholder
  install.sh                             user-level install / --uninstall
  SHA256SUMS
  LICENSE
  THIRD-PARTY-NOTICES.md
  README.md
```

No game files, mod packages, Proton builds, downloads or credentials are included. The build
script fails if it finds `*.zip`, `*.pak`, `*.vpk`, `*.exe`, `BepInEx*`, `Northstar*`,
`Titanfall2*` or `Titanfall2VR*` anywhere in the staged tree.

## Install and uninstall

```bash
tar -xzf circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz
./circuitlord-vrmodinstaller-1.0.0-linux-x64/install.sh

# later
./circuitlord-vrmodinstaller-1.0.0-linux-x64/install.sh --uninstall
```

Before writing anything, `install.sh` verifies every packaged file against the archive's
`SHA256SUMS` (231 entries for 1.0.0) and prints `Verified <n> files against SHA256SUMS`. It aborts
with a non-zero exit and a clear message if a checksum mismatches, if the list contains an absolute
or `..` path, or if `SHA256SUMS` is missing or empty. Nothing is created in the home directory on
those failure paths.

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
- Avalonia `11.3.9`; `Tmds.DBus.Protocol` pinned to the patched `0.21.3` (CVE-2026-39959 backport)

Deterministic output:

- `Deterministic=true`, `ContinuousIntegrationBuild=true`, no PDBs in the package
- archive written with `tar --sort=name --owner=0 --group=0 --numeric-owner --mtime=@SOURCE_DATE_EPOCH`
  (`SOURCE_DATE_EPOCH` defaults to the last commit time)

Measured on 2026-11-12: two consecutive clean builds produced the byte-identical archive

```
fb8a07fa8b22816da5c2ba260ed9a5e7e4f8f4869f4e6fd302b972ca469d212b  circuitlord-vrmodinstaller-1.0.0-linux-x64.tar.gz
```

Build is reproducible for the same commit, SDK and package sources; a NuGet package republished
with different content would change the hash, which is the intended detection.

## Verified for this release

| Check | Result |
|---|---|
| `dotnet build -c Release` | succeeds, 0 warnings |
| `dotnet test -c Release` | 33/33 pass |
| Two clean package builds | identical SHA-256 |
| Archive contents guard | no game/mod payload |
| `desktop-file-validate` on the shipped menu entry | valid |
| Install into a throwaway `HOME` | 231 files verified, app, symlink, 7 icon sizes, menu entry created |
| Tampered archive (one byte changed) | install aborted before any write, clear integrity message |
| Absolute path injected into `SHA256SUMS` | install aborted, path rejected |
| Missing/empty `SHA256SUMS` | install aborted |
| `--uninstall` | app/icon/entry removed; `backend/` preserved |
| Vulnerability audit (`dotnet list package --vulnerable --include-transitive`) | none |

## Known limitations

- `linux-x64` only; no arm64 build.
- No AppImage, Flatpak or distribution package. A sandboxed package would need verified access to
  the game directory and Steam configuration before it could be offered.
- No CI pipeline; the commands above are the manual release procedure.
- The packaged app is self-contained but not code-signed.
