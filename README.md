# Copilot CLI Session Notifier

A Windows system tray application that monitors [GitHub Copilot CLI](https://githubnext.com/projects/copilot-cli/) sessions and shows popup notifications when sessions need your attention.

## Features

- **🔔 Real-time notifications** — Get notified when a Copilot CLI session:
  - ⏳ Finishes its turn and waits for your input
  - ✅ Completes and shuts down
  - 🔔 Finishes a background task
- **📌 Persistent popups** — Notifications stay in the lower-right corner until you dismiss them
- **🖱️ Click to focus** — Click a notification to bring the terminal window to the foreground
- **🌙 Light & dark theme** — Automatically matches your Windows theme setting
- **⚡ Auto-start** — Optional Windows startup registration
- **📋 Active sessions list** — Right-click the tray icon to see all running sessions

## How It Works

The app monitors `~/.copilot/session-state/*/events.jsonl` files using a combination of:
- **FileSystemWatcher** for instant detection of new events
- **Periodic polling** (every 3 seconds) as a reliable fallback

Only new events are processed — the app tracks file read positions and never re-notifies on the same event.

## Requirements

- Windows 10/11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or SDK to build from source)
- GitHub Copilot CLI installed and used (creates the session-state directory)

## Build & Run

```powershell
# If MSBuildSDKsPath is set to an older SDK, clear it first
$env:MSBuildSDKsPath = $null

# Build
dotnet build CopilotNotifier\CopilotNotifier.csproj

# Run
dotnet run --project CopilotNotifier\CopilotNotifier.csproj
```

## Publish as Standalone Executable

```powershell
$env:MSBuildSDKsPath = $null
dotnet publish CopilotNotifier\CopilotNotifier.csproj -c Release -r win-x64
```

The executable will be in `CopilotNotifier\bin\Release\net10.0-windows\win-x64\publish\`.

## Settings

Right-click the tray icon → **Settings** (or double-click the icon):

| Setting | Description |
|---|---|
| Notify on waiting for input | Show popup when agent finishes its turn |
| Notify on session complete | Show popup when session shuts down |
| Notify on task complete | Show popup when a background task finishes |
| Play notification sound | Play Windows notification sound |
| Start with Windows | Register in Windows startup (HKCU Run key) |

Settings are saved to `%APPDATA%\CopilotNotifier\settings.json`.

## Architecture

```
CopilotNotifier/
├── Models/
│   ├── SessionEvent.cs       # Parsed event from events.jsonl
│   ├── SessionInfo.cs        # Tracked session state
│   └── NotificationItem.cs   # Notification display data
├── Services/
│   ├── EventParser.cs        # JSON line parser for events.jsonl
│   ├── SessionWatcher.cs     # FileSystemWatcher + poll timer
│   ├── SessionStateTracker.cs# Track sessions, detect events, dedup
│   ├── WindowFocusService.cs # Win32 P/Invoke to focus terminal
│   ├── AutoStartService.cs   # Windows registry startup management
│   ├── ThemeService.cs       # System light/dark theme detection
│   └── AppSettings.cs        # Settings persistence
├── UI/
│   ├── NotificationPopup.xaml # Custom notification window
│   ├── NotificationManager.cs # Popup stacking and lifecycle
│   └── SettingsWindow.xaml    # Settings dialog
├── App.xaml                   # Application entry point
└── Resources/
    └── app.ico                # Tray icon
```
