using System.Windows;
using CopilotNotifier.Services;

namespace CopilotNotifier.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ChkWaitingInput.IsChecked = settings.NotifyOnWaitingForInput;
        ChkSessionComplete.IsChecked = settings.NotifyOnSessionComplete;
        ChkTaskComplete.IsChecked = settings.NotifyOnTaskComplete;
        ChkPlaySound.IsChecked = settings.PlaySound;
        ChkAutoStart.IsChecked = settings.AutoStart;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.NotifyOnWaitingForInput = ChkWaitingInput.IsChecked == true;
        _settings.NotifyOnSessionComplete = ChkSessionComplete.IsChecked == true;
        _settings.NotifyOnTaskComplete = ChkTaskComplete.IsChecked == true;
        _settings.PlaySound = ChkPlaySound.IsChecked == true;
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
