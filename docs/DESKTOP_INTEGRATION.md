# WiVRn desktop integration

## Canonical entry

| Property | Value |
|---|---|
| File | `${XDG_DATA_HOME:-~/.local/share}/applications/tf2vr.desktop` |
| Name | `Titanfall 2 VR` |
| Exec | installed launcher `~/.local/bin/tf2vr` |
| Icon | `steam_icon_1237970` when Steam's icon is present, otherwise `applications-games` |
| Terminal | `false` |
| Categories | `Categories=Game;X-WiVRn-VR;` exactly |

The backend's own template (`tf2vr.desktop` in the checkout) supplies Name, Comment, Keywords and
GenericName; the manager substitutes `@BIN@`/`@ICON@`, replaces the `Categories=` line and adds a
managed marker comment. The same canonical file is reused rather than creating a second launcher.

## Why reconciliation is required

`install.sh` regenerates `tf2vr.desktop` — verified at commit 8b02b87 — with `Categories=Game;`,
which does not contain the WiVRn category. After every successful backend install/update the app
rewrites the canonical entry, so a backend regeneration cannot silently remove WiVRn discovery.

## Behaviour

- Writes are atomic: temporary file in the same directory, mode `0644`, rename over target.
- If the existing entry was not created by this app, it is copied to
  `tf2vr.desktop.bak-<timestamp>` before replacement.
- `desktop-file-validate` is run when present; a missing validator is not treated as a failure.
- `GetStatus()` checks file existence, exact category line, executable `Exec` target, validator
  result and duplicate entries that also launch `tf2vr`.
- Repair rewrites the canonical entry without reinstalling the mod.
- Uninstall removal deletes only a file carrying this app's managed marker.
- Duplicate detection reports other `.desktop` files named "Titanfall 2 VR" or launching `tf2vr`;
  the app does not delete them automatically.

## Verified by tests

Exact category line, XDG destination, Exec target, `Terminal=false`, repair after a simulated
backend regeneration, missing-launcher reporting, path-with-spaces rendering. Real WiVRn
discovery (the headset-side picker) has **not** been tested.
