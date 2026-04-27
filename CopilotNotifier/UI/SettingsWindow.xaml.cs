using System.Text.RegularExpressions;
using System.Windows;
using CopilotNotifier.Services;

namespace CopilotNotifier.UI;

public partial class SettingsWindow : Window
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$");
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ChkWaitingInput.IsChecked = settings.NotifyOnWaitingForInput;
        ChkSessionComplete.IsChecked = settings.NotifyOnSessionComplete;
        TxtSessionCompleteDismiss.Text = settings.SessionCompleteAutoDismissSeconds.ToString();
        ChkTaskComplete.IsChecked = settings.NotifyOnTaskComplete;
        ChkPlaySound.IsChecked = settings.PlaySound;
        ChkAutoDismissWhenFocused.IsChecked = settings.AutoDismissWhenFocused;
        TxtAutoDismissSeconds.Text = settings.AutoDismissSeconds.ToString();
        ChkApprovalPending.IsChecked = settings.NotifyOnApprovalPending;
        TxtApprovalDelay.Text = settings.ApprovalPendingDelaySeconds.ToString();
        ChkAutoStart.IsChecked = settings.AutoStart;
    }

    private void OnDigitsOnlyPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.NotifyOnWaitingForInput = ChkWaitingInput.IsChecked == true;
        _settings.NotifyOnSessionComplete = ChkSessionComplete.IsChecked == true;
        if (int.TryParse(TxtSessionCompleteDismiss.Text, out var scSecs) && scSecs >= 0)
            _settings.SessionCompleteAutoDismissSeconds = scSecs;
        _settings.NotifyOnTaskComplete = ChkTaskComplete.IsChecked == true;
        _settings.PlaySound = ChkPlaySound.IsChecked == true;
        _settings.AutoDismissWhenFocused = ChkAutoDismissWhenFocused.IsChecked == true;
        if (int.TryParse(TxtAutoDismissSeconds.Text, out var secs) && secs >= 0)
            _settings.AutoDismissSeconds = secs;
        _settings.NotifyOnApprovalPending = ChkApprovalPending.IsChecked == true;
        if (int.TryParse(TxtApprovalDelay.Text, out var apSecs) && apSecs >= 0)
            _settings.ApprovalPendingDelaySeconds = apSecs;
        _settings.AutoStart = ChkAutoStart.IsChecked == true;
        _settings.Save();

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
