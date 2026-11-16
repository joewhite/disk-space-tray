# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build / run

Windows-only .NET 10 tray app (WinForms + WPF, no tests or linter).

```bash
dotnet build
dotnet run
```

Use dev (Debug) builds unless told otherwise; they are the default for `dotnet build`/`dotnet run`. Debug builds automatically kill any running `DiskSpaceTray.exe` before building (the `KillRunningInstances` target in the csproj), so a running instance never causes a locked-file error. Release builds do not do this.

Settings are read from `appsettings.json` next to the exe (copied on build); see the `Settings` class in `Program.cs` for the keys.

## Architecture

- **Message loop is WPF's, not WinForms'.** `Program.Main` calls `ApplicationConfiguration.Initialize()` (sets PerMonitorV2 DPI before WPF starts; keep this ordering), then runs a `System.Windows.Application` with `OnExplicitShutdown`. Exit goes through `Application.Current.Shutdown()`. All timers are `DispatcherTimer`. WinForms is still used for `ContextMenuStrip`, `NativeWindow`, and GDI+ icon rendering.
- **`TrayIcon`** (`TrayIcon.cs`) wraps `Shell_NotifyIcon` directly with `NOTIFYICON_VERSION_4` instead of using WinForms `NotifyIcon`, because only v4 delivers hover (`NIN_POPUPOPEN`) and hover-exit (`NIN_POPUPCLOSE`). Consequences: callback message layout is the v4 one (event in `LOWORD(lParam)`, anchor point in `wParam`), `NIF_SHOWTIP` is deliberately not set so the shell shows no standard tooltip, and it uses a normal hidden window (not message-only) so it receives the `TaskbarCreated` broadcast.
- **`HoverPopup`** is a non-activating, click-through WPF window. It is positioned in physical pixels with `SetWindowPos` against the icon's monitor (DPI from `GetDpiForMonitor`) rather than WPF `Left`/`Top`, so it is correct on mixed-DPI setups. Don't switch it to DIP-based positioning.
- **`TrayContext`** (in `Program.cs`) ties it together: the refresh timer renders the free-space icon, and hover starts a short-lived timer that is only a safety net for a missed `NIN_POPUPCLOSE`.
  - Don't gate showing the popup on the cursor being inside `Shell_NotifyIconGetRect`: the shell's hover area is larger than that rect (notably in the overflow flyout), and a strict check silently drops valid hovers. The safety-net check uses a deliberately inflated rect for the same reason.
- **Win32 calls use CsWin32.** Add new APIs to `NativeMethods.txt` and call them via `PInvoke.*`; don't hand-write `DllImport`s. The project targets `x64` and `net10.0-windows10.0.19041.0` because CsWin32 can't generate `Shell_NotifyIcon` for AnyCPU, and the Windows 10 TFM avoids platform-compat warnings.
- With both `UseWPF` and `UseWindowsForms` enabled, WPF and WinForms/System.Drawing type names collide (`Size`, `Point`, `SystemColors`, `Application`, ...), so WPF types are fully qualified in code that also imports `System.Drawing`. `System.IO` is not an implicit using and must be imported explicitly.
