using Aion2Meter.Models;
using System.Collections.Concurrent;

namespace Aion2Meter.Services;

/// <summary>
/// 아이온2 게임 프로토콜 파서.
/// 
/// 패킷 구조 분석 방법:
/// 1. Wireshark로 게임 트래픽 덤프
/// 2. 특정 행동(공격) 반복 → 패킷 패턴 식별
/// 3. 오프셋별 값과 게임 내 수치 비교
/// 
/// 현재 파서는 역공학으로 추정된 구조 기반.
/// 실제 패킷 분석 후 OpCode와 오프셋 수정 필요.
/// 
/// TCP 스트림 조합(Reassembly):
/// TCP는 스트림이라 패킷 경계가 게임 메시지 경계와 다를 수 있음.
/// → 버퍼에 데이터를 쌓으면서 완성된 메시지 단위로 파싱.
/// </summary>
public class PacketParserService
{
    // 아이온2 게임 메시지 OpCode (실제 값은 패킷 분석 후 수정)
    private const ushort OPCODE_ATTACK = 0x0015;       // 일반 공격 피해
    private const ushort OPCODE_SKILL_DAMAGE = 0x0051; // 스킬 피해
    private const ushort OPCODE_DOT_DAMAGE = 0x0089;   // 지속 피해(DoT)
    private const ushort OPCODE_ENTITY_INFO = 0x0021;  // 엔티티(캐릭터) 정보
    private const ushort OPCODE_BOSS_HP = 0x0033;      // 보스 HP 정보

    public event EventHandler<CombatEvent>? OnCombatEvent;
    public event EventHandler<(uint entityId, string name)>? OnEntityInfo;
    public event EventHandler<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHp;

    // TCP 스트림 재조합용 버퍼 (연결별로 분리가 이상적이나 단순화)
    private readonly byte[] _buffer = new byte[65536];
    private int _bufferLen = 0;

    // 엔티티 ID → 이름 캐시 (OPCODE_ENTITY_INFO로 수집)
    private readonly ConcurrentDictionary<uint, string> _entityNames = new();

