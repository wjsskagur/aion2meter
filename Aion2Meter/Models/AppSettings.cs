namespace Aion2Meter.Models;

public class AppSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool CompactMode { get; set; } = false;
    public double Opacity { get; set; } = 0.9;
    public int MaxHistoryCount { get; set; } = 20;
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public string? NetworkInterface { get; set; } = null;
    public string? ServerIp { get; set; } = null;

    /// <summary>아이온2 서버 포트 (패킷 필터링용, 실제 포트로 수정 필요)</summary>
    public int AionPort { get; set; } = 13328;

    /// <summary>창 너비 (240 ~ 600)</summary>
    public double WindowWidth { get; set; } = 320;

    /// <summary>플레이어 행 높이 (18 ~ 40). 글씨 크기에 비례</summary>
    public double RowHeight { get; set; } = 22;

    /// <summary>폰트 크기 배율 (0.8 ~ 1.4)</summary>
    public double FontScale { get; set; } = 1.0;
}
