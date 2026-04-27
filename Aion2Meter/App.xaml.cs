using Aion2Meter.Services;
using Aion2Meter.Views;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Aion2Meter;

public partial class App : Application
{
    // 크래시 로그 경로: %AppData%\Aion2Meter\crash.log
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aion2Meter", "crash.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 전역 예외 핸들러 등록 ──────────────────────────
        // UI 스레드 미처리 예외
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 백그라운드 스레드 미처리 예외
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        // async Task 미처리 예외
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            // ── Npcap 확인 ──────────────────────────────────────
            if (!NpcapHelper.IsNpcapInstalled())
            {
                var result = MessageBox.Show(
                    "Aion2 DPS Meter를 사용하려면 Npcap이 필요합니다.\n\n" +
                    "지금 설치하시겠습니까?\n" +
                    "(설치 파일이 동봉되어 있지 않은 경우 https://npcap.com 에서 직접 설치하세요)\n\n" +
                    "⚠ Install Npcap in WinPcap API-compatible Mode 를 반드시 체크하세요.",
                    "Npcap 필요",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                    NpcapHelper.InstallNpcap();
            }

            // ── 업데이트 체크 (백그라운드, 앱 시작 블로킹 안 함) ──
            _ = CheckUpdateAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnStartup", ex);
            MessageBox.Show(
                $"시작 중 오류가 발생했습니다.\n\n{ex.Message}\n\n" +
                $"로그 위치: {LogPath}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task CheckUpdateAsync()
    {
        try
        {
            var updater = new UpdateCheckerService();
            var update = await updater.CheckForUpdateAsync();
            if (update == null) return;

            Current.Dispatcher.Invoke(() =>
            {
                var window = new UpdateWindow(updater, update);
                window.Show();
            });
        }
        catch { /* 업데이트 체크 실패는 무시 */ }
    }

    // ── 전역 예외 핸들러 ────────────────────────────────────

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI Thread", e.Exception);
        MessageBox.Show(
            $"예상치 못한 오류가 발생했습니다.\n\n{e.Exception.Message}\n\n" +
            $"로그 위치: {LogPath}",
            "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // true = 앱 종료 방지, 계속 실행
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Background Thread", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("Task", e.Exception);
        e.SetObserved(); // 예외 처리됨으로 표시 → 프로세스 종료 방지
    }

    /// <summary>
    /// 예외 정보를 파일로 기록.
    /// 아무 반응 없이 꺼질 때 원인 파악용.
    /// </summary>
    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var msg = $"""
                [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]
                {ex?.GetType().FullName}: {ex?.Message}
                {ex?.StackTrace}
                Inner: {ex?.InnerException?.Message}
                ─────────────────────────────────────────
                """;
            File.AppendAllText(LogPath, msg + Environment.NewLine);
        }
        catch { /* 로그 쓰기 실패는 무시 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
