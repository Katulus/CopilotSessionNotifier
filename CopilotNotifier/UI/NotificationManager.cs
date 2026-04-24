using System.Windows;
using System.Windows.Threading;
using CopilotNotifier.Models;
using CopilotNotifier.Services;
using WpfApplication = System.Windows.Application;

namespace CopilotNotifier.UI;

public class NotificationManager
{
    private readonly List<NotificationPopup> _activePopups = new();
    private readonly object _lock = new();
    private const double Margin = 10;

    private readonly DispatcherTimer _focusPollTimer;

    public NotificationManager()
    {
        _focusPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _focusPollTimer.Tick += OnFocusPollTick;
    }

    public void ShowNotification(NotificationItem item, TimeSpan? autoDismissAfter = null, bool exemptFromFocusDismiss = false)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var popup = new NotificationPopup(item, autoDismissAfter)
            {
                ExemptFromFocusDismiss = exemptFromFocusDismiss
            };
            popup.PopupClosed += OnPopupClosed;
            popup.BodyClicked += OnPopupBodyClicked;

            lock (_lock)
            {
                _activePopups.Add(popup);
            }

            popup.Show();
            popup.UpdateLayout();
            RepositionAll();

            if (!_focusPollTimer.IsEnabled)
                _focusPollTimer.Start();
        });
    }

    private void OnPopupBodyClicked(NotificationPopup popup)
    {
        // User clicked to focus this session — dismiss other notifications for the same session.
        DismissBySession(popup.Item.SessionId, except: popup);
    }

    private void OnFocusPollTick(object? sender, EventArgs e)
    {
        List<NotificationPopup> snapshot;
        lock (_lock)
        {
            if (_activePopups.Count == 0)
            {
                _focusPollTimer.Stop();
                return;
            }
            snapshot = _activePopups.ToList();
        }

        // Dedupe by session so we check each terminal at most once per tick.
        // Exclude popups shown while the terminal was already focused — they manage
        // their own lifetime (either via auto-dismiss timer or explicit user action).
        var bySession = snapshot
            .Where(p => p.Item.Pid.HasValue && !p.HasAutoDismiss && !p.ExemptFromFocusDismiss)
            .GroupBy(p => p.Item.SessionId);

        foreach (var group in bySession)
        {
            var anyPopup = group.First();
            var pid = anyPopup.Item.Pid!.Value;
            if (WindowFocusService.IsTerminalWindowFocused(pid, anyPopup.Item.DisplayName))
            {
                DismissBySession(group.Key);
            }
        }
    }

    private void DismissBySession(string sessionId, NotificationPopup? except = null)
    {
        WpfApplication.Current.Dispatcher.BeginInvoke(() =>
        {
            List<NotificationPopup> toClose;
            lock (_lock)
            {
                toClose = _activePopups
                    .Where(p => p.Item.SessionId == sessionId && p != except && !p.HasAutoDismiss && !p.ExemptFromFocusDismiss)
                    .ToList();
            }

            foreach (var popup in toClose)
            {
                popup.Close();
            }
        });
    }

    private void OnPopupClosed(NotificationPopup popup)
    {
        lock (_lock)
        {
            _activePopups.Remove(popup);
            if (_activePopups.Count == 0)
                _focusPollTimer.Stop();
        }

        WpfApplication.Current.Dispatcher.BeginInvoke(RepositionAll);
    }

    private void RepositionAll()
    {
        lock (_lock)
        {
            var screen = SystemParameters.WorkArea;
            double currentBottom = screen.Bottom;

            for (int i = _activePopups.Count - 1; i >= 0; i--)
            {
                var popup = _activePopups[i];
                currentBottom -= popup.ActualHeight + Margin;
                popup.Left = screen.Right - popup.ActualWidth - Margin;
                popup.Top = currentBottom;
            }
        }
    }

    public void DismissAll()
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            lock (_lock)
            {
                foreach (var popup in _activePopups.ToArray())
                {
                    popup.Close();
                }
                _activePopups.Clear();
                _focusPollTimer.Stop();
            }
        });
    }
}
