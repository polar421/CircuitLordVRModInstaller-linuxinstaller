# OpenXR runtime support

The application never changes the active OpenXR runtime. It reads
`${XDG_CONFIG_HOME}/openxr/1/active_runtime.json` when present and reports the runtime name and
library path, plus whether WiVRn, Monado or SteamVR processes appear to be running.

A configured runtime file is **not** evidence that a headset is connected; `HeadsetConnected` is
deliberately always false, and no UI text claims otherwise.

## Launching

All three entry points — the **Launch in VR** button, the WiVRn `.desktop` entry and the Steam
shortcut — invoke the same installed launcher `~/.local/bin/tf2vr`. The launcher owns runtime
selection, Proton-TF2VR validation, the mmdevapi audio fix check and crash-monitor startup.
SteamVR is never forced; `OpenVR=0` is written for the shortcut because no VR runtime flag has
been verified for the native launcher.

## Per-runtime expectations

| Runtime | What the app does | What the user must do |
|---|---|---|
| SteamVR | Nothing beyond detection | Have SteamVR configured as the active OpenXR runtime and running |
| WiVRn | Installs the `X-WiVRn-VR` desktop entry | Start WiVRn and connect the headset before launching |
| Monado | Detection only | Start Monado and connect the headset |

## Test status

| Level | Status |
|---|---|
| Runtime JSON parsing | Unit-testable via `RuntimeService.Inspect`; not covered by an automated test yet |
| Real SteamVR session | Not tested |
| Real WiVRn session | Not tested |
| Real Monado session | Not tested |
| Headset launch through `tf2vr` | Not tested |

Documented limitation: the launcher itself decides how to start Proton and the game; this app
does not add flags or wrappers around it.
