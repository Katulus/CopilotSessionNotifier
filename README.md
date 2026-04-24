# Copilot CLI Session Notifier

A Windows system tray application that monitors [GitHub Copilot CLI](https://githubnext.com/projects/copilot-cli/) sessions and shows popup notifications when sessions need your attention.

**_Note:_** This application was fully vibecoded.

## Features

- **🔔 Real-time notifications** — Get notified when a Copilot CLI session:
  - ⏳ Finishes its turn and waits for your input
  - ✅ Completes and shuts down
  - 🔔 Finishes a background task
- **📌 Persistent popups** — Notifications stay in the lower-right corner until you dismiss them
- **🖱️ Click to focus** — Click a notification to bring the terminal window to the foreground
- **🧹 Auto-dismiss on focus** — Once a session's terminal is focused (via click or manually), other pending notifications for that session are dismissed
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
# Build
dotnet build CopilotNotifier\CopilotNotifier.csproj

# Run
dotnet run --project CopilotNotifier\CopilotNotifier.csproj
```

## Publish as Standalone Executable

```powershell
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
| Notify when a tool is waiting for approval | Show popup when a tool stays unresolved for the delay below, indicating Copilot is asking to approve it |
| Detect after _N_ seconds | How long to wait after a tool starts before treating it as approval-pending |
| Play notification sound | Play Windows notification sound |
| Auto-dismiss when session terminal is focused | If the session's terminal is already focused when an event fires, show the popup as transient instead of persistent |
| Dismiss after _N_ seconds | How long a transient (focused-terminal) popup stays on screen. `0` = persists until clicked or closed |
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
