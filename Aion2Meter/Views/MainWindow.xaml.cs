using Aion2Meter.ViewModels;
using System.Windows;

namespace Aion2Meter.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        _vm = new MainViewModel();
        DataContext = _vm;

        // StartCaptureAsync 전체를 백그라운드에서 실행
        // pipe.ConnectAsync(타임아웃)가 UI 스레드를 점유하지 않도록
        _ = Task.Run(async () => await _vm.StartCaptureAsync());
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_vm != null)
                _vm.Settings.CompactMode = !_vm.Settings.CompactMode;
            return;
        }
        DragMove();
        if (_vm != null)
        {
            _vm.Settings.WindowLeft = Left;
            _vm.Settings.WindowTop = Top;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var settingsWindow = new SettingsWindow(_vm) { Owner = this };
        settingsWindow.ShowDialog();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _vm?.Cleanup();
    }
}
