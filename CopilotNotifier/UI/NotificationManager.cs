using System.Windows;
using CopilotNotifier.Models;
using WpfApplication = System.Windows.Application;

namespace CopilotNotifier.UI;

public class NotificationManager
{
    private readonly List<NotificationPopup> _activePopups = new();
    private readonly object _lock = new();
    private const double Margin = 10;

    public void ShowNotification(NotificationItem item)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var popup = new NotificationPopup(item);
            popup.PopupClosed += OnPopupClosed;

            lock (_lock)
            {
                _activePopups.Add(popup);
            }

            popup.Show();
            popup.UpdateLayout();
            RepositionAll();
        });
    }

    private void OnPopupClosed(NotificationPopup popup)
    {
        lock (_lock)
        {
            _activePopups.Remove(popup);
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
            }
        });
    }
}
