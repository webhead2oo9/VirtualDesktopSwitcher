# VirtualDesktopSwitcher

Automatically or manually switches the **Preferred Codec** in Virtual Desktop
Streamer without bringing the Streamer settings window to the foreground. A
global keyboard chord or discrete wheel/controller button can switch codecs or
restart the stream encoder even while the simulator has focus.

> **Prefer no injection?** A self-contained PowerShell variant that drives the
> Streamer UI through Windows UI Automation lives on the
> [`uia-powershell`](../../tree/uia-powershell) branch.

## Why this exists

There's a known issue on Meta headsets where streaming **FPS drops after roughly
20–30 minutes** of continuous play. Changing the codec forces the encode
pipeline to reinitialize, which clears the drop and restores smooth frames.

Doing that by hand every half hour sucks, or is just not feasible for sim racing. This tool automates the
workaround: it toggles between two codecs (or rotates a list) on an interval, so
the stream resets on its own before the slowdown sets in. The default interval
is **25 minutes**; if you still see drops (some people experience at like 15 minutes?) lower it. It's a workaround until Meta fixes the garbage that is the Quest OS.

## How it works

The rotator sets the codec from *inside* the Streamer process, writing the same
live managed setting the Streamer UI itself uses:

```text
VirtualDesktop.Streamer.StreamerSettings.Default.PreferredCodec
```

Because it sets the live setting directly, the switch takes effect immediately
and the Streamer window never has to come to the foreground. Each switch:

1. `VirtualDesktopSwitcher.exe` injects `VdCodecPayload.dll` into `VirtualDesktop.Streamer.exe` (via EasyHook).
2. The payload loads `VdCodecWorker.dll` into Streamer's default AppDomain.
3. The worker reads or sets `StreamerSettings.Default.PreferredCodec` and writes the result back to the rotator.

This in-process approach is resilient across Streamer versions as long as that
managed setting path stays stable. Tested against Virtual Desktop Streamer
`1.34.18` with EasyHook `2.7.7097`.

## Requirements

- Windows (x64)
- Virtual Desktop Streamer installed, and running or startable
- The rotator must run at the **same elevation level** as the Streamer

> **Started with Windows?** If Virtual Desktop Streamer launched automatically at
> sign-in, the rotator may not be able to reach it. Fully exit the Streamer (tray
> icon → Exit) and reopen it manually before running the rotator.

> **Heads up:** because this uses process injection, antivirus or SmartScreen
> may flag unsigned builds. If you'd rather not inject at all, use the
> [UI Automation fork](../../tree/uia-powershell).

## Quick start

Download a release, unzip it, and run the executable from that folder:

```powershell
.\VirtualDesktopSwitcher.exe
```

With no codec arguments, it reads the current codec and opens a small popup
asking which different codec to toggle with, whether to beep before timer
switches, how many minutes to wait between switches (default `25`, range
`1`–`10080`), and whether to enable a manual trigger.

To configure the manual trigger without starting or requiring Virtual Desktop
Streamer, run:

```powershell
.\VirtualDesktopSwitcher.exe --configure-manual
```

Choose **Bind**, then press one non-modifier keyboard key (optionally with Ctrl,
Alt, Shift, or Windows) or one discrete wheel/controller button. The setting is
global: the trigger remains active while another application has focus.

