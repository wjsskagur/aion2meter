using Aion2Meter.Services;
using System.Windows;

namespace Aion2Meter.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateCheckerService _updater;
    private readonly UpdateCheckerService.UpdateInfo _update;

    public UpdateWindow(UpdateCheckerService updater, UpdateCheckerService.UpdateInfo update)
    {
        InitializeComponent();
        _updater = updater;
        _update = update;

        CurrentVersionText.Text = $"v{update.CurrentVersion}";
        LatestVersionText.Text = $"v{update.LatestVersion}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? "변경사항 없음"
            : update.ReleaseNotes;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        var progress = new Progress<int>(p =>
        {
            ProgressBar.Value = p;
            ProgressText.Text = $"{p}%";
        });

        bool ok = await _updater.DownloadAndInstallAsync(_update, progress);

        if (!ok)
        {
            MessageBox.Show(
                $"다운로드에 실패했습니다.\n직접 다운로드: {_update.ReleaseUrl}",
                "업데이트 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }
}
