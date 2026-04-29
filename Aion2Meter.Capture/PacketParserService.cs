using System.Collections.Concurrent;
using System.Text;

namespace Aion2Meter.Capture;

/// <summary>
/// 아이온2 패킷 파서.
/// TK-open-public/Aion2-Dps-Meter 프로젝트 분석 기반.
///
/// 패킷 구조:
/// - VarInt(길이) + 2바이트 OpCode + 데이터
/// - 0xFF 0xFF 마커가 있으면 LZ4 압축
/// - TCP 시퀀스 번호 기반 재조합 필요
///
/// OpCode:
/// - 0x04 0x38: 일반 데미지
/// - 0x05 0x38: DoT 데미지
/// - 0x33 0x36: 자신 닉네임
/// - 0x44 0x36: 타인 닉네임
/// - 0x00 0x8D: 보스 HP
/// - 0x21 0x8D: 전투 시작/종료
/// </summary>
public class PacketParserService
{
    private readonly ConcurrentDictionary<int, string> _entityNames = new();
    private static readonly Dictionary<long, string> _skillNames = new();
    private static bool _skillsLoaded = false;

    private static readonly Dictionary<long, (string name, bool boss)> _mobData = new();
    private static bool _mobsLoaded = false;

    private static void EnsureMobsLoaded()
    {
        if (_mobsLoaded) return;
        _mobsLoaded = true;
        try
        {
            var candidates = new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "mobs.json"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "Aion2Meter", "Resources", "mobs.json"),
            };
            var path = candidates.FirstOrDefault(System.IO.File.Exists);
            if (path == null) return;

            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                long code = item.GetProperty("code").GetInt64();
                string name = item.GetProperty("name").GetString() ?? "";
                bool boss = item.TryGetProperty("boss", out var b) && b.GetBoolean();
                _mobData[code] = (name, boss);
            }
        }
        catch { }
    }

    private static string? GetMobNameInternal(long mobCode)
    {
        EnsureMobsLoaded();
        return _mobData.TryGetValue(mobCode, out var m) ? m.name : null;
    }

    private static bool IsBossInternal(long mobCode)
    {
        EnsureMobsLoaded();
        return _mobData.TryGetValue(mobCode, out var m) && m.boss;
    }

    private static void EnsureSkillsLoaded()
    {
        if (_skillsLoaded) return;
        _skillsLoaded = true;
        try
        {
            // Capture.exe 옆 Resources 폴더 또는 상위 프로젝트 Resources 폴더
            var candidates = new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "skills.json"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "Aion2Meter", "Resources", "skills.json"),
            };
            var path = candidates.FirstOrDefault(System.IO.File.Exists);
            if (path == null) return;

            var json = System.IO.File.ReadAllText(path);
            var items = System.Text.Json.JsonDocument.Parse(json).RootElement;
            foreach (var item in items.EnumerateArray())
            {
                long code = item.GetProperty("code").GetInt64();
                string name = item.GetProperty("name").GetString() ?? "";
                _skillNames[code] = name;
            }
        }
        catch { }
    }

    private static string GetSkillNameInternal(long skillId)
    {
        EnsureSkillsLoaded();
        if (_skillNames.TryGetValue(skillId, out var n)) return n;
        if (_skillNames.TryGetValue(skillId / 10, out n)) return n;
        if (_skillNames.TryGetValue(skillId / 100, out n)) return n;
        return $"Skill_{skillId}";
    }

    public event Action<object>? OnDamageEvent;
    public event Action<(uint entityId, string name, bool isLocalPlayer)>? OnEntityInfoEvent;
    public event Action<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHpEvent;
    public event Action<(uint entityId, string mobName, bool isBoss)>? OnSpawnEvent;

    // entityId → mobCode 매핑 (스폰 패킷에서 수집)
    private readonly ConcurrentDictionary<int, int> _mobCodeMap = new();

    // TCP 스트림 재조합 버퍼
    private readonly byte[] _streamBuffer = new byte[1024 * 1024]; // 1MB
    private int _streamLen = 0;

    // TCP 시퀀스 재조합
    private long _nextExpectedSeq = -1;
    private readonly SortedDictionary<long, byte[]> _holdBuffer = new();

    private readonly object _feedLock = new();

    /// <summary>TCP 시퀀스 번호와 함께 패킷 처리 (순서 보장)</summary>
    public void FeedPacket(long seqNum, byte[] data)
    {
        lock (_feedLock)
        {
            if (_nextExpectedSeq == -1)
                _nextExpectedSeq = seqNum;

            _holdBuffer[seqNum] = data;

            while (_holdBuffer.Count > 0)
            {
                var firstSeq = _holdBuffer.Keys.First();

                if (firstSeq == _nextExpectedSeq)
                {
                    var chunk = _holdBuffer[firstSeq];
                    _holdBuffer.Remove(firstSeq);
                    _nextExpectedSeq = (_nextExpectedSeq + chunk.Length) & 0xFFFFFFFFL;
                    ProcessChunk(chunk);
                }
                else if (firstSeq < _nextExpectedSeq)
                {
                    _holdBuffer.Remove(firstSeq);
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>시퀀스 재조합 없이 직접 처리 (단순 모드)</summary>
    public void ParsePacket(byte[] data) => ProcessChunk(data);

    private void ProcessChunk(byte[] chunk)
    {
        // 버퍼에 추가
        if (_streamLen + chunk.Length > _streamBuffer.Length)
            _streamLen = 0;

        Buffer.BlockCopy(chunk, 0, _streamBuffer, _streamLen, chunk.Length);
        _streamLen += chunk.Length;

        int offset = 0;
        while (offset < _streamLen)
        {
            if (_streamLen - offset < 2) break;

            var lengthInfo = ReadVarInt(_streamBuffer, offset);
            if (lengthInfo.value == 0) { offset++; continue; }
            if (lengthInfo.value < 0) { _streamLen = 0; break; }

            // TK 공식: realLength = vi_val + vi_len - 4
            int realLength = lengthInfo.value + lengthInfo.length - 4;
            if (realLength <= 0) { _streamLen = 0; break; }
            if (_streamLen - offset < realLength) break;

            var packet = new byte[realLength];
            Buffer.BlockCopy(_streamBuffer, offset, packet, 0, realLength);
            OnPacketReceived(packet);
            offset += realLength;
        }

        // 처리된 데이터 제거
        if (offset > 0)
        {
            int remaining = _streamLen - offset;
            if (remaining > 0)
                Buffer.BlockCopy(_streamBuffer, offset, _streamBuffer, 0, remaining);
            _streamLen = remaining;
        }
    }

    private void OnPacketReceived(byte[] packet)
    {
        if (packet.Length < 3) return;

        var lengthInfo = ReadVarInt(packet);
        if (lengthInfo.value < 0) return;

        int offset = lengthInfo.length;
        if (offset >= packet.Length) return;

        bool extraFlag = (packet[offset] >= 0xF0 && packet[offset] < 0xFF);

        // LZ4 압축 패킷 확인
        if (extraFlag)
        {
            if (offset + 2 < packet.Length &&
                packet[offset + 1] == 0xFF && packet[offset + 2] == 0xFF)
            {
                DecompressAndProcess(packet, offset, true);
                return;
            }
        }
        else
        {
            if (offset + 1 < packet.Length &&
                packet[offset] == 0xFF && packet[offset + 1] == 0xFF)
            {
                DecompressAndProcess(packet, offset, false);
                return;
            }
        }

        // OpCode 로깅 (닉네임 패킷 디버깅용)
        if (offset + 1 < packet.Length)
        {
            byte op1 = packet[offset], op2 = packet[offset + 1];
            if ((op1 == 0x33 || op1 == 0x44) && op2 == 0x36)
                Console.WriteLine($"[NICK_PKT] op=0x{op1:X2}{op2:X2} len={packet.Length} hex={BitConverter.ToString(packet, 0, Math.Min(24, packet.Length))}");
        }

        var li = new VarIntResult(lengthInfo.value, lengthInfo.length);
        ParseNicknameOwn(packet, li);
        ParseNicknameOther(packet, li);
        if (ParseSpawn(packet, li, extraFlag)) return;
        if (ParseDamage(packet, extraFlag)) return;
        if (ParseDoT(packet, extraFlag)) return;
        if (ParseBossHp(packet, li, extraFlag)) return;
    }

    private void DecompressAndProcess(byte[] packet, int headerLength, bool extraFlag)
    {
        try
        {
            int offset = headerLength + 2;
            if (extraFlag) offset++;

            if (offset + 4 > packet.Length) return;
            int originLength = ReadInt32LE(packet, offset);
            offset += 4;

            var restored = new byte[originLength];
            int decoded = LZ4Decompress(packet, offset, packet.Length - offset, restored, 0, originLength);
            if (decoded < 0) return;

            int innerOffset = 0;
            while (innerOffset < restored.Length)
            {
                var li = ReadVarInt(restored, innerOffset);
                if (li.value == 0) { innerOffset++; continue; }
                if (li.value < 0) break;

                int realLen = li.value + li.length - 4;
                if (realLen <= 0) break;
                if (innerOffset + realLen > restored.Length) break;

                var innerPacket = new byte[realLen];
                Buffer.BlockCopy(restored, innerOffset, innerPacket, 0, realLen);
                OnPacketReceived(innerPacket);
                innerOffset += realLen;
            }
        }
        catch { }
    }

    private bool ParseDamage(byte[] packet, bool extraFlag)
    {
        var li = ReadVarInt(packet);
        if (li.length < 0) return false;
        int offset = li.length;
        if (extraFlag) offset++;
        if (offset + 2 >= packet.Length) return false;

        if (packet[offset] != 0x04) return false;
        if (packet[offset + 1] != 0x38) return false;
        offset += 2;

        var targetInfo = ReadVarInt(packet, offset);
        if (targetInfo.length < 0) return false;
        offset += targetInfo.length;

        var switchInfo = ReadVarInt(packet, offset);
        if (switchInfo.length < 0) return false;
        offset += switchInfo.length;

        var flagInfo = ReadVarInt(packet, offset);
        if (flagInfo.length < 0) return false;
        offset += flagInfo.length;

        var actorInfo = ReadVarInt(packet, offset);
        if (actorInfo.length < 0) return false;
        offset += actorInfo.length;

        if (offset + 5 >= packet.Length) return false;

        int skillCode = ReadInt32LE(packet, offset);
        offset += 5;

        var typeInfo = ReadVarInt(packet, offset);
        if (typeInfo.length < 0) return false;
        offset += typeInfo.length;

        int andResult = switchInfo.value & 0x0F;
        int skip = andResult switch
        {
            4 => 8,
            5 => 12,
            6 => 10,
            7 => 14,
            _ => -1
        };
        if (skip < 0) return false;
        offset += skip;

        var unknownInfo = ReadVarInt(packet, offset);
        if (unknownInfo.length < 0) return false;
        offset += unknownInfo.length;

        var damageInfo = ReadVarInt(packet, offset);
        if (damageInfo.length < 0) return false;

        if (actorInfo.value == targetInfo.value) return false;
        if (damageInfo.value <= 0 || damageInfo.value >= 10_000_000) return true;

        string attackerName = _entityNames.TryGetValue(actorInfo.value, out var an)
            ? an : $"플레이어_{actorInfo.value % 1000:D3}";
        string targetName = _entityNames.TryGetValue(targetInfo.value, out var tn)
            ? tn : $"플레이어_{targetInfo.value % 1000:D3}";

        OnDamageEvent?.Invoke(new
        {
            type = "damage",
            attackerId = (uint)actorInfo.value,
            attackerName,
            targetId = (uint)targetInfo.value,
            targetName,
            skillId = (uint)skillCode,
            skillName = GetSkillNameInternal(skillCode),
            damage = (long)damageInfo.value,
            isCritical = false,
            isDot = false
        });
        return true;
    }

    private bool ParseDoT(byte[] packet, bool extraFlag)
    {
        var li = ReadVarInt(packet);
        if (li.length < 0) return false;
        int offset = li.length;
        if (extraFlag) offset++;
        if (offset + 2 >= packet.Length) return false;

        if (packet[offset] != 0x05) return false;
        if (packet[offset + 1] != 0x38) return false;
        offset += 2;

        var targetInfo = ReadVarInt(packet, offset);
        if (targetInfo.length < 0) return false;
        offset += targetInfo.length;

        if (offset >= packet.Length) return false;
        var flagByte = packet[offset];
        if ((flagByte & 0x02) == 0) return true;
        offset++;

        var actorInfo = ReadVarInt(packet, offset);
        if (actorInfo.length < 0) return false;
        if (actorInfo.value == targetInfo.value) return false;
        offset += actorInfo.length;

        var unknownInfo = ReadVarInt(packet, offset);
        if (unknownInfo.length < 0) return false;
        offset += unknownInfo.length;

        if (offset + 4 >= packet.Length) return false;
        int skillCode = ReadInt32LE(packet, offset);
        offset += 4;

        var damageInfo = ReadVarInt(packet, offset);
        if (damageInfo.length < 0 || damageInfo.value <= 0) return false;

        string attackerName = _entityNames.TryGetValue(actorInfo.value, out var an)
            ? an : $"플레이어_{actorInfo.value % 1000:D3}";

        OnDamageEvent?.Invoke(new
        {
            type = "damage",
            attackerId = (uint)actorInfo.value,
            attackerName,
            targetId = (uint)targetInfo.value,
            targetName = _entityNames.TryGetValue(targetInfo.value, out var tn) ? tn : $"플레이어_{targetInfo.value % 1000:D3}",
            skillId = (uint)skillCode,
            skillName = GetSkillNameInternal(skillCode),
            damage = (long)damageInfo.value,
            isCritical = false,
            isDot = true
        });
        return true;
    }

    private void ParseNicknameOwn(byte[] packet, VarIntResult lengthInfo)
    {
        int offset = lengthInfo.length;
        if (offset + 2 >= packet.Length) return;
        if (packet[offset] != 0x33) return;
        if (packet[offset + 1] != 0x36) return;
        offset += 2;

        var userInfo = ReadVarInt(packet, offset);
        if (userInfo.length < 0) return;
        offset += userInfo.length;

        if (offset + 10 >= packet.Length) return;

        // 0x07 구분자 찾기
        int spliterIdx = -1;
        for (int i = 0; i < Math.Min(10, packet.Length - offset); i++)
        {
            if (packet[offset + i] == 0x07) { spliterIdx = i; break; }
        }
        if (spliterIdx == -1) return;
        offset += spliterIdx + 1;

        var nameLengthInfo = ReadVarInt(packet, offset);
        if (nameLengthInfo.length < 0 || nameLengthInfo.value < 1 || nameLengthInfo.value > 71) return;
        offset += nameLengthInfo.length;
        if (offset + nameLengthInfo.value > packet.Length) return;

        string name = Encoding.UTF8.GetString(packet, offset, nameLengthInfo.value);
        if (!IsValidNickname(name)) return;

        _entityNames[userInfo.value] = name;
        OnEntityInfoEvent?.Invoke(((uint)userInfo.value, name, true)); // 자신
    }

    private void ParseNicknameOther(byte[] packet, VarIntResult lengthInfo)
    {
        int offset = lengthInfo.length;
        if (offset + 2 >= packet.Length) return;
        if (packet[offset] != 0x44) return;
        if (packet[offset + 1] != 0x36) return;
        offset += 2;

        var userInfo = ReadVarInt(packet, offset);
        if (userInfo.length < 0) return;
        offset += userInfo.length;

        var u1 = ReadVarInt(packet, offset); if (u1.length < 0) return; offset += u1.length;
        var u2 = ReadVarInt(packet, offset); if (u2.length < 0) return; offset += u2.length;

        if (offset + 2 >= packet.Length) return;
        offset++;

        // 닉네임 찾기 (최대 5바이트 오프셋 시도)
        for (int i = 0; i < 5; i++)
        {
            int tryOffset = offset + i;
            if (tryOffset >= packet.Length) break;
            var nli = ReadVarInt(packet, tryOffset);
            if (nli.length <= 0 || nli.value < 1 || nli.value > 71) continue;
            int nameStart = tryOffset + nli.length;
            if (nameStart + nli.value > packet.Length) continue;
            string candidate = Encoding.UTF8.GetString(packet, nameStart, nli.value);
            if (!IsValidNickname(candidate)) continue;

            _entityNames[userInfo.value] = candidate;
            OnEntityInfoEvent?.Invoke(((uint)userInfo.value, candidate, false)); // 타인
            return;
        }
    }

    private bool ParseSpawn(byte[] packet, VarIntResult lengthInfo, bool extraFlag)
    {
        int offset = lengthInfo.length;
        if (extraFlag) offset++;
        if (offset + 2 >= packet.Length) return false;
        if (packet[offset] != 0x40) return false;
        if (packet[offset + 1] != 0x36) return false;
        offset += 2;

        var entityInfo = ReadVarInt(packet, offset);
        if (entityInfo.length < 0) return false;

        // codeMarker [00 40 02] 또는 [00 00 02] 찾기
        int codeIdx = FindBytes(packet, 0x00, 0x40, 0x02);
        if (codeIdx < 3) codeIdx = FindBytes(packet, 0x00, 0x00, 0x02);
        if (codeIdx < 3) return false;

        // mobCode = codeMarker 앞 3바이트 (little-endian 24bit)
        int mobCode = (packet[codeIdx - 1] << 16) | (packet[codeIdx - 2] << 8) | packet[codeIdx - 3];
        if (mobCode <= 0) return false;

        _mobCodeMap[entityInfo.value] = mobCode;

        // 스킬 데이터에서 몬스터 이름 조회
        string mobName = GetMobNameInternal(mobCode) ?? $"Mob_{mobCode}";
        bool isBoss = IsBossInternal(mobCode);

        // 이름 캐시에도 저장
        _entityNames[entityInfo.value] = mobName;

        Console.WriteLine($"[SPAWN] entityId={entityInfo.value} mobCode={mobCode} name={mobName} boss={isBoss}");
        OnSpawnEvent?.Invoke(((uint)entityInfo.value, mobName, isBoss));
        return true;
    }

    private static int FindBytes(byte[] data, params byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
                if (data[i + j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
    {
        int offset = lengthInfo.length;
        if (extraFlag) offset++;
        if (offset + 2 >= packet.Length) return false;
        if (packet[offset] != 0x00) return false;
        if (packet[offset + 1] != 0x8D) return false;
        offset += 2;

        var mobIdInfo = ReadVarInt(packet, offset);
        if (mobIdInfo.length < 0) return false;
        offset += mobIdInfo.length;

        // 이미 알려진 플레이어 ID면 파티원 HP 패킷 → 보스 HP 아님
        if (_entityNames.ContainsKey(mobIdInfo.value)) return false;

        // 3개 VarInt 스킵
        for (int i = 0; i < 3; i++)
        {
            var vi = ReadVarInt(packet, offset);
            if (vi.length < 0) return false;
            offset += vi.length;
        }

        if (offset + 4 > packet.Length) return false;
        long currentHp = ReadUInt32LE(packet, offset);

        string bossName = _entityNames.TryGetValue(mobIdInfo.value, out var bn) ? bn : $"Boss_{mobIdInfo.value}";
        OnBossHpEvent?.Invoke(((uint)mobIdInfo.value, bossName, currentHp, 0));
        return true;
    }

    // ── LZ4 순수 C# 구현 ────────────────────────────────────────────
    private static int LZ4Decompress(byte[] src, int srcOff, int srcLen, byte[] dst, int dstOff, int dstLen)
    {
        int sEnd = srcOff + srcLen;
        int dEnd = dstOff + dstLen;
        int si = srcOff, di = dstOff;

        while (si < sEnd)
        {
            int token = src[si++];
            int litLen = (token >> 4) & 0xF;
            if (litLen == 15)
            {
                int extra;
                do { extra = src[si++]; litLen += extra; } while (extra == 255);
            }

            if (di + litLen > dEnd) return -1;
            Buffer.BlockCopy(src, si, dst, di, litLen);
            si += litLen; di += litLen;

            if (si >= sEnd) break;

            int matchOff = src[si] | (src[si + 1] << 8);
            si += 2;
            int matchLen = (token & 0xF) + 4;
            if (matchLen - 4 == 15)
            {
                int extra;
                do { extra = src[si++]; matchLen += extra; } while (extra == 255);
            }

            int matchSrc = di - matchOff;
            if (matchSrc < dstOff) return -1;
            if (di + matchLen > dEnd) return -1;
            for (int k = 0; k < matchLen; k++)
                dst[di++] = dst[matchSrc++];
        }
        return di - dstOff;
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────
    private static int ReadInt32LE(byte[] buf, int offset) =>
        buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    private static long ReadUInt32LE(byte[] buf, int offset) =>
        (long)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24)) & 0xFFFFFFFFL;

    private record VarIntResult(int value, int length);

    private static VarIntResult ReadVarInt(byte[] bytes, int offset = 0)
    {
        int value = 0, shift = 0, count = 0;
        while (true)
        {
            if (offset + count >= bytes.Length) return new(-1, -1);
            int b = bytes[offset + count++] & 0xFF;
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return new(value, count);
            shift += 7;
            if (shift >= 32) return new(-1, -1);
        }
    }

    private static bool IsValidNickname(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 71) return false;
        // 최소 한 글자 이상의 한글 또는 영문 포함
        bool hasKorOrEng = s.Any(c =>
            (c >= '\uAC00' && c <= '\uD7A3') ||
            (c >= 'a' && c <= 'z') ||
            (c >= 'A' && c <= 'Z'));
        // 제어문자만 없으면 OK (몬스터 이름은 공백/특수문자 포함 가능)
        bool noControl = s.All(c => c >= 0x20);
        return hasKorOrEng && noControl;
    }
}
