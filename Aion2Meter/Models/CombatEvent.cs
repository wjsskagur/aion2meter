namespace Aion2Meter.Models;

/// <summary>
/// 패킷에서 파싱된 전투 이벤트 1건.
/// 패킷 1개 = CombatEvent 1개로 변환되는 구조.
/// </summary>
public class CombatEvent
{
    /// <summary>공격자 엔티티 ID (패킷 내 4바이트 식별자)</summary>
    public uint AttackerId { get; set; }

    /// <summary>공격자 이름 (캐릭터 정보 패킷에서 매핑)</summary>
    public string AttackerName { get; set; } = "Unknown";

    /// <summary>피격 대상 엔티티 ID</summary>
    public uint TargetId { get; set; }

    /// <summary>피격 대상 이름</summary>
    public string TargetName { get; set; } = "Unknown";

    /// <summary>스킬 ID (패킷 내 값 → 스킬명 테이블로 변환)</summary>
    public uint SkillId { get; set; }

    /// <summary>스킬명 (매핑 실패 시 SkillId 숫자 그대로)</summary>
    public string SkillName { get; set; } = "Unknown";

    /// <summary>실제 피해량</summary>
    public long Damage { get; set; }

    /// <summary>치명타 여부</summary>
    public bool IsCritical { get; set; }

    /// <summary>이벤트 발생 시각 (DPS 계산용)</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
