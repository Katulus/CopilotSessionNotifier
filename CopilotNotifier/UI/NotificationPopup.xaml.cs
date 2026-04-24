using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CopilotNotifier.Models;
using CopilotNotifier.Services;

namespace CopilotNotifier.UI;

public partial class NotificationPopup : Window
{
    private readonly NotificationItem _item;
    private readonly TimeSpan? _autoDismissAfter;
    private DispatcherTimer? _autoDismissTimer;
    private bool _closing;
    public event Action<NotificationPopup>? PopupClosed;
    public event Action<NotificationPopup>? BodyClicked;

    public NotificationItem Item => _item;
    public bool HasAutoDismiss => _autoDismissAfter.HasValue;

    public NotificationPopup(NotificationItem item, TimeSpan? autoDismissAfter = null)
    {
        InitializeComponent();
        _item = item;
        _autoDismissAfter = autoDismissAfter;

        IconText.Text = item.Icon;
        TitleText.Text = item.DisplayName;
        MessageText.Text = item.Message;
        TimeText.Text = item.Timestamp.ToLocalTime().ToString("HH:mm:ss");

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var storyboard = (Storyboard)FindResource("SlideIn");
        storyboard.Begin(this);

        if (_autoDismissAfter is { } delay)
        {
            _autoDismissTimer = new DispatcherTimer { Interval = delay };
            _autoDismissTimer.Tick += (_, _) =>
            {
                _autoDismissTimer?.Stop();
                if (!_closing) AnimateClose();
            };
            _autoDismissTimer.Start();
        }
    }

    public void SetPosition(double right, double bottom)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - right - ActualWidth;
        Top = bottom;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        AnimateClose();
    }

    private void OnBodyClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_item.Pid.HasValue)
        {
            WindowFocusService.FocusTerminalWindow(_item.Pid.Value, _item.DisplayName);
        }
        BodyClicked?.Invoke(this);
        AnimateClose();
    }

    private void AnimateClose()
    {
        if (_closing) return;
        _closing = true;
        _autoDismissTimer?.Stop();
        var storyboard = (Storyboard)FindResource("SlideOut");
        storyboard.Begin(this);
    }

    private void SlideOut_Completed(object? sender, EventArgs e)
    {
        PopupClosed?.Invoke(this);
        Close();
    }
}
