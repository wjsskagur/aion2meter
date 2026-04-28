using Aion2Meter.ViewModels;
using SharpPcap;
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

        await Task.Yield();

        _vm = new MainViewModel();
        DataContext = _vm;

        await Task.Delay(500);

        // SharpPcap(wpcap.dll) 로드 가능 여부 먼저 확인
        // Npcap 미설치 시 DllNotFoundException → 앱이 즉시 종료되는 것 방지
        if (!CheckNpcapAvailable()) return;

        _vm.StartCapture();
    }

    private bool CheckNpcapAvailable()
    {
        try
        {
            // SharpPcap.LibPcap의 네이티브 DLL 로드 테스트
            // 실제 캡처 없이 버전 정보만 읽어서 DLL 존재 확인
            var version = SharpPcap.Pcap.LibpcapVersion;
            return true;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException ||
            ex is BadImageFormatException ||
            ex is TypeInitializationException)
        {
            System.Windows.MessageBox.Show(
                "Npcap이 설치되지 않았거나 올바르지 않습니다.\n\n" +
                "https://npcap.com 에서 Npcap을 설치해주세요.\n\n" +
                "⚠ 설치 시 'Install Npcap in WinPcap API-compatible Mode' 반드시 체크",
                "Npcap 필요",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);

            if (_vm != null)
            {
                _vm.StatusMessage = "Npcap 미설치 - 캡처 불가";
                _vm.IsCapturing = false;
            }
            return false;
        }
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
