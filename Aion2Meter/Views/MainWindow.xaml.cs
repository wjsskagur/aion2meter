using Aion2Meter.ViewModels;
using System.Windows;

namespace Aion2Meter.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        // ViewModel 초기화를 Loaded 이후로 미룸
        // 이유: MainViewModel 생성자에서 PacketCaptureService 초기화 시
        //       Npcap 드라이버 로딩이 UI 스레드를 블로킹할 수 있음
        //       창이 화면에 완전히 뜬 뒤 초기화해야 응답없음 방지
        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        // 창이 완전히 렌더링될 시간을 줌 (UI 스레드 양보)
        await Task.Yield();

        // ViewModel은 UI 스레드에서 생성 (Dispatcher 접근 안전)
        _vm = new MainViewModel();
        DataContext = _vm;
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
