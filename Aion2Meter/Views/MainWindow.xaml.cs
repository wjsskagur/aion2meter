using Aion2Meter.ViewModels;
using System.Windows;

namespace Aion2Meter.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    // 헤더 드래그로 창 이동
    // WindowStyle=None이라 기본 타이틀바가 없어서 직접 구현
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 더블클릭: 컴팩트 모드 토글
            _vm.Settings.CompactMode = !_vm.Settings.CompactMode;
            return;
        }
        DragMove();
        // 드래그 후 위치 저장
        _vm.Settings.WindowLeft = Left;
        _vm.Settings.WindowTop = Top;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_vm)
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _vm.Cleanup();
    }
}
