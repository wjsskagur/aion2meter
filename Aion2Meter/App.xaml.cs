using Aion2Meter.Services;
using Aion2Meter.Views;
using System.Windows;

namespace Aion2Meter;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

    private static async Task CheckUpdateAsync()
    {
        try
        {
            var updater = new UpdateCheckerService();
            var update = await updater.CheckForUpdateAsync();

            if (update == null) return;

            // UI 스레드에서 팝업 표시
            Current.Dispatcher.Invoke(() =>
            {
                var window = new UpdateWindow(updater, update);
                window.Show();
            });
        }
        catch { /* 업데이트 체크 실패는 무시 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
