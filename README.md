# VirtualDesktopSwitcher: UI Automation fork

> **This is the PowerShell / UI Automation fork**, a self-contained alternative
> to the main project, which uses in-process injection. For the standard
> backend, switch to the [`main`](../../tree/main) branch.

Rotates the **Preferred Codec** in Virtual Desktop Streamer on a timer, using
nothing but a PowerShell script.


## Why this fork exists

This fork does the same job a different way: it drives the Streamer's own
settings window through **Windows UI Automation**, the same accessibility layer
a screen reader uses. It opens the **OPTIONS** tab, finds the **Preferred
Codec** dropdown, and selects the codec for you.

## Requirements

- Windows
- PowerShell 5.1 or newer
- Virtual Desktop Streamer installed, with its settings window available
- Run at the same elevation level as the Streamer

> **Started with Windows?** If Virtual Desktop Streamer launched automatically at
> sign-in, the script may not be able to reach it. Fully exit the Streamer (tray
> icon → Exit) and reopen it manually before running the script.

## Quick start

From a folder containing `VirtualDesktopSwitcher.ps1`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\VirtualDesktopSwitcher.ps1
```

With no codec arguments, the script reads the current codec and opens a small
popup asking which different codec to toggle with, and how many minutes to wait
between switches (default `25`, range `1`–`10080`).

## Usage

```powershell
# Interactive setup, but start the timer at 15 minutes
.\VirtualDesktopSwitcher.ps1 -IntervalMinutes 15

# Explicit toggle pair on a 25-minute timer
.\VirtualDesktopSwitcher.ps1 -Codecs HEVC10bit,HEVC -IntervalMinutes 25

# Switch once and exit
.\VirtualDesktopSwitcher.ps1 -Codecs HEVC10bit,HEVC -Once

# Switch immediately, then keep rotating on the timer
.\VirtualDesktopSwitcher.ps1 -Codecs HEVC10bit,HEVC -SwitchImmediately

# Set one exact codec and exit
.\VirtualDesktopSwitcher.ps1 -TargetCodec HEVC10bit
```

### Parameters

| Parameter | Description |
|---|---|
| `-Codecs <list>` | Comma-separated codec rotation list. Skips the popup. |
| `-IntervalMinutes <n>` | Minutes between switches. Default `25`, range `1`–`10080`. |
| `-TargetCodec <codec>` | Set one exact codec and exit. |
| `-Once` | Switch to the next codec once and exit. |
| `-SwitchImmediately` | Switch immediately before entering the timer loop. |
| `-StreamerPath <path>` | Path to `VirtualDesktop.Streamer.exe` (used to launch it if not running). |
| `-UiTimeoutSeconds <n>` | How long to wait for the Streamer window. Default `15`. |

## Codec codes

`Automatic`, `H264`, `H264Plus`, `HEVC`, `HEVC10bit`, `AV110bit`

In the interactive popup the currently selected codec is hidden, because the
toggle target must differ from the current selection. Some GPUs or headsets do
not support every codec.

## Notes

- This project is not affiliated with Virtual Desktop, Inc.

## License

MIT. See [LICENSE](LICENSE).
