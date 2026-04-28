using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;

namespace Aion2Meter.Services;

/// <summary>
/// Npcap 설치 여부 확인 및 설치 지원.
/// </summary>
public static class NpcapHelper
{
    private const string NPCAP_DOWNLOAD_URL = "https://npcap.com/dist/npcap-1.80.exe";

    public static bool IsNpcapInstalled()
    {
        try
        {
            // 방법 1: Npcap 공식 레지스트리 키
            using var key1 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Npcap");
            if (key1 != null) return true;

            // 방법 2: WinPcap 호환 모드 레지스트리 키
            using var key2 = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap");
            if (key2 != null) return true;

            // 방법 3: wpcap.dll 파일 존재 확인 (System32)
            string dllPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "wpcap.dll");
            if (File.Exists(dllPath)) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Npcap 설치.
    /// 우선순위: 앱 폴더 번들 파일 → 임시폴더 추출 → 웹 다운로드
    /// </summary>
    public static async Task<bool> InstallNpcapAsync(IProgress<string>? progress = null)
    {
        string? installerPath = null;

        try
        {
            // 1순위: 앱 실행 폴더에 npcap-installer.exe 가 있으면 사용
            string bundledPath = Path.Combine(AppContext.BaseDirectory, "npcap-installer.exe");
            if (File.Exists(bundledPath))
            {
                installerPath = bundledPath;
                progress?.Report("번들 설치 파일 사용 중...");
            }

            // 2순위: 리소스에서 추출
            if (installerPath == null)
            {
                installerPath = ExtractInstallerFromResource();
                if (installerPath != null)
                    progress?.Report("내장 설치 파일 추출 중...");
            }

            // 3순위: 웹에서 다운로드
            if (installerPath == null)
            {
                progress?.Report("Npcap 다운로드 중...");
                installerPath = await DownloadNpcapAsync();
            }

            if (installerPath == null)
            {
                progress?.Report("다운로드 실패");
                return false;
            }

            progress?.Report("Npcap 설치 중...");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/winpcap_mode",
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(psi);
            if (process == null) return false;

            // WaitForExitAsync: 비동기 대기 → UI 블로킹 없음
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> DownloadNpcapAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            string tempPath = Path.Combine(Path.GetTempPath(), "npcap-installer.exe");
            var bytes = await http.GetByteArrayAsync(NPCAP_DOWNLOAD_URL);
            await File.WriteAllBytesAsync(tempPath, bytes);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractInstallerFromResource()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "Aion2Meter.Resources.npcap-installer.exe";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            string tempPath = Path.Combine(Path.GetTempPath(), "npcap-installer.exe");
            using var file = File.Create(tempPath);
            stream.CopyTo(file);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }
}
