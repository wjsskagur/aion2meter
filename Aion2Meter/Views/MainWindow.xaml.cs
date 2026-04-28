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

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        _vm = new MainViewModel();
        DataContext = _vm;

        // 설정값 코드로 적용 (바인딩 대신)
        // WindowStyle=None + AllowsTransparency=True 에서
        // Left/Top/Width/Opacity 바인딩은 WPF 레이아웃 무한루프 유발
        var s = _vm.Settings;
        Left    = s.WindowLeft;
        Top     = s.WindowTop;
        Width   = s.WindowWidth;
        Opacity = s.Opacity;
        Topmost = s.AlwaysOnTop;

        _vm.StartCapture();
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_vm != null) _vm.Settings.CompactMode = !_vm.Settings.CompactMode;
            return;
        }
        DragMove();
        if (_vm != null)
        {
            _vm.Settings.WindowLeft = Left;
            _vm.Settings.WindowTop  = Top;
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
        if (_vm != null)
        {
            _vm.Settings.WindowLeft  = Left;
            _vm.Settings.WindowTop   = Top;
            _vm.Settings.WindowWidth = Width;
        }
        _vm?.Cleanup();
    }
}
