namespace Aion2Meter.Models;

/// <summary>
/// 앱 설정값. JSON으로 직렬화하여 로컬 파일에 저장.
/// %AppData%\Aion2Meter\settings.json
/// </summary>
public class AppSettings
{
    /// <summary>항상 최상위 창으로 표시 (게임 위에 오버레이)</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>컴팩트 모드 (창 크기 축소)</summary>
    public bool CompactMode { get; set; } = false;

    /// <summary>창 투명도 (0.1 ~ 1.0)</summary>
    public double Opacity { get; set; } = 0.9;

    /// <summary>저장할 전투 기록 최대 개수</summary>
    public int MaxHistoryCount { get; set; } = 20;

    /// <summary>마지막 창 위치 X</summary>
    public double WindowLeft { get; set; } = 100;

    /// <summary>마지막 창 위치 Y</summary>
    public double WindowTop { get; set; } = 100;

    /// <summary>캡처할 네트워크 인터페이스 이름 (null이면 자동선택)</summary>
    public string? NetworkInterface { get; set; } = null;

    /// <summary>아이온2 서버 IP (패킷 필터링에 사용, null이면 자동감지)</summary>
    public string? ServerIp { get; set; } = null;
}
