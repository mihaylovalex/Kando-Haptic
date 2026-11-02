<p align="center"><img src="docs/images/icon.png" width="128" alt="Kando Haptics"></p>

# Kando Haptics

**English** · [Русский](README.ru.md)

Haptic feedback for the [Kando](https://kando.menu/) pie menu on the **Logitech MX Master 4**.
Open your usual Kando menu and the mouse answers with a light click when you move across
sectors, a distinct pulse when you enter a submenu and a confirmation when you select an item.

* Runs inside Logi Plugin Service as a regular Logi Actions SDK plugin — no extra EXE, no
  background process, nothing to assign to mouse buttons.
* Finds Kando automatically through `%APPDATA%\kando\ipc-info.json` (no fixed port) and
  reconnects after Kando restarts.
* Uses only official APIs: the Kando WebSocket IPC API v2 and `PluginEvents.RaiseEvent()` of
  the Logi Actions SDK (`HasHapticMapping`). No HID++, no process injection, no changes to
  Kando, its themes or your menus.
* Effects, rate limits and per-event switches live in `settings.json` and apply instantly.

## Requirements

| | |
|---|---|
| OS | Windows 11 x64 |
| Logi Options+ | with Logi Plugin Service 6.x (tested: Options+ 2.9.4631, LPS 6.4.2.3414) |
| Kando | 3.0 or newer (IPC API v2; tested with 3.0.0-beta1) |
| Mouse | Logitech MX Master 4 |

## Install

1. Download `KandoHaptics_1_0_0.lplug4` from the
   [latest release](../../releases/latest).
2. Double-click it — Logi Plugin Service installs the plugin.
3. Make sure Kando is running. The plugin connects within 1–10 seconds.
4. Open a Kando menu and move across the sectors.

> [!IMPORTANT]
> **Windows user names with non-Latin characters (e.g. Cyrillic).** Logi Plugin Service
> passes Logi Options+ only the *path* to the plugin's `events\extra` folder, and Options+
> fails to read `eventMapping.yaml` when that path contains non-ASCII characters. Kando
> events still reach the plugin, but the mouse does not vibrate. The plugin writes a warning
> to its log in this case. Fix: run the plugin from a folder with an ASCII path:
>
> ```powershell
> powershell -ExecutionPolicy Bypass -File build\build.ps1 -LinkTo C:\ProgramData\KandoHaptics
> ```
>
> This builds the plugin, copies it to `C:\ProgramData\KandoHaptics`, creates
> `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\KandoHaptics.link` and reloads the plugin.
> Restart Logi Options+ if there is no vibration right after the first link.

Details: [docs/INSTALLATION.md](docs/INSTALLATION.md) (Russian).

## What you feel

| Kando event    | Default effect       | On  | Priority   |
|----------------|----------------------|:---:|------------|
| `openMenu`     | `damp_state_change`  | yes | transition |
| `hoverButton`  | `subtle_collision`   | yes | navigation |
| `hoverSubmenu` | `subtle_collision`   | yes | navigation |
| `openSubmenu`  | `sharp_state_change` | yes | transition |
| `closeSubmenu` | `damp_collision`     | yes | transition |
| `selectButton` | `completed`          | yes | selection  |
| `hoverCenter`, `hoverParent` | —      | no  | navigation |
| `activateMenu` | —                    | no  | selection  |
| `closeMenu`    | —                    | no  | transition |
| `scrollUp`, `scrollDown` | —          | no  | navigation (in Kando's source, not yet in the IPC docs) |

The feedback is meant to stay calm during long use:

* **No repeats** — hovering the same item again (same type and `path`) does not vibrate.
* **70 ms cooldown** between hover clicks. If a sector was skipped by the cooldown and the
  cursor stays on it, it clicks once the cooldown ends, so the sector you land on is always felt.
* **Boundary jitter suppression** — flicking A→B→A between neighbours within 100 ms gives
  one click, not a burst.
* **Priorities** — for 90 ms after a transition or selection, less important effects are
  dropped: opening a submenu and hovering its first child (Kando sends both at once) give a
  single pulse. `selectButton` is never rate limited.
* **Menu state** — `openMenu` starts a fresh session, `closeMenu` clears it and cancels pending
  clicks; depth and path are tracked through submenus and recovered from hover paths if an
  event was missed.

## Configuration

`%LOCALAPPDATA%\KandoHaptics\settings.json` is created on first start (template:
[config/settings.json](config/settings.json)). No admin rights needed; changes apply about
0.25 s after saving. Invalid JSON keeps the last working configuration. `"enabled": false`
turns all vibration off.

```json
{
  "enabled": true,
  "navigationCooldownMs": 70,
  "navigationDebounceMs": 100,
  "transitionGuardMs": 90,
  "trailingHoverFeedback": true,
  "logLevel": "Info",
  "events": {
    "hoverButton": { "enabled": true, "waveform": "subtle_collision" }
  }
}
```

Available waveforms: `sharp_state_change`, `damp_state_change`, `sharp_collision`,
`damp_collision`, `subtle_collision`, `happy_alert`, `angry_alert`, `completed`, `square`,
`wave`, `firework`, `mad`, `knock`, `jingle`, `ringing`.

Vibration strength is set in Logi Options+ (device → haptic feedback). The SDK offers no API
for amplitude, so the plugin does not change it.

**Why 15 `waveform_*` events?** Logi Plugin Service reads `events/extra/eventMapping.yaml`
only when the plugin loads. The plugin therefore declares one event per official waveform
(`waveform_subtle_collision`, `waveform_completed`, …) and raises whichever one
`settings.json` selects — effects change without rebuilding or restarting.

Full reference: [docs/CONFIGURATION.md](docs/CONFIGURATION.md) (Russian).

## Troubleshooting

* Plugin log: `%LOCALAPPDATA%\KandoHaptics\Logs\KandoHaptics.log` (1 MB × 3 rotation).
  Set `"logLevel": "Debug"` to see every Kando event and the decision taken for it
  (`Played`, `Duplicate`, `Deferred`, `Guarded`, …).
* Logi Plugin Service log for the plugin:
  `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\KandoHaptics.log`.
* Events show `Played` but nothing vibrates → see the non-ASCII path note above, and check
  that haptic feedback is enabled for MX Master 4 in Logi Options+.

More: [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) (Russian, includes a manual test checklist).

## Building from source

Requires the .NET 10 SDK and an installed Logi Options+ (the build references
`C:\Program Files\Logi\LogiPluginService\PluginApi.dll`; override with `LOGI_PLUGIN_API_DIR`).
Keep the repository on a short path such as `C:\src\KandoHaptics` — very long paths can hit
the Windows 260-character limit.

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

The script checks the SDK and dependencies, builds Release with warnings as errors, runs the
tests, packs `dist\KandoHaptics_<version>.lplug4` with the official LogiPluginTool
(`pack` + `verify`) and checks the archive. Options: `-Configuration Debug`, `-SkipTests`,
`-Install`, `-LinkTo <folder>`, `-Uninstall`.

Tests only (no mouse or Logi Options+ needed — they use a mock haptic provider and a local
fake Kando WebSocket server):

```powershell
dotnet test tests\KandoHaptics.Tests\KandoHaptics.Tests.csproj
```

## Project layout

```text
src/KandoHaptics.Core/      core without Logi SDK dependencies (fully testable)
  Ipc/                      ipc-info.json, message parsing, WebSocket client
  Processing/               menu state and event filtering
  Haptics/                  waveform catalog, IHapticProvider, HapticEngine
  Configuration/            settings.json model, validation, hot reload
  Logging/                  rotating file log
src/KandoHaptics.Plugin/    Logi Actions SDK plugin (Load/Unload, provider, optional actions)
  package/                  LoupedeckPackage.yaml, events/*.yaml, icon
tests/KandoHaptics.Tests/   xUnit tests
build/build.ps1             build, test, pack, install, link
docs/                       installation, configuration, troubleshooting, dev report (Russian)
```

## Privacy and security

* Connects only to `127.0.0.1`; the host is never read from files and the system proxy is bypassed.
* Opens no network ports and never executes anything from incoming messages.
* Logs contain only interaction types and numeric paths — no menu item names or commands
  (the plugin never receives them).

## License

[MIT](LICENSE).

Kando is a project by Simon Schneegans and contributors; Logitech, Logi Options+ and
MX Master are trademarks of Logitech. This plugin is an independent community project and
is not affiliated with either.
