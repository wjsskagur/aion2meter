namespace Aion2Meter.Models;

/// <summary>
/// 보스 1마리 기준 전투 세션.
/// 전투 시작(첫 피해) ~ 전투 종료(보스 사망 or 수동 리셋)까지의 단위.
/// 전투 기록(History) 저장 시 이 단위로 저장됨.
/// </summary>
public class CombatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>보스 엔티티 ID</summary>
    public uint BossId { get; set; }

    /// <summary>보스 이름</summary>
    public string BossName { get; set; } = "Unknown";

    /// <summary>전투 시작 시각 (첫 CombatEvent 수신 시각)</summary>
    public DateTime StartTime { get; set; }

    /// <summary>전투 종료 시각. 진행 중이면 null</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>전투 경과 시간(초)</summary>
    public double ElapsedSeconds =>
        (EndTime ?? DateTime.Now).Subtract(StartTime).TotalSeconds;

    /// <summary>전투 진행 여부</summary>
    public bool IsActive => EndTime == null;

    /// <summary>참여 플레이어 목록. key: EntityId</summary>
    public Dictionary<uint, PlayerStats> Players { get; set; } = new();

    /// <summary>원시 이벤트 목록 (상세 분석용, 메모리 유의)</summary>
    public List<CombatEvent> Events { get; set; } = new();

    /// <summary>
    /// 전체 파티 총 피해량.
    /// CombatTrackerService._totalPartyDamageCache 와 동기화됨.
    /// History 저장 후 조회용으로만 사용 (실시간 계산 X).
    /// </summary>
    public long TotalPartyDamage { get; set; }
}