    /// <summary>
    /// 원시 TCP 페이로드를 받아 게임 메시지 단위로 파싱.
    /// 여러 메시지가 한 패킷에 들어있거나, 메시지가 여러 패킷에 걸칠 수 있음.
    /// </summary>
    public void ParsePacket(byte[] data)
    {
        // 버퍼에 추가
        if (_bufferLen + data.Length > _buffer.Length) _bufferLen = 0; // overflow 방지
        Buffer.BlockCopy(data, 0, _buffer, _bufferLen, data.Length);
        _bufferLen += data.Length;

        int offset = 0;
        while (offset < _bufferLen)
        {
            // 아이온 패킷 헤더: [2바이트 길이][2바이트 OpCode][...데이터...]
            if (_bufferLen - offset < 4) break; // 헤더 미완성

            ushort msgLen = ReadUInt16(_buffer, offset);
            if (msgLen < 4 || msgLen > 8192) { offset++; continue; } // 잘못된 길이 → 동기화 재시도

            if (_bufferLen - offset < msgLen) break; // 메시지 미완성, 다음 패킷 대기

            ushort opCode = ReadUInt16(_buffer, offset + 2);
            var msgData = new ArraySegment<byte>(_buffer, offset + 4, msgLen - 4);

            ProcessMessage(opCode, msgData);
            offset += msgLen;
        }

        // 처리된 데이터 제거 (남은 미완성 데이터 앞으로 당김)
        if (offset > 0)
        {
            int remaining = _bufferLen - offset;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, remaining);
            _bufferLen = remaining;
        }
    }

    private void ProcessMessage(ushort opCode, ArraySegment<byte> data)
    {
        switch (opCode)
        {
            case OPCODE_ATTACK:
            case OPCODE_SKILL_DAMAGE:
                ParseDamageEvent(opCode, data);
                break;

            case OPCODE_DOT_DAMAGE:
                ParseDotDamageEvent(data);
                break;

            case OPCODE_ENTITY_INFO:
                ParseEntityInfo(data);
                break;

            case OPCODE_BOSS_HP:
                ParseBossHp(data);
                break;
        }
    }

    /// <summary>
    /// 일반 공격 / 스킬 피해 파싱.
    /// 
    /// 추정 패킷 구조 (Wireshark 분석 기반, 실제와 다를 수 있음):
    /// [0-3]  uint  AttackerId
    /// [4-7]  uint  TargetId
    /// [8-11] uint  SkillId
    /// [12-15] int  Damage
    /// [16]   byte  Flags (bit0: Critical, bit1: Perfect, bit2: BackAttack)
    /// </summary>
    private void ParseDamageEvent(ushort opCode, ArraySegment<byte> data)
    {
        if (data.Count < 17) return;
        var arr = data.Array!;
        int off = data.Offset;

        uint attackerId = ReadUInt32(arr, off);
        uint targetId = ReadUInt32(arr, off + 4);
        uint skillId = ReadUInt32(arr, off + 8);
        int damage = ReadInt32(arr, off + 12);
        byte flags = arr[off + 16];

        if (damage <= 0) return; // 미스 또는 회피

        var evt = new CombatEvent
        {
            AttackerId = attackerId,
            AttackerName = _entityNames.GetValueOrDefault(attackerId, $"Entity_{attackerId}"),
            TargetId = targetId,
            TargetName = _entityNames.GetValueOrDefault(targetId, $"Entity_{targetId}"),
            SkillId = skillId,
            SkillName = GetSkillName(skillId),
            Damage = damage,
            IsCritical = (flags & 0x01) != 0,
            Timestamp = DateTime.Now
        };

        OnCombatEvent?.Invoke(this, evt);
    }

    private void ParseDotDamageEvent(ArraySegment<byte> data)
    {
        // DoT는 치명타 없음 — flags 없이 파싱
        if (data.Count < 16) return;
        var arr = data.Array!;
        int off = data.Offset;

        uint attackerId = ReadUInt32(arr, off);
        uint targetId = ReadUInt32(arr, off + 4);
        uint skillId = ReadUInt32(arr, off + 8);
        int damage = ReadInt32(arr, off + 12);

        if (damage <= 0) return;

        var evt = new CombatEvent
        {
            AttackerId = attackerId,
            AttackerName = _entityNames.GetValueOrDefault(attackerId, $"Entity_{attackerId}"),
            TargetId = targetId,
            TargetName = _entityNames.GetValueOrDefault(targetId, $"Entity_{targetId}"),
            SkillId = skillId,
            SkillName = GetSkillName(skillId),
            Damage = damage,
            IsCritical = false,
            Timestamp = DateTime.Now
        };

        OnCombatEvent?.Invoke(this, evt);
    }

    /// <summary>
    /// 엔티티 정보 파싱. 캐릭터명 수집.
    /// 
    /// 추정 구조:
    /// [0-3]  uint   EntityId
    /// [4]    byte   NameLength
    /// [5-N]  string Name (UTF-8)
    /// </summary>
    private void ParseEntityInfo(ArraySegment<byte> data)
    {
        if (data.Count < 6) return;
        var arr = data.Array!;
        int off = data.Offset;

        uint entityId = ReadUInt32(arr, off);
        byte nameLen = arr[off + 4];

        if (data.Count < 5 + nameLen) return;

        string name = System.Text.Encoding.UTF8.GetString(arr, off + 5, nameLen);
        _entityNames[entityId] = name;
        OnEntityInfo?.Invoke(this, (entityId, name));
    }

    private void ParseBossHp(ArraySegment<byte> data)
    {
        // bossId(4) + currentHp(8) + maxHp(8) + nameLen(1) = 최소 21바이트
        if (data.Count < 21) return;
        var arr = data.Array!;
        int off = data.Offset;

        uint bossId = ReadUInt32(arr, off);
        long currentHp = ReadInt64(arr, off + 4);
        long maxHp = ReadInt64(arr, off + 12);

        byte nameLen = arr[off + 20];

        // nameLen 만큼 추가 데이터가 있는지 확인
        string bossName = (nameLen > 0 && data.Count >= 21 + nameLen)
            ? System.Text.Encoding.UTF8.GetString(arr, off + 21, nameLen)
            : _entityNames.GetValueOrDefault(bossId, $"Boss_{bossId}");

        _entityNames[bossId] = bossName;
        OnBossHp?.Invoke(this, (bossId, bossName, currentHp, maxHp));
    }

    // ── 스킬 이름 테이블 ──────────────────────────────────────────
    private static string GetSkillName(uint skillId) => skillId switch
    {
        0 => "일반 공격",
        _ => $"Skill_{skillId}"
    };

    /// <summary>
    /// 엔티티 이름 캐시 초기화.
    /// 던전 입장/리셋 시 호출하여 이전 세션의 엔티티 정보 제거.
    /// ConcurrentDictionary는 Clear()가 스레드 안전함.
    /// </summary>
    public void ClearEntityCache() => _entityNames.Clear();

    // ── Little-Endian 바이트 읽기 헬퍼 ──────────────────────────
    // BitConverter 대신 직접 구현 이유: ArraySegment 오프셋 처리 편의성
    private static ushort ReadUInt16(byte[] buf, int offset) =>
        (ushort)(buf[offset] | (buf[offset + 1] << 8));

    private static uint ReadUInt32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) |
               (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private static int ReadInt32(byte[] buf, int offset) =>
        buf[offset] | (buf[offset + 1] << 8) |
        (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    private static long ReadInt64(byte[] buf, int offset) =>
        (long)buf[offset] | ((long)buf[offset + 1] << 8) |
        ((long)buf[offset + 2] << 16) | ((long)buf[offset + 3] << 24) |
        ((long)buf[offset + 4] << 32) | ((long)buf[offset + 5] << 40) |
        ((long)buf[offset + 6] << 48) | ((long)buf[offset + 7] << 56);
}
