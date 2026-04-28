using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Aion2Meter.Capture;

/// <summary>
/// 아이온2 게임 프로토콜 파서.
/// TCP 스트림을 게임 메시지 단위로 재조합하고 OpCode별로 파싱.
///
/// TCP 재조합이 필요한 이유:
///   TCP는 스트림 프로토콜 → 패킷 경계 ≠ 게임 메시지 경계
///   한 패킷에 여러 메시지가 들어오거나 메시지가 여러 패킷에 걸칠 수 있음.
///   버퍼에 누적하면서 완성된 메시지 단위로 파싱.
///
/// OpCode 수정 방법:
///   Wireshark로 게임 트래픽 캡처 후 반복 패턴 분석
///   실제 값으로 OPCODE_* 상수 교체
/// </summary>
public class PacketParserService
{
    // ── OpCode 상수 (실제 값으로 교체 필요) ──────────────────
    private const ushort OPCODE_ATTACK       = 0x0015; // 일반 공격 피해
    private const ushort OPCODE_SKILL_DAMAGE = 0x0051; // 스킬 피해
    private const ushort OPCODE_DOT_DAMAGE   = 0x0089; // 지속 피해(DoT)
    private const ushort OPCODE_ENTITY_INFO  = 0x0021; // 캐릭터 정보 (이름 수신)
    private const ushort OPCODE_BOSS_HP      = 0x0033; // 보스 HP 정보

    // ── TCP 재조합 버퍼 ────────────────────────────────────────
    private readonly byte[] _buffer = new byte[65536];
    private int _bufferLen = 0;

    // ── 엔티티 이름 캐시 (entityId → 이름) ────────────────────
    private readonly ConcurrentDictionary<uint, string> _entityNames = new();

    // ── 이벤트 (Program.cs에서 구독) ─────────────────────────
    public event Func<object, Task>? OnDamageEvent;
    public event Func<(uint entityId, string name), Task>? OnEntityInfoEvent;
    public event Func<(uint bossId, string bossName, long currentHp, long maxHp), Task>? OnBossHpEvent;

    // ── 스킬 이름 테이블 ─────────────────────────────────────
    // 실제 스킬 ID → 이름 매핑은 게임 클라이언트 데이터에서 추출 필요
    // 현재는 ID 숫자 그대로 표시
    private static string GetSkillName(uint skillId) => skillId switch
    {
        0 => "일반 공격",
        _ => $"Skill_{skillId}"
    };

