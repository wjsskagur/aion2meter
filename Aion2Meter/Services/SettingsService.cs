using Aion2Meter.Models;
using Newtonsoft.Json;
using System.IO;

namespace Aion2Meter.Services;

/// <summary>
/// 설정을 JSON 파일로 저장/로드.
/// 
/// 저장 경로: %AppData%\Aion2Meter\settings.json
/// AppData 사용 이유: Program Files는 관리자 권한 없이 쓰기 불가,
/// 반면 AppData\Roaming은 사용자 권한으로 읽기/쓰기 가능.
/// (이 앱은 관리자 권한으로 실행되지만 관례상 AppData가 적절)
/// </summary>
public class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2Meter");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings(); // 로드 실패 시 기본값
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* 저장 실패는 조용히 무시 */ }
    }
}
