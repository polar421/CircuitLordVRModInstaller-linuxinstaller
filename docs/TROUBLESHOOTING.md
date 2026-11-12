# Troubleshooting

Logs: `${XDG_STATE_HOME:-~/.local/state}/circuitlord-vrmodinstaller/logs/`
History: `.../history.json`. Steam transaction journals: `.../steam-transactions/`.

| Symptom | What it means | Next step |
|---|---|---|
| "Backend clone failed" | `git clone` of tf2vr-linux failed (network/disk) | Check connectivity; the previous checkout, if any, is untouched |
| "Existing checkout has a different origin" | The managed directory was created by something else | The app refuses to overwrite it; move it away manually |
| Catalogue is empty | `--list-versions` failed or printed an unknown format | Run **Update helper**, then **Refresh**; check the log for the backend message |
| Catalogue shows only newest releases | GitHub's release API was unavailable inside the backend | Retry later; older pinned versions return |
| "Refusing to install '<token>'" | The token is not in the validated catalogue | Refresh the catalogue and reselect |
| Backend check reports game not found | Titanfall 2 or its prefix was not detected | Start the game normally once, then re-run the check; pass `--game`/`--prefix` via the backend if using EA/Heroic/Lutris |
| Missing Proton-TF2VR | The helper was not built yet | Run an install; or **Update helper** and install again |
| French/desktop entry not in WiVRn | Category or Exec wrong, or the file was regenerated | **Repair WiVRn entry**; verify the exact category line in the file |
| Steam shortcut missing | Shortcut added to a different account, or Steam was running | Check the selected account shown in Integrations; retry with Steam running and confirm the restart dialog |
| "Steam did not close on its own" | Graceful shutdown did not complete within 90 s | Close Steam manually, then retry. The app never force-kills it and did not touch the VDF |
| "Steam did not reopen" | Launcher failed after the shortcut commit | Start Steam manually; the shortcut is preserved and a recovery journal remains |
| Launcher missing after install | Backend install did not finish | Re-run the install; check the operation log's exit code |
| VR launch fails immediately | Missing prefix/config, Proton-TF2VR or OpenVR bridge | Read `~/.local/state/tf2vr/launch.log`; the launcher prints the specific missing file |

Do not delete Proton prefixes, the whole Steam directory or the game directory as a first step.
Collect the operation log first.
