# Monitor Brightness Flyout

[![Release](https://img.shields.io/github/v/release/stonymahomie/monitor-brightness-flyout?label=release)](https://github.com/stonymahomie/monitor-brightness-flyout/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/stonymahomie/monitor-brightness-flyout/total?color=blue)](https://github.com/stonymahomie/monitor-brightness-flyout/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-yellow)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white)
![C#](https://img.shields.io/badge/C%23-.NET%20Framework%204.x-512BD4?logo=dotnet&logoColor=white)
![Dependencies](https://img.shields.io/badge/dependencies-none-brightgreen)
![Code size](https://img.shields.io/github/languages/code-size/stonymahomie/monitor-brightness-flyout)
[![Built with Claude Code](https://img.shields.io/badge/built%20with-Claude%20Code-D97757?logo=claude&logoColor=white)](https://claude.com/claude-code)

A tiny Windows tray app that controls the brightness of **each external monitor individually**, with a flyout that looks and feels like the Windows 11 quick settings.

Windows only has a brightness slider for built-in laptop screens. This tool talks to external monitors directly over the display cable using **DDC/CI**, so no on-screen-display buttons are needed.

<p align="center"><img src="docs/flyout.png" alt="Brightness flyout with one slider per monitor" width="500"></p>

## Features

- **Tray icon + Windows 11 style flyout:** rounded corners, translucent blurred background, slide-in animation and sliders in your Windows accent color
- **One slider per monitor** plus an "All monitors" slider; mouse wheel adjusts by ±2 %
- **Type an exact value:** click the percentage, type a number, press Enter
- **Plug and play:** monitors that are plugged in, unplugged or switched on are picked up automatically
- **Command line** for scripts, hotkeys and scheduled tasks
- **Start with Windows** from the tray menu
- **UI language follows Windows** (English, or German on German systems)
- **Zero dependencies:** a single ~35 KB `.exe`, built with the C# compiler that ships with Windows

## Download

Grab `BrightnessFlyout.exe` from the [latest release](https://github.com/stonymahomie/monitor-brightness-flyout/releases/latest) and run it. Nothing to install.

> The `.exe` is not code-signed, so Windows SmartScreen may warn on first launch. Click **More info → Run anyway**, or build it yourself (see below).

## Usage

Run `BrightnessFlyout.exe`. A sun icon appears in the notification area (on Windows 11 it may start hidden under the `^` arrow; drag it onto the taskbar to keep it visible).

| Action | Result |
|---|---|
| Left click on the icon | Open the flyout with the sliders |
| Mouse wheel over a slider | ±2 % |
| Click on the percentage | Type a value (0–100): `Enter` applies, `Esc` cancels, `↑`/`↓` ±1; clicking elsewhere also applies |
| `Esc` or click outside | Close the flyout |
| Right click on the icon | All to 100/75/50/25 %, re-detect monitors, start with Windows, exit |

Launching the `.exe` again while it runs just opens the flyout of the running instance.

### Command line

```
BrightnessFlyout.exe list        show monitors and current brightness
BrightnessFlyout.exe 2 50        set monitor 2 to 50 %
BrightnessFlyout.exe all 30      set all monitors to 30 %
BrightnessFlyout.exe 3 +10       monitor 3 10 % brighter (-10 = darker)
```

Monitors are numbered from left to right.

## Enabling DDC/CI

The app can only control monitors that have **DDC/CI** switched on. A monitor without it shows **"no DDC/CI"** and a locked slider, and the flyout shows a short hint. Click **×** to hide the hint for good; it only comes back when a monitor is connected that wasn't there when you dismissed it.

1. Open your monitor's own on-screen menu with the buttons or joystick on the monitor.
2. Look for an option called **DDC/CI**, **DDC**, **External control** or similar. It's usually under *System*, *Setup*, *Other* or *General*.
3. Turn it **on**.
4. Open the flyout again. The slider unlocks by itself, since the app asks every monitor again each time the flyout opens.

**Not every monitor supports DDC/CI.** Some models don't have the option at all, some only support it on certain inputs (e.g. DisplayPort but not HDMI, or the other way round), and some docking stations, adapters and KVM switches don't pass it through. If there's no such option, try a different cable or input; otherwise that monitor can only be dimmed with its own buttons.

## Requirements and limitations

- Windows 10 or 11 with .NET Framework 4.x (included in Windows)
- Monitors with DDC/CI turned on (see above)
- DDC/CI is slow (about 50–100 ms per command), so a value is only sent once a slider rests for a moment.
- Built-in laptop screens are not supported (they don't use DDC/CI).

## Building

Only needs Windows with .NET Framework 4.x (included in Windows 10 and 11):

```
build.cmd
```

This calls the built-in compiler (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), which only supports C# 5, so the code avoids `$"…"` strings and `?.`.

`BrightnessFlyout.exe --render preview.png [en-US]` saves the flyout as an image, handy for checking the look without opening it.

## How it works

- **Brightness:** `dxva2.dll` (`GetMonitorBrightness` / `SetMonitorBrightness`), the Windows API for DDC/CI
- **Monitor names:** EDID data via WMI (`WmiMonitorID`)
- **Plug and play:** `SystemEvents.DisplaySettingsChanged`, then the monitor list is reloaded after a 2 second delay
- **Flyout:** drawn entirely with GDI+ into a layered window (`UpdateLayeredWindow`), blur via `SetWindowCompositionAttribute`, accent color from `AccentPalette`
- **Autostart:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `BrightnessFlyout`
- **Dismissed hint:** hardware ids of the monitors connected at that moment, stored in `HKCU\Software\BrightnessFlyout`, value `HintDismissedFor`

## Built with Claude

Most of the work on this project was done by **[Claude](https://www.anthropic.com/claude)**, Anthropic's AI model, in [Claude Code](https://claude.com/claude-code): the code, the Windows 11 flyout design, the debugging and this README.

[stonymahomie](https://github.com/stonymahomie) had the idea, tested everything on a real three-monitor setup and steered the design.

## License

[MIT](LICENSE) © 2026 stonymahomie
