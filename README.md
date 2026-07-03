# VirtualDesktopSwitcher

Automatically switches the **Preferred Codec** in Virtual Desktop Streamer on a
timer silently, without bringing the Streamer settings window to the
foreground.

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
switches, and how many minutes to wait
between switches (default `25`, range `1`–`10080`).

Prefer to build it yourself? See [Building from source](#building-from-source).

## Usage

```powershell
# Interactive setup, but start the timer at 15 minutes
.\VirtualDesktopSwitcher.exe --interval-minutes 15

# Explicit toggle pair on a 25-minute timer
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --interval-minutes 25

# Beep during the final 5 seconds before each timer switch
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --interval-minutes 25 --beep-warning

# Switch once and exit
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --once

# Switch immediately, then keep rotating on the timer
.\VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --switch-immediately

# Set one exact codec and exit
.\VirtualDesktopSwitcher.exe --target-codec HEVC10bit

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
| `--streamer-path <path>` | Path to `VirtualDesktop.Streamer.exe` (used to launch it if not running). |
| `--timeout-seconds <n>` | Helper timeout in seconds. Default `15`, range `1`–`300`. |

## Codec codes

`Automatic`, `H264`, `H264Plus`, `HEVC`, `HEVC10bit`, `AV110bit`

The interactive popup hides the currently selected codec, since the toggle
target must differ from the current selection. Some GPUs or headsets do not
support every codec.

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
src\VdCodecRotator.Payload\PayloadEntryPoint.cs   EasyHook entry point
src\VdCodecRotator.Worker\StreamerCodecWorker.cs  reads/sets the live setting
build.ps1                                         builds and packages the release
test-package.ps1                                  verifies the packaged release
.github\workflows\                                CI build and tagged releases
```

## Notes

- This project is not affiliated with Virtual Desktop, Inc.
- The selected toggle codec only needs to be different from the current one.
- Release packages bundle third-party notices for EasyHook and UDIS86 in
  [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

## License

MIT. See [LICENSE](LICENSE).
