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

        // UI 스레드에서 팝업 표시
        bool install = false;
        Current.Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                "Npcap이 필요합니다.\n\n지금 설치하시겠습니까?\n\n" +
                "⚠ Install Npcap in WinPcap API-compatible Mode 체크 필수",
                "Npcap 필요", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            install = result == MessageBoxResult.Yes;
        });

        if (!install) return;

        bool ok = await NpcapHelper.InstallNpcapAsync().ConfigureAwait(false);
        if (!ok)
        {
            Current.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    "Npcap 설치 실패.\nhttps://npcap.com 에서 직접 설치하세요.\n\n" +
                    "⚠ WinPcap API-compatible Mode 체크 필수",
                    "설치 실패", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private static async Task CheckUpdateAsync()
    {
        try
        {
            var updater = new UpdateCheckerService();
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
