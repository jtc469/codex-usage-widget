# Codex Usage Widget

A small Windows 11 taskbar overlay showing the remaining Codex five-hour and seven-day allowances. I built this with Codex & Claude.

Too many tabs open may cause the widget to overlay on the search bar.

## Build

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). From `source/`:

```
dotnet build -c Release -r win-arm64 --self-contained false
```

Swap `win-arm64` for `win-x64` on an Intel or AMD machine. The build lands in
`source/bin/Release/net9.0-windows/<rid>/`.

## Run

Launch `CodexUsageWidget.exe` from the build output. The widget appears in the unused area at the lower-left of the primary taskbar. It is click-through, so it does not interfere with the taskbar.

Use the blue information icon in the system tray to:

- refresh immediately;
- enable or disable **Start with Windows**;
- exit the widget.

The display refreshes every minute. After repeated failures it backs off to 1, 2, 4, 8, then 15 minutes; a successful read restores the one-minute interval. **Refresh now** always retries immediately. Green means more than 50% remains, amber means 21–50%, and red means 20% or less.

## Requirements and behavior

- Windows 11 and the Codex desktop app/CLI signed into a ChatGPT account.
- .NET 9 Desktop Runtime (already present on the machine this build targets).
- The widget asks the locally installed `codex app-server` for `account/rateLimits/read`. It does not read, copy, or store account tokens.
- The app-server runs inside a Windows kill-on-close Job Object, so Windows terminates it if the widget crashes or is killed.
- This is an overlay above the taskbar, because Windows 11 does not expose a supported API for third-party widgets inside the taskbar itself.

## Remove

Exit from the tray icon. If **Start with Windows** is enabled, turn it off first, then delete the folder.
