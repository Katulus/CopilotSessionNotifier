using System.Windows;
using System.Windows.Media.Animation;
using CopilotNotifier.Models;
using CopilotNotifier.Services;

namespace CopilotNotifier.UI;

public partial class NotificationPopup : Window
{
    private readonly NotificationItem _item;
    public event Action<NotificationPopup>? PopupClosed;

    public NotificationPopup(NotificationItem item)
    {
        InitializeComponent();
        _item = item;

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
            WindowFocusService.FocusTerminalWindow(_item.Pid.Value);
        }
        AnimateClose();
    }

    private void AnimateClose()
    {
        var storyboard = (Storyboard)FindResource("SlideOut");
        storyboard.Begin(this);
    }

    private void SlideOut_Completed(object? sender, EventArgs e)
    {
        PopupClosed?.Invoke(this);
        Close();
    }
}
