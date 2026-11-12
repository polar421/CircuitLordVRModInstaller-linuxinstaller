# Steam shortcut integration

## Shortcut

A separate non-Steam entry named **Titanfall 2 VR** that runs the native launcher
`~/.local/bin/tf2vr`. The ordinary Titanfall 2 Steam entry and its compatibility settings are
never read or written.

Fields written: `appid`, `AppName`, `Exe`, `StartDir`, `icon`, `ShortcutPath`, `LaunchOptions`,
`IsHidden`, `AllowDesktopConfig`, `AllowOverlay`, `OpenVR`, `Devkit`, `DevkitGameID`,
`LastPlayTime`, `tags`. No undocumented keys are invented. Unknown keys already in the file are
preserved by the parser/serializer.

Identity = exact name plus launcher path. A same-named entry pointing at another executable is a
conflict: the write is refused rather than overwriting somebody else's shortcut. AppID is derived
from CRC32(`Exe` + name) with the high bit set, stored as signed int32 as the VDF field requires.

## Transaction

`Idle → DetectingInstallation → CheckingActiveSessions → (AwaitingConfirmation) →
RequestingGracefulShutdown → WaitingForSteamExit → CreatingBackup → ReadingShortcuts →
UpdatingShortcutModel → WritingTemporaryFile → VerifyingCommittedShortcut →
(RestartingSteam → WaitingForSteamStartup → VerifyingSteamRestart) → Completed`

- Steam running requires explicit user confirmation in the UI before the manager is called with
  `allowSteamRestart: true`.
- Shutdown is requested with the launcher's `-shutdown` argument for native Steam, or
  `flatpak run com.valvesoftware.Steam -shutdown` for Flatpak. Process exit is observed by polling
  `/proc/*/cmdline` for the selected installation; a 90 s timeout applies.
- On timeout: no file write, no force kill, `ShutdownTimedOut` recorded, user offered retry.
- Before writing: timestamped backup `shortcuts.vdf.backup-YYYYMMDD-HHMMSS` (never overwritten).
- Write: same-directory temp file, `Flush(flushToDisk: true)`, re-parse, atomic rename, re-parse
  the committed file, confirm exactly one owned entry.
- Restart obligation: once Steam was shut down by this transaction, `RestartOwed` is persisted in
  the journal and a relaunch is attempted on **every** terminal path, including shortcut failure.
  Steam is verified by observing the client process, not by a zero exit code from the launcher.
- If Steam was already closed no restart is attempted; the file is updated safely and Steam stays
  closed.
- Journalled under `${XDG_STATE_HOME}/circuitlord-vrmodinstaller/steam-transactions/`; pending
  recoveries are reported in the log at startup instead of launching Steam silently.

## Test status

Fixture-tested (33 tests total, Steam subset): parse/serialize round trip, unknown-field
preservation, idempotent upsert, same-name conflict refusal, backup creation, running → close →
write → reopen verified, refusal to restart, shutdown timeout with no write, write failure followed
by successful reopen, reopen failure marked as recovery required, initially-closed behaviour,
owned-only removal.

**Not tested against a live Steam client.** No real `shortcuts.vdf` was modified during
development or testing.
