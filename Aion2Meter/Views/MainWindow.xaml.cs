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
        WriteCrashLog("OnWindowLoaded", null);
        Dispatcher.InvokeAsync(InitStep1, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void InitStep1()
    {
        WriteCrashLog("InitStep1 - Creating MainViewModel", null);
        _vm = new MainViewModel();
        WriteCrashLog("InitStep1 - Skipping DataContext for test", null);
        // DataContext = _vm;  // 테스트: 바인딩 없이 확인
        WriteCrashLog("InitStep1 - Done", null);
        Dispatcher.InvokeAsync(InitStep2, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void InitStep2()
    {
        WriteCrashLog("InitStep2 - StartCapture", null);
        // 테스트: 캡처 시작 없이 UI만 확인
        // _vm?.StartCapture();
        WriteCrashLog("InitStep2 - Done (capture disabled for test)", null);
    }

    private static void WriteCrashLog(string step, Exception? ex)
    {
        try
        {
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Aion2Meter", "init.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {step}" +
                (ex != null ? $"\n  {ex.GetType().Name}: {ex.Message}" : "") + "\n");
        }
        catch { }
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
