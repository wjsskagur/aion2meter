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
        // StartupUri 대신 직접 창 생성: 예외 핸들러가 먼저 등록된 후 창을 열어야
        // InitializeComponent() 내 XAML 파싱 오류도 핸들러에서 잡힘
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            // ── Npcap 확인 (MainWindow 생성 전에 먼저 체크) ────────
            // SharpPcap DLL은 MainWindow → MainViewModel → PacketCaptureService
            // 로드 순서로 참조됨. Npcap 없으면 DLL 로드 실패로 앱이 즉시 종료.
            // 창 뜨기 전에 체크해서 사용자에게 안내.
            if (!NpcapHelper.IsNpcapInstalled())
            {
                var result = MessageBox.Show(
                    "Aion2 DPS Meter를 사용하려면 Npcap이 필요합니다.\n\n" +
                    "지금 설치하시겠습니까?\n" +
                    "(설치 파일이 없으면 https://npcap.com 에서 자동 다운로드됩니다)\n\n" +
                    "⚠ Install Npcap in WinPcap API-compatible Mode 를 반드시 체크하세요.",
                    "Npcap 필요",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    bool ok = await NpcapHelper.InstallNpcapAsync();
                    if (!ok)
                        MessageBox.Show(
                            "Npcap 설치에 실패했습니다.\nhttps://npcap.com 에서 직접 설치해주세요.\n\n" +
                            "⚠ Install Npcap in WinPcap API-compatible Mode 체크 필수",
                            "설치 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            // ── 메인 창 생성 ─────────────────────────────────────
            var mainWindow = new Views.MainWindow();
            mainWindow.Show();

            // ── 업데이트 체크 (백그라운드) ────────────────────────
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

            _ = Current.Dispatcher.BeginInvoke(() =>
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
