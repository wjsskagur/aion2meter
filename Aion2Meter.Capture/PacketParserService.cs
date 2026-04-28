using System.Collections.Concurrent;
using System.Text;

namespace Aion2Meter.Capture;

/// <summary>
/// 아이온2 게임 프로토콜 파서.
/// TCP 스트림을 게임 메시지 단위로 재조합하고 OpCode별로 파싱.
/// 모든 메서드는 동기 → async 오버헤드 없음, ref struct 제약 없음.
/// </summary>
public class PacketParserService
{
    private const ushort OPCODE_ATTACK       = 0x0015;
    private const ushort OPCODE_SKILL_DAMAGE = 0x0051;
    private const ushort OPCODE_DOT_DAMAGE   = 0x0089;
    private const ushort OPCODE_ENTITY_INFO  = 0x0021;
    private const ushort OPCODE_BOSS_HP      = 0x0033;

    private readonly byte[] _buffer = new byte[65536];
    private int _bufferLen = 0;
    private readonly ConcurrentDictionary<uint, string> _entityNames = new();

    public event Action<object>? OnDamageEvent;
    public event Action<(uint entityId, string name)>? OnEntityInfoEvent;
    public event Action<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHpEvent;

    private static string GetSkillName(uint skillId) => skillId switch
    {
        0 => "일반 공격",
        _ => $"Skill_{skillId}"
    };

    public void ParsePacket(byte[] data)
    {
        if (_bufferLen + data.Length > _buffer.Length)
            _bufferLen = 0;

        Buffer.BlockCopy(data, 0, _buffer, _bufferLen, data.Length);
        _bufferLen += data.Length;

        int offset = 0;
        while (offset < _bufferLen)
        {
            if (_bufferLen - offset < 4) break;

            ushort msgLen = ReadUInt16(_buffer, offset);
            if (msgLen < 4 || msgLen > 8192) { offset++; continue; }
            if (_bufferLen - offset < msgLen) break;

            ushort opCode = ReadUInt16(_buffer, offset + 2);
            var msgData = new ArraySegment<byte>(_buffer, offset + 4, msgLen - 4);
            ProcessMessage(opCode, msgData);
            offset += msgLen;
        }

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
                ParseDamage(opCode, data, isCriticalEnabled: true);
                break;
            case OPCODE_DOT_DAMAGE:
                ParseDamage(opCode, data, isCriticalEnabled: false);
                break;
            case OPCODE_ENTITY_INFO:
                ParseEntityInfo(data);
                break;
            case OPCODE_BOSS_HP:
                ParseBossHp(data);
                break;
        }
    }

    private void ParseDamage(ushort opCode, ArraySegment<byte> data, bool isCriticalEnabled)
    {
        int minSize = isCriticalEnabled ? 17 : 16;
        if (data.Count < minSize) return;

        var arr = data.Array!;
        int off = data.Offset;

        uint attackerId = ReadUInt32(arr, off);
        uint targetId   = ReadUInt32(arr, off + 4);
        uint skillId    = ReadUInt32(arr, off + 8);
        int  damage     = ReadInt32(arr,  off + 12);
        byte flags      = isCriticalEnabled ? arr[off + 16] : (byte)0;

        if (damage <= 0) return;

        OnDamageEvent?.Invoke(new
        {
            type         = "damage",
            attackerId,
            attackerName = _entityNames.GetValueOrDefault(attackerId, $"플레이어_{attackerId % 1000:D3}"),
            targetId,
            targetName   = _entityNames.GetValueOrDefault(targetId,   $"플레이어_{targetId   % 1000:D3}"),
            skillId,
            skillName    = GetSkillName(skillId),
            damage,
            isCritical   = isCriticalEnabled && (flags & 0x01) != 0,
            isDot        = opCode == OPCODE_DOT_DAMAGE
        });
    }

    private void ParseEntityInfo(ArraySegment<byte> data)
    {
        if (data.Count < 6) return;

        var arr = data.Array!;
        int off = data.Offset;

        uint entityId = ReadUInt32(arr, off);
        byte nameLen  = arr[off + 4];
        if (nameLen == 0 || data.Count < 5 + nameLen) return;

        string name = Encoding.UTF8.GetString(arr, off + 5, nameLen);
        _entityNames[entityId] = name;
        OnEntityInfoEvent?.Invoke((entityId, name));
    }

    private void ParseBossHp(ArraySegment<byte> data)
    {
        if (data.Count < 21) return;

        var arr = data.Array!;
        int off = data.Offset;

        uint bossId    = ReadUInt32(arr, off);
        long currentHp = ReadInt64(arr,  off + 4);
        long maxHp     = ReadInt64(arr,  off + 12);
        byte nameLen   = arr[off + 20];

        string bossName = (nameLen > 0 && data.Count >= 21 + nameLen)
            ? Encoding.UTF8.GetString(arr, off + 21, nameLen)
            : _entityNames.GetValueOrDefault(bossId, $"Boss_{bossId}");

        if (!string.IsNullOrEmpty(bossName))
            _entityNames[bossId] = bossName;

        OnBossHpEvent?.Invoke((bossId, bossName, currentHp, maxHp));
    }

    private static ushort ReadUInt16(byte[] buf, int offset) =>
        (ushort)(buf[offset] | (buf[offset + 1] << 8));

    private static uint ReadUInt32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) |
               (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private static int ReadInt32(byte[] buf, int offset) =>
        buf[offset] | (buf[offset + 1] << 8) |
        (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    private static long ReadInt64(byte[] buf, int offset) =>
        (long)buf[offset]             | ((long)buf[offset + 1] << 8)  |
        ((long)buf[offset + 2] << 16) | ((long)buf[offset + 3] << 24) |
        ((long)buf[offset + 4] << 32) | ((long)buf[offset + 5] << 40) |
        ((long)buf[offset + 6] << 48) | ((long)buf[offset + 7] << 56);
}
