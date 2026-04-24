using System.IO;
using System.Windows;
using CopilotNotifier.Models;
using CopilotNotifier.Services;
using CopilotNotifier.UI;
using DrawingIcon = System.Drawing.Icon;
using WinForms = System.Windows.Forms;

namespace CopilotNotifier;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _trayIcon;
    private SessionStateTracker? _tracker;
    private SessionWatcher? _watcher;
    private NotificationManager? _notificationManager;
    private AppSettings? _settings;

    private static readonly string SessionStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "session-state"
    );

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeService.ApplyTheme(this);

        _settings = AppSettings.Load();
        _notificationManager = new NotificationManager();

        _tracker = new SessionStateTracker(SessionStatePath);
        _tracker.NotificationReady += OnNotificationReady;
        _tracker.PerformInitialScan();

        _watcher = new SessionWatcher(SessionStatePath, _tracker, Dispatcher);
        _watcher.Start();

        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = "Copilot Session Notifier"
        };

        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/app.ico");
            var iconStream = GetResourceStream(iconUri)?.Stream;
            if (iconStream != null)
                _trayIcon.Icon = new DrawingIcon(iconStream);
        }
        catch
        {
            _trayIcon.Icon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath!);
        }

        var menu = new WinForms.ContextMenuStrip();

        var activeSessionsMenu = new WinForms.ToolStripMenuItem("Sessions");
        activeSessionsMenu.DropDownOpening += (_, _) => RefreshActiveSessionsMenu(activeSessionsMenu);
        menu.Items.Add(activeSessionsMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var settingsItem = new WinForms.ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        var dismissAllItem = new WinForms.ToolStripMenuItem("Dismiss All Notifications");
        dismissAllItem.Click += (_, _) => _notificationManager?.DismissAll();
        menu.Items.Add(dismissAllItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowSettings();
    }

    private void RefreshActiveSessionsMenu(WinForms.ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();

        if (_tracker == null) return;

        var sessions = _tracker.Sessions.Values
            .Where(s => !s.IsShutdown)
            .OrderByDescending(s => s.Pid.HasValue) // active (has PID) first
            .ThenByDescending(s => s.LastEventTime)
            .Take(30)
            .ToList();

        bool addedActive = false;
        bool addedInactive = false;

        foreach (var session in sessions)
        {
            var pid = session.Pid;
            var name = session.DisplayName;

            if (pid.HasValue && !addedActive)
            {
                addedActive = true;
            }
            else if (!pid.HasValue && !addedInactive)
            {
                if (addedActive)
                    menu.DropDownItems.Add(new WinForms.ToolStripSeparator());
                addedInactive = true;
            }

            var item = new WinForms.ToolStripMenuItem(name);
            if (pid.HasValue)
            {
                item.Font = new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
                item.Click += (_, _) => WindowFocusService.FocusTerminalWindow(pid.Value, name);
            }
            else
            {
                item.Enabled = false;
                item.Text += " (no terminal)";
            }
            menu.DropDownItems.Add(item);
        }

        if (sessions.Count == 0)
        {
            menu.DropDownItems.Add(new WinForms.ToolStripMenuItem("No sessions") { Enabled = false });
        }
    }

    private void OnNotificationReady(NotificationItem item)
    {
        if (_settings == null) return;

        var shouldNotify = item.Type switch
        {
            NotificationType.WaitingForInput => _settings.NotifyOnWaitingForInput,
            NotificationType.SessionCompleted => _settings.NotifyOnSessionComplete,
            NotificationType.TaskComplete => _settings.NotifyOnTaskComplete,
            _ => true
        };

        if (!shouldNotify) return;

        // If the session's terminal is already in focus, the user is actively looking
        // at it — show a transient popup (with optional timeout) instead of a persistent one.
        TimeSpan? autoDismiss = null;
        bool shownWhileFocused = false;
        if (_settings.AutoDismissWhenFocused && item.Pid.HasValue &&
            WindowFocusService.IsTerminalWindowFocused(item.Pid.Value, item.DisplayName))
        {
            shownWhileFocused = true;
            if (_settings.AutoDismissSeconds > 0)
                autoDismiss = TimeSpan.FromSeconds(_settings.AutoDismissSeconds);
        }

        Dispatcher.BeginInvoke(() =>
        {
            _notificationManager?.ShowNotification(item, autoDismiss, shownWhileFocused);

            if (_settings.PlaySound)
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        });
    }

    private void ShowSettings()
    {
        Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow(_settings ?? new AppSettings());
            window.ShowDialog();
        });
    }

    private void ExitApp()
    {
        _watcher?.Dispose();
        _notificationManager?.DismissAll();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnExit(e);
    }
}

