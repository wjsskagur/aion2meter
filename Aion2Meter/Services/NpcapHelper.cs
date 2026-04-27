using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Aion2Meter.Services;

/// <summary>
/// Npcap 설치 여부 확인 및 자동 설치 지원.
/// 
/// 왜 Npcap이 별도 설치가 필요한가:
/// Npcap은 커널 드라이버 수준에서 동작하기 때문에
/// .exe 안에 번들링해서 실행하는 것이 불가능함.
/// → 대신 Npcap 설치 파일을 리소스로 포함하고, 미설치 시 자동 실행.
/// </summary>
public static class NpcapHelper
{
    /// <summary>
    /// Npcap 설치 여부 확인.
    /// 레지스트리 키로 확인 (Npcap 공식 설치 경로).
    /// </summary>
    public static bool IsNpcapInstalled()
    {
        try
        {
            // Npcap은 설치 시 이 레지스트리 키를 생성함
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Npcap");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Npcap 설치 파일 실행.
    /// npcap-installer.exe를 앱과 같은 폴더에 두거나, 
    /// 빌드 시 리소스로 포함(EmbeddedResource)한 경우 임시 폴더로 추출 후 실행.
    /// 
    /// WinPcap API 호환 모드(/winpcap_mode)를 강제: SharpPcap이 이 모드 필요.
    /// </summary>
    public static bool InstallNpcap()
    {
        try
        {
            // 앱 실행 파일과 같은 폴더에서 설치 파일 탐색
            string appDir = AppContext.BaseDirectory;
            string installerPath = Path.Combine(appDir, "npcap-installer.exe");

            if (!File.Exists(installerPath))
            {
                // 리소스에서 추출 (빌드 시 EmbeddedResource로 포함한 경우)
                string? extracted = ExtractInstallerFromResource();
                if (extracted == null) return false;
                installerPath = extracted;
            }

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                // /winpcap_mode: WinPcap API 호환 모드 (SharpPcap 필수 조건)
                // /S: 설치 시 UI 최소화 (silent는 아님 - 라이선스 동의 필요)
                Arguments = "/winpcap_mode",
                UseShellExecute = true,
                Verb = "runas" // UAC 권한 상승 (이미 관리자지만 명시)
            };

            var process = Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
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
