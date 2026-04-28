using Aion2Meter.Services;
using Aion2Meter.Views;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Aion2Meter;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aion2Meter", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        GameDataService.Load();

        try
        {
            // Npcap 체크는 백그라운드로 - async void 블로킹 방지
            _ = Task.Run(CheckNpcapAsync);

            var mainWindow = new MainWindow();
            mainWindow.Show();

            _ = CheckUpdateAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnStartup", ex);
            MessageBox.Show($"시작 오류: {ex.Message}\n로그: {LogPath}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task CheckNpcapAsync()
    {
        if (NpcapHelper.IsNpcapInstalled()) return;

        bool install = false;
        Current.Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                "Aion2 DPS Meter를 사용하려면 Npcap이 필요합니다.\n\n" +
                "지금 설치하시겠습니까?\n" +
                "(https://npcap.com 에서 자동 다운로드)",
                "Npcap 필요", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            install = result == MessageBoxResult.Yes;
        });

        if (!install) return;

        bool ok = await NpcapHelper.InstallNpcapAsync().ConfigureAwait(false);
        if (!ok)
        {
            Current.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    "Npcap 설치에 실패했습니다.\nhttps://npcap.com 에서 직접 설치 후 앱을 재시작해주세요.",
                    "설치 실패", MessageBoxButton.OK, MessageBoxImage.Error));
            return;
        }

        // 설치 완료 후 드라이버 로드 대기 (3초)
        await Task.Delay(3000).ConfigureAwait(false);

        // 캡처 재시작
        Current.Dispatcher.Invoke(() =>
        {
            var mainWindow = Current.MainWindow as Views.MainWindow;
            mainWindow?.RetryCapture();
        });
    }

    private static async Task CheckUpdateAsync()
    {
        try
        {
            using var updater = new UpdateCheckerService();
            var update = await updater.CheckForUpdateAsync().ConfigureAwait(false);
            if (update == null) return;

            _ = Current.Dispatcher.BeginInvoke(() =>
            {
                var window = new UpdateWindow(updater, update);
                window.Show();
            });
        }
        catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI Thread", e.Exception);
        MessageBox.Show($"오류: {e.Exception.Message}\n로그: {LogPath}",
            "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => WriteCrashLog("Background Thread", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("Task", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n" +
                $"{ex?.GetType().FullName}: {ex?.Message}\n" +
                $"{ex?.StackTrace}\n" +
                $"─────────────────────────────────────────\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e) => base.OnExit(e);
}