    /// <summary>
    /// TCP 페이로드를 받아 게임 메시지 단위로 파싱.
    /// 여러 패킷에 걸쳐 있을 수 있으므로 버퍼에 누적 후 처리.
    /// </summary>
    public async Task ParsePacketAsync(byte[] data)
    {
        // 버퍼 오버플로 방지
        if (_bufferLen + data.Length > _buffer.Length)
        {
            _bufferLen = 0; // 버퍼 초기화 (동기화 유실 복구)
        }

        Buffer.BlockCopy(data, 0, _buffer, _bufferLen, data.Length);
        _bufferLen += data.Length;

        int offset = 0;
        while (offset < _bufferLen)
        {
            // 헤더 최소 4바이트 필요: [2바이트 길이][2바이트 OpCode]
            if (_bufferLen - offset < 4) break;

            ushort msgLen = ReadUInt16(_buffer, offset);

            // 유효하지 않은 길이 → 버퍼 동기화 유실 → 1바이트씩 이동하며 재탐색
            if (msgLen < 4 || msgLen > 8192)
            {
                offset++;
                continue;
            }

            // 메시지 완성 여부 확인
            if (_bufferLen - offset < msgLen) break;

            ushort opCode = ReadUInt16(_buffer, offset + 2);
            var msgData = new ArraySegment<byte>(_buffer, offset + 4, msgLen - 4);

            await ProcessMessageAsync(opCode, msgData);
            offset += msgLen;
        }

        // 처리된 데이터 제거, 미완성 데이터 앞으로 이동
        if (offset > 0)
        {
            int remaining = _bufferLen - offset;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, remaining);
            _bufferLen = remaining;
        }
    }

    private async Task ProcessMessageAsync(ushort opCode, ArraySegment<byte> data)
    {
        switch (opCode)
        {
            case OPCODE_ATTACK:
            case OPCODE_SKILL_DAMAGE:
                await ParseDamageEventAsync(opCode, data, isCriticalEnabled: true);
                break;

            case OPCODE_DOT_DAMAGE:
                await ParseDamageEventAsync(opCode, data, isCriticalEnabled: false);
                break;

            case OPCODE_ENTITY_INFO:
                await ParseEntityInfoAsync(data);
                break;

            case OPCODE_BOSS_HP:
                await ParseBossHpAsync(data);
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
    /// [16]   byte  Flags (bit0=Critical, bit1=Perfect, bit2=BackAttack)
    /// </summary>
    private async Task ParseDamageEventAsync(ushort opCode, ArraySegment<byte> data, bool isCriticalEnabled)
    {
        int minSize = isCriticalEnabled ? 17 : 16;
        if (data.Count < minSize) return;

        var arr = data.Array!;
        int off = data.Offset;

        uint attackerId = ReadUInt32(arr, off);
        uint targetId   = ReadUInt32(arr, off + 4);
        uint skillId    = ReadUInt32(arr, off + 8);
        int  damage     = ReadInt32(arr, off + 12);
        byte flags      = isCriticalEnabled ? arr[off + 16] : (byte)0;

        if (damage <= 0) return; // 미스 또는 회피

        string attackerName = _entityNames.GetValueOrDefault(attackerId, $"플레이어_{attackerId % 1000:D3}");
        string targetName   = _entityNames.GetValueOrDefault(targetId,   $"플레이어_{targetId   % 1000:D3}");
        string skillName    = GetSkillName(skillId);
        bool   isCritical   = isCriticalEnabled && (flags & 0x01) != 0;

        if (OnDamageEvent != null)
        {
            await OnDamageEvent(new
            {
                type         = "damage",
                attackerId,
                attackerName,
                targetId,
                targetName,
                skillId,
                skillName,
                damage,
                isCritical,
                isDot        = opCode == OPCODE_DOT_DAMAGE
            });
        }
    }

    /// <summary>
    /// 엔티티(캐릭터) 정보 파싱. 이름 캐시 갱신.
    ///
    /// 추정 구조:
    /// [0-3]  uint  EntityId
    /// [4]    byte  NameLength
    /// [5-N]  UTF-8 Name
    /// </summary>
    private async Task ParseEntityInfoAsync(ArraySegment<byte> data)
    {
        if (data.Count < 6) return;

        var arr = data.Array!;
        int off = data.Offset;

        uint entityId = ReadUInt32(arr, off);
        byte nameLen  = arr[off + 4];

        if (nameLen == 0 || data.Count < 5 + nameLen) return;

        string name = Encoding.UTF8.GetString(arr, off + 5, nameLen);
        _entityNames[entityId] = name;

        if (OnEntityInfoEvent != null)
            await OnEntityInfoEvent((entityId, name));
    }

    /// <summary>
    /// 보스 HP 파싱.
    ///
    /// 추정 구조:
    /// [0-3]   uint  BossId
    /// [4-11]  long  CurrentHp
    /// [12-19] long  MaxHp
    /// [20]    byte  NameLength
    /// [21-N]  UTF-8 BossName
    /// </summary>
    private async Task ParseBossHpAsync(ArraySegment<byte> data)
    {
        // 최소: bossId(4) + currentHp(8) + maxHp(8) + nameLen(1) = 21바이트
        if (data.Count < 21) return;

        var arr = data.Array!;
        int off = data.Offset;

        uint bossId   = ReadUInt32(arr, off);
        long currentHp = ReadInt64(arr, off + 4);
        long maxHp     = ReadInt64(arr, off + 12);
        byte nameLen   = arr[off + 20];

        string bossName = (nameLen > 0 && data.Count >= 21 + nameLen)
            ? Encoding.UTF8.GetString(arr, off + 21, nameLen)
            : _entityNames.GetValueOrDefault(bossId, $"Boss_{bossId}");

        if (!string.IsNullOrEmpty(bossName))
            _entityNames[bossId] = bossName;

        if (OnBossHpEvent != null)
            await OnBossHpEvent((bossId, bossName, currentHp, maxHp));
    }

    // ── Little-Endian 바이트 읽기 헬퍼 ──────────────────────

    private static ushort ReadUInt16(byte[] buf, int offset) =>
        (ushort)(buf[offset] | (buf[offset + 1] << 8));

    private static uint ReadUInt32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) |
               (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private static int ReadInt32(byte[] buf, int offset) =>
        buf[offset] | (buf[offset + 1] << 8) |
        (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    private static long ReadInt64(byte[] buf, int offset) =>
        (long)buf[offset]           | ((long)buf[offset + 1] << 8)  |
        ((long)buf[offset + 2] << 16) | ((long)buf[offset + 3] << 24) |
        ((long)buf[offset + 4] << 32) | ((long)buf[offset + 5] << 40) |
        ((long)buf[offset + 6] << 48) | ((long)buf[offset + 7] << 56);
}
