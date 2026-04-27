namespace Aion2Meter.Models;

/// <summary>
/// 플레이어 1명의 누적 전투 통계.
/// CombatEvent가 쌓일수록 이 객체의 값이 업데이트됨.
/// </summary>
public class PlayerStats
{
    public uint EntityId { get; set; }
    public string Name { get; set; } = "Unknown";

    /// <summary>보스에게 넣은 총 피해량</summary>
    public long TotalDamage { get; set; }

    /// <summary>전체 파티 피해량 대비 비율 (0.0 ~ 1.0), UI 바 너비에 사용</summary>
    public double DamagePercent { get; set; }

    /// <summary>초당 피해량. TotalDamage / 전투경과초</summary>
    public double Dps { get; set; }

    /// <summary>타격 횟수 (스킬 시전 횟수가 아닌 명중 횟수)</summary>
    public int HitCount { get; set; }

    /// <summary>치명타 횟수</summary>
    public int CritCount { get; set; }

    /// <summary>치명타율 (0.0 ~ 1.0)</summary>
    public double CritRate => HitCount > 0 ? (double)CritCount / HitCount : 0;

    /// <summary>
    /// 스킬별 통계 목록. 상세 보기 패널에서 사용.
    /// key: SkillId
    /// </summary>
    public Dictionary<uint, SkillStats> Skills { get; set; } = new();

    /// <summary>본인(로컬 플레이어) 여부 → UI에서 초록색으로 표시</summary>
    public bool IsLocalPlayer { get; set; }
}

/// <summary>스킬 1개의 누적 통계</summary>
public class SkillStats
{
    public uint SkillId { get; set; }
    public string SkillName { get; set; } = "Unknown";
    public long TotalDamage { get; set; }
    public int HitCount { get; set; }
    public int CritCount { get; set; }
    public double CritRate => HitCount > 0 ? (double)CritCount / HitCount : 0;
    public double AverageDamage => HitCount > 0 ? (double)TotalDamage / HitCount : 0;
}