Prefer to build it yourself? See [Building from source](#building-from-source).

## Usage

```powershell
# Interactive setup, but start the timer at 15 minutes
.\VirtualDesktopSwitcher.exe --interval-minutes 15

# Explicit toggle pair on a 25-minute timer
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --interval-minutes 25

# Beep during the final 5 seconds before each timer switch
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --interval-minutes 25 --beep-warning

# Keep H.264+ active, briefly switching to H.264 to reset the stream every 25 minutes
.\VirtualDesktopSwitcher.exe --codecs H264Plus,H264 --interval-minutes 25 --switch-back

# Same, but stay on H.264 for 500 ms before switching back
.\VirtualDesktopSwitcher.exe --codecs H264Plus,H264 --interval-minutes 25 --switch-back --switch-back-delay-ms 500

# Switch once and exit
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --once

# Switch immediately, then keep rotating on the timer
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --switch-immediately

# Set one exact codec and exit
.\VirtualDesktopSwitcher.exe --target-codec HEVC10bit

# Capture/save the global trigger and timer preference, then exit
.\VirtualDesktopSwitcher.exe --configure-manual

# Wait only for the saved manual binding; do not run the automatic timer
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --manual-only

# Temporarily override the saved manual action
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --manual-action switch

# Force the timer on or disable the saved trigger for this run
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --enable-timer --no-manual-trigger

# Show help
.\VirtualDesktopSwitcher.exe --help
```

### Options

| Option | Description |
|---|---|
| `--codecs <list>` | Comma-separated codec rotation list. Skips the popup. |
| `--interval-minutes <n>` | Minutes between switches. Default `25`, range `1`–`10080`. |
| `--beep-warning` | Beep during the final 5 seconds before each timer switch. |
| `--no-beep-warning` | Keep timer switches silent. Default. |
| `--target-codec <codec>` | Set one exact codec and exit. |
| `--once` | Switch to the next codec once and exit. |
| `--switch-immediately` | Switch immediately before entering the timer loop. |
| `--switch-back` | Treat the first of exactly two codecs as preferred. At each interval, switch to the second codec, wait for the switch-back delay, and restore the first. The preferred codec is also restored when the tool starts. |
| `--switch-back-delay-ms <n>` | Milliseconds to stay on the temporary codec before restoring the preferred one. Default `50`, range `0`–`60000`. |
| `--configure-manual` | Open manual/timer settings, save them, and exit without requiring Streamer. |
| `--manual-only` | Disable automatic timer switching for this run. Requires a saved binding. |
| `--enable-timer` | Force automatic timer switching on for this run. |
| `--manual-action <switch\|restart>` | Override the saved manual action for this run. |
| `--no-manual-trigger` | Disable the saved manual binding for this run. |
| `--streamer-path <path>` | Path to `VirtualDesktop.Streamer.exe` (used to launch it if not running). |
| `--timeout-seconds <n>` | Helper timeout in seconds. Default `15`, range `1`–`300`. |

## Codec codes

`Automatic`, `H264`, `H264Plus`, `HEVC`, `HEVC10bit`, `AV110bit`

The interactive popup hides the currently selected codec, since the toggle
target must differ from the current selection. Some GPUs or headsets do not
support every codec.

With `--switch-back`, specify exactly two codecs. The first is the preferred
codec and the second is only used temporarily to reset the stream. For example,
`--codecs H264Plus,H264 --switch-back` keeps H.264+ selected between resets.
The interactive popup offers the same behavior: check *Switch back to*, pick
the codec to keep (it defaults to the current one), and pick the delay in
milliseconds.

## Manual trigger

Manual settings are stored in the versioned file
`%LOCALAPPDATA%\VirtualDesktopSwitcher\manual-settings.json`. Updates use an
atomic file replacement. If the file is missing, malformed, or from an
unsupported version, the program warns and uses safe defaults: timer enabled,
manual trigger unbound, **Restart stream**, and a `50 ms` restart delay.

The effective setting order is command-line override, saved setting, then
default. Runs with an explicit `--codecs` list automatically use the saved
binding. `--once` and `--target-codec` are one-shot modes and ignore both timer
and manual settings.

Every manual action counts down `3`, `2`, `1` with one beep per second. If beep
playback is unavailable, the countdown continues. Press Ctrl+C to cancel it and
stop normally. Further key repeats or button reports are ignored during the
countdown and codec operation. A manual trigger that arrives during the
optional five-second timer warning takes priority; after a successful manual
action, the automatic interval starts over.

- **Restart stream** remembers the exact active codec, switches to the next
  different codec in the configured rotation, waits the saved restart delay,
  and restores the remembered codec. This resets the encoder; it does not exit
  Virtual Desktop Streamer.
- **Switch codec** advances through the same configured rotation used by the
  timer.

### Controller support and troubleshooting

The listener uses Windows Raw Input in background mode and registers keyboard,
joystick, gamepad, and multi-axis controller collections. HID bindings store
only the button usage page and usage number, not a vendor, product, or device
identifier. Consequently, the equivalent reported button on another supported
controller can activate the same binding.

Only discrete HID buttons can be bound. Axes, pedals, analog paddles, hats that
are reported solely as axes, and force-feedback data are not supported. If a
wheel button is not detected, check its driver/control panel to confirm that it
is exposed as a DirectInput-style button rather than an axis. Re-run
`--configure-manual` after changing the driver mapping.

If Raw Input cannot initialize, a timer-enabled run logs a warning and
continues with the timer. A manual-only run exits with a clear error because it
has no remaining trigger. If the application says both scheduling methods are
disabled or that manual-only mode has no binding, run `--configure-manual` and
bind a key/button, or use `--enable-timer`.

## Building from source

Generated binaries are intentionally kept out of git. The build script downloads
EasyHook from NuGet and compiles everything from source, no .NET SDK required,
just the .NET Framework compiler that ships with Windows.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Package
```

The script:

- downloads pinned EasyHook `2.7.7097` from NuGet
- compiles the .NET Framework x64 executable and helper DLLs using
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- writes the release folder under `dist\`
- writes `artifacts\VirtualDesktopSwitcher.zip` when `-Package` is passed

A packaged release contains:

```text
VirtualDesktopSwitcher.exe   VdCodecPayload.dll   VdCodecWorker.dll
EasyHook.dll   EasyHook64.dll   EasyLoad64.dll   EasyHook64Svc.exe
README.md   LICENSE   THIRD-PARTY-NOTICES.md
```

### Source layout

```text
src\VdCodecRotator\Program.cs                     CLI, timer loop, injection client
src\VdCodecRotator\ManualSettings.cs              JSON settings and configuration UI
src\VdCodecRotator\RawInput.cs                    background keyboard/HID input
src\VdCodecRotator\ToggleSelectionDialog.cs       interactive codec and scheduling setup
src\VdCodecRotator.Payload\PayloadEntryPoint.cs   EasyHook entry point
src\VdCodecRotator.Worker\StreamerCodecWorker.cs  reads/sets the live setting
build.ps1                                         builds and packages the release
test-package.ps1                                  verifies the packaged release
test-manual-features.ps1                          focused manual-trigger unit tests
.github\workflows\                                CI build and tagged releases
```

After building once, run the focused tests with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\test-manual-features.ps1
```

## Notes

- This project is not affiliated with Virtual Desktop, Inc.
- The selected toggle codec only needs to be different from the current one.
- Release packages bundle third-party notices for EasyHook and UDIS86 in
  [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

## License

MIT. See [LICENSE](LICENSE).
