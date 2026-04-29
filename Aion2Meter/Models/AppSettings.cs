namespace Aion2Meter.Models;

public class AppSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public double Opacity { get; set; } = 0.9;
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public double WindowWidth { get; set; } = 320;
    public string? NetworkInterface { get; set; } = null;
    public string? ServerIp { get; set; } = null;

    public int AionPort { get; set; } = 13328;
    public bool FilterByBossTarget { get; set; } = true;
    public bool FilterByKnownPlayers { get; set; } = false;  // 기본 OFF

    public double RowHeight { get; set; } = 22;
    public double FontScale { get; set; } = 1.0;
    public bool CompactMode { get; set; } = false;
    public int AutoEndSeconds { get; set; } = 10;
    public string SortBy { get; set; } = "TotalDamage";

    /// <summary>본인 캐릭터 항상 맨 위 고정</summary>
    public bool PinLocalPlayer { get; set; } = false;

    // ── 전투 결과 자동 발송 ──────────────────────────────────
    /// <summary>전투 결과 자동 발송 (사이트 준비 후 활성화)</summary>
    public bool AutoUpload { get; set; } = false;

    /// <summary>발송 API URL (예: https://yoursite.com/api/combat)</summary>
    public string UploadUrl { get; set; } = "";

    /// <summary>HMAC 서명용 비밀키 (서버 발급)</summary>
    public string UploadSecretKey { get; set; } = "";

    /// <summary>익명 클라이언트 ID (기기별 고정 UUID)</summary>
    public string ClientId { get; set; } = Guid.NewGuid().ToString();
}
