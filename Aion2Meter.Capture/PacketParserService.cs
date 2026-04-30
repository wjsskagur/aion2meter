using System.Collections.Concurrent;
using System.Text;

namespace Aion2Meter.Capture;

/// <summary>
/// 아이온2 패킷 파서 (A2Viewer 분석 기반 개선판)
///
/// A2Viewer PacketEngine 분석 적용:
/// - TryParseUserInfo: 자신/타인 닉네임 (LZ4 압축 포함)
/// - TryParseMobInfo: 스폰 패킷에서 몬스터 이름+보스여부
/// - TryParseDamage/TryParseDot: 데미지 파싱
/// - TryParseBossHp: 보스 HP
/// - FindNickAndServerInCpPacket: CP 패킷에서 닉네임 추출
/// - NormalizeToBaseSkill: 스킬 코드 정규화
/// - IsPlausibleJobCode: 직업 코드 유효성 검사
/// - TryConfirmBossDeathByDamage: 데미지로 보스 사망 확인
/// </summary>
public class PacketParserService
{
    // ── 엔티티 이름 캐시 ─────────────────────────────────────────────
    private readonly ConcurrentDictionary<int, string> _entityNames = new();
    private readonly ConcurrentDictionary<int, int>    _entityJobCodes = new();

    // A2Viewer ActorStats.IsPlayer 기준 재현
    // ENTITY 패킷(0x33/0x44 0x36) 수신 → 플레이어 확정
    private readonly ConcurrentDictionary<int, byte> _confirmedPlayerIds = new();
    // SPAWN 패킷(0x40 0x36) 수신 → 몬스터 확정
    private readonly ConcurrentDictionary<int, byte> _confirmedMobIds = new();

    /// <summary>플레이어 여부 (ENTITY 패킷 수신 기준)</summary>
    public bool IsConfirmedPlayer(int entityId) => _confirmedPlayerIds.ContainsKey(entityId);

    /// <summary>몬스터 여부 (SPAWN 패킷 수신 기준)</summary>
    public bool IsConfirmedMob(int entityId) => _confirmedMobIds.ContainsKey(entityId);

    // ── 스킬 DB ──────────────────────────────────────────────────────
    private static readonly Dictionary<long, (string name, string cls)> _skillDb = new();
    private static bool _skillDbLoaded;

    // ── 몬스터 DB ────────────────────────────────────────────────────
    private static readonly Dictionary<long, (string name, bool boss)> _mobDb = new();
    private static bool _mobDbLoaded;

    // ── 서버 ID 목록 (A2Viewer ServerMap 기준) ───────────────────────
    private static readonly HashSet<int> _validServerIds = new()
    {
        1001,1002,1003,1004,1005,1006,1007,1008,1009,1010,
        1011,1012,1013,1014,1015,1016,1017,1018,1019,1020,1021,
        2001,2002,2003,2004,2005,2006,2007,2008,2009,2010,
        2011,2012,2013,2014,2015,2016,2017,2018,2019,2020,2021,
    };

    // 0x4F 0x36 0x00 0x00 — 캐릭터 뷰 닉네임 앵커 (A2Viewer NickAnchor)
    private static readonly byte[] _nickAnchor = { 0x4F, 0x36, 0x00, 0x00 };

    // ── 직업 코드 매핑 (패킷에서 직접 읽은 jobCode → 직업명) ─────────
    // A2Viewer IsPlausibleJobCode 기준: 2자리 숫자로 11~18
    private static readonly Dictionary<int, string> _jobNames = new()
    {
        { 11, "검성" }, { 12, "수호성" }, { 13, "살성" }, { 14, "궁성" },
        { 15, "마도성" }, { 16, "정령성" }, { 17, "치유성" }, { 18, "호법성" },
    };

    // 스킬 코드 prefix → 직업명 (직업 코드 없을 때 fallback)
    private static readonly Dictionary<string, string> _classByPrefix = new()
    {
        { "11", "검성" }, { "12", "수호성" }, { "13", "살성" }, { "14", "궁성" },
        { "15", "마도성" }, { "16", "정령성" }, { "17", "치유성" }, { "18", "호법성" },
    };

    // entityId → 추론된 직업명
    private readonly ConcurrentDictionary<int, string> _detectedClasses = new();
    // entityId → 총 누적 데미지 (보스 사망 확인용)
    private readonly ConcurrentDictionary<int, long>   _mobTotalDamageTaken = new();
    // 스폰 패킷에서 수집한 entityId → mobCode
    private readonly ConcurrentDictionary<int, int>    _mobCodeMap = new();

    // A2Viewer TryConfirmBossDeathByDamage: HP 패킷 수신 시 스냅샷
    // entityId → (hp, 스냅샷 시점 누적 피해)
    private readonly ConcurrentDictionary<int, (long hp, long dmgAtSnapshot)> _mobHpSnapshot = new();

    // A2Viewer TryParseSummon: 소환수 → 주인 매핑
    private readonly ConcurrentDictionary<int, int> _summonOwners = new();

    // A2Viewer SubHitParentMap: 서브히트 스킬 → 부모 스킬
    private static readonly Dictionary<int, int> _subHitParentMap = new() { { 17040000, 17050000 } };

    // CP 패킷 마커
    private static readonly byte[] _cpPacketMarker    = { 0x00, 0x92 };
    private static readonly byte[] _summonBoundary    = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] _summonActorHeader = { 0x07, 0x02, 0x06 };

    // ── 이벤트 ───────────────────────────────────────────────────────
    public event Action<object>? OnDamageEvent;
    public event Action<(uint entityId, string name, bool isLocalPlayer, int serverId)>? OnEntityInfoEvent;
    public event Action<(uint bossId, string bossName, long currentHp, long maxHp)>? OnBossHpEvent;
    public event Action<(uint entityId, string mobName, bool isBoss)>? OnSpawnEvent;
    public event Action<uint>? OnEntityRemovedEvent;
    public event Action<(uint summonId, uint ownerId)>? OnSummonEvent;
    public event Action<(string nick, int serverId)>? OnCombatPowerNameEvent;

    // ── TCP 재조합 ───────────────────────────────────────────────────
    private readonly byte[] _streamBuffer = new byte[1024 * 1024];
    private int _streamLen;
    private long _nextExpectedSeq = -1;
    private readonly SortedDictionary<long, byte[]> _holdBuffer = new();
    private readonly object _feedLock = new();

    // ══════════════════════════════════════════════════════════════════
    // 데이터 로드
    // ══════════════════════════════════════════════════════════════════

    private static void EnsureSkillDb()
    {
        if (_skillDbLoaded) return;
        _skillDbLoaded = true;
        try
        {
            var path = FindResourceFile("skills.json");
            if (path == null) return;
            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                long code = item.GetProperty("code").GetInt64();
                string name = item.GetProperty("name").GetString() ?? "";
                string cls  = item.TryGetProperty("class", out var c) ? c.GetString() ?? "" : "";
                _skillDb[code] = (name, cls);
            }
        }
        catch { }
    }

    private static void EnsureMobDb()
    {
        if (_mobDbLoaded) return;
        _mobDbLoaded = true;
        try
        {
            var path = FindResourceFile("mobs.json");
            if (path == null) return;
            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                long code = item.GetProperty("code").GetInt64();
                string name = item.GetProperty("name").GetString() ?? "";
                bool boss = item.TryGetProperty("boss", out var b) && b.GetBoolean();
                _mobDb[code] = (name, boss);
            }
        }
        catch { }
    }

    private static string? FindResourceFile(string filename)
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", filename),
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "Aion2Meter", "Resources", filename),
        };
        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    // ══════════════════════════════════════════════════════════════════
    // 스킬 이름 조회 (NormalizeToBaseSkill 방식)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A2Viewer NormalizeToBaseSkill 방식:
    /// 스킬 코드를 단계별로 정규화하여 이름 조회.
    /// 아이온2 스킬 코드는 XXYYYYY0 / XXYYYYY1 형태로
    /// 마지막 1~3자리가 레벨/변형을 나타냄.
    /// </summary>
    private static string GetSkillName(long skillId)
    {
        EnsureSkillDb();

        // 1. 직접 조회
        if (_skillDb.TryGetValue(skillId, out var s)) return s.name;

        // 2. 마지막 1자리 짝수화 (12092351 → 12092350)
        var b0 = (skillId / 10) * 10;
        if (_skillDb.TryGetValue(b0, out s)) return s.name;

        // 3. 마지막 1자리 제거 (/10)
        if (_skillDb.TryGetValue(skillId / 10, out s)) return s.name;

        // 4. 마지막 2자리 제거 (/100)
        if (_skillDb.TryGetValue(skillId / 100, out s)) return s.name;

        // 5. 마지막 3자리 제거 (/1000)
        if (_skillDb.TryGetValue(skillId / 1000, out s)) return s.name;

        return $"Skill_{skillId}";
    }

    private static string GetSkillClass(long skillId)
    {
        EnsureSkillDb();
        if (_skillDb.TryGetValue(skillId, out var s) && !string.IsNullOrEmpty(s.cls)) return s.cls;
        var b0 = (skillId / 10) * 10;
        if (_skillDb.TryGetValue(b0, out s) && !string.IsNullOrEmpty(s.cls)) return s.cls;
        if (_skillDb.TryGetValue(skillId / 10, out s) && !string.IsNullOrEmpty(s.cls)) return s.cls;
        if (_skillDb.TryGetValue(skillId / 100, out s) && !string.IsNullOrEmpty(s.cls)) return s.cls;
        return "";
    }

    /// <summary>
    /// 스킬 변형 코드를 기본 코드로 정규화 (GetSkillName과 동일한 단계).
    /// 같은 스킬이 여러 키로 분산되지 않도록 dict 키를 통일하기 위해 사용.
    /// </summary>
    private static long NormalizeSkillCode(long skillId)
    {
        EnsureSkillDb();
        if (_skillDb.ContainsKey(skillId)) return skillId;
        var b0 = (skillId / 10) * 10;
        if (_skillDb.ContainsKey(b0)) return b0;
        if (_skillDb.ContainsKey(skillId / 10)) return skillId / 10;
        if (_skillDb.ContainsKey(skillId / 100)) return skillId / 100;
        if (_skillDb.ContainsKey(skillId / 1000)) return skillId / 1000;
        return skillId;
    }

    // ══════════════════════════════════════════════════════════════════
    // 직업 추론
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A2Viewer IsPlausibleJobCode: 직업 코드가 유효한지 확인
    /// </summary>
    private static bool IsPlausibleJobCode(int code) => _jobNames.ContainsKey(code);

    private void SetEntityClass(int entityId, string className)
    {
        _detectedClasses.TryAdd(entityId, className);
    }

    private void DetectClassFromSkill(int entityId, long skillId)
    {
        if (_detectedClasses.ContainsKey(entityId)) return;

        // skills.json class 필드 우선
        string cls = GetSkillClass(skillId);
        if (!string.IsNullOrEmpty(cls)) { SetEntityClass(entityId, cls); return; }

        // prefix fallback
        string code = skillId.ToString();
        if (code.Length == 8 && _classByPrefix.TryGetValue(code[..2], out var prefixCls))
            SetEntityClass(entityId, prefixCls);
    }

    /// <summary>직업명 조회. 같은 직업 복수 시 번호 부여</summary>
    public string GetDisplayName(int entityId, string fallbackName)
    {
        if (_entityNames.TryGetValue(entityId, out var name)) return name;
        if (_detectedClasses.TryGetValue(entityId, out var cls))
        {
            var sameIds = _detectedClasses
                .Where(kv => kv.Value == cls)
                .Select(kv => kv.Key)
                .OrderBy(id => id)
                .ToList();
            if (sameIds.Count == 1) return cls;
            int rank = sameIds.IndexOf(entityId) + 1;
            return $"{cls}{rank}";
        }
        return fallbackName;
    }

    // ══════════════════════════════════════════════════════════════════
    // TCP 재조합
    // ══════════════════════════════════════════════════════════════════

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
                    _holdBuffer.Remove(firstSeq);
                else
                    break;
            }
        }
    }

    public void ParsePacket(byte[] data) => ProcessChunk(data);

    // ══════════════════════════════════════════════════════════════════
    // 스트림 처리
    // ══════════════════════════════════════════════════════════════════

    private void ProcessChunk(byte[] chunk)
    {
        // 원시 청크에서 NickAnchor(0x4F 0x36 0x00 0x00) 패턴 스캔 (A2Viewer ParseLoop 방식)
        TryScanNickAnchor(chunk);

        if (_streamLen + chunk.Length > _streamBuffer.Length)
            _streamLen = 0;

        Buffer.BlockCopy(chunk, 0, _streamBuffer, _streamLen, chunk.Length);
        _streamLen += chunk.Length;

        int offset = 0;
        while (offset < _streamLen)
        {
            if (_streamLen - offset < 2) break;

            var li = ReadVarInt(_streamBuffer, offset);
            if (li.value == 0) { offset++; continue; }
            if (li.value < 0) { _streamLen = 0; break; }

            int realLength = li.value + li.length - 4;
            if (realLength <= 0) { _streamLen = 0; break; }
            if (_streamLen - offset < realLength) break;

            var packet = new byte[realLength];
            Buffer.BlockCopy(_streamBuffer, offset, packet, 0, realLength);
            OnPacketReceived(packet);
            offset += realLength;
        }

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

        var li = ReadVarInt(packet);
        if (li.value < 0) return;

        int offset = li.length;
        if (offset >= packet.Length) return;

        bool extra = (packet[offset] >= 0xF0 && packet[offset] < 0xFF);

        // LZ4 압축 패킷
        if (extra)
        {
            if (offset + 2 < packet.Length && packet[offset+1] == 0xFF && packet[offset+2] == 0xFF)
            { DecompressAndProcess(packet, offset, true); return; }
        }
        else
        {
            if (offset + 1 < packet.Length && packet[offset] == 0xFF && packet[offset+1] == 0xFF)
            { DecompressAndProcess(packet, offset, false); return; }
        }

        var lir = new VarIntResult(li.value, li.length);
        ParseUserInfo(packet, lir, isOwn: packet[li.length] == 0x33);
        if (ParseSpawn(packet, lir, extra)) return;
        if (ParseDamage(packet, extra)) return;
        if (ParseDoT(packet, extra)) return;
        if (ParseBossHp(packet, lir, extra)) return;
        TryParseEntityRemoved(packet, lir);
        TryScanCombatPower(packet, lir.length, packet.Length - lir.length);
        TryParseSummon(packet, lir, extra);
    }

    private void DecompressAndProcess(byte[] packet, int headerLen, bool extra)
    {
        try
        {
            int offset = headerLen + 2;
            if (extra) offset++;
            if (offset + 4 > packet.Length) return;

            int originLen = ReadInt32LE(packet, offset);
            offset += 4;

            var restored = new byte[originLen];
            if (LZ4Decompress(packet, offset, packet.Length - offset, restored, 0, originLen) < 0) return;

            int inner = 0;
            while (inner < restored.Length)
            {
                var li = ReadVarInt(restored, inner);
                if (li.value == 0) { inner++; continue; }
                if (li.value < 0) break;

                int realLen = li.value + li.length - 4;
                if (realLen <= 0) break;
                if (inner + realLen > restored.Length) break;

                var innerPkt = new byte[realLen];
                Buffer.BlockCopy(restored, inner, innerPkt, 0, realLen);
                OnPacketReceived(innerPkt);
                inner += realLen;
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════
    // 닉네임 파싱 (A2Viewer DecodeNick / DecompressNick 방식)
    // ══════════════════════════════════════════════════════════════════

    private void ParseUserInfo(byte[] packet, VarIntResult li, bool isOwn)
    {
        int offset = li.length;
        if (offset + 2 >= packet.Length) return;

        // 0x33 0x36 = 자신, 0x44 0x36 = 타인
        bool ownPkt   = packet[offset] == 0x33 && packet[offset+1] == 0x36;
        bool otherPkt = packet[offset] == 0x44 && packet[offset+1] == 0x36;
        if (!ownPkt && !otherPkt) return;
        offset += 2;

        var entityInfo = ReadVarInt(packet, offset);
        if (entityInfo.length < 0) return;
        offset += entityInfo.length;

        if (ownPkt)
        {
            // 0x33 0x36: 0x07 구분자 뒤에 이름
            string? name = ParseNameAfter07(packet, offset);
            if (name == null) return;
            _entityNames[entityInfo.value] = name;
            _confirmedPlayerIds.TryAdd(entityInfo.value, 0);

            // 자신: 이름 바로 뒤 2바이트가 serverId (A2Viewer TryParseUserInfo self 분기)
            int afterNameOffset = FindAfterName07Offset(packet, offset);
            int ownServerId = -1;
            if (afterNameOffset >= 0 && afterNameOffset + 2 <= packet.Length)
            {
                int sid = packet[afterNameOffset] | (packet[afterNameOffset + 1] << 8);
                if (_validServerIds.Contains(sid)) ownServerId = sid;
            }
            OnEntityInfoEvent?.Invoke(((uint)entityInfo.value, name, true, ownServerId));
        }
        else
        {
            // 0x44 0x36: unknownInfo1, unknownInfo2 skip 후 이름
            var u1 = ReadVarInt(packet, offset); if (u1.length < 0) return; offset += u1.length;
            var u2 = ReadVarInt(packet, offset); if (u2.length < 0) return; offset += u2.length;
            if (offset >= packet.Length) return;
            offset++; // 1바이트 skip

            TryExtractJobCode(packet, offset, entityInfo.value);

            string? name = TryFindName(packet, offset, maxTry: 8);
            if (name == null) return;

            _entityNames[entityInfo.value] = name;
            _confirmedPlayerIds.TryAdd(entityInfo.value, 0);

            if (_detectedClasses.ContainsKey(entityInfo.value))
                _detectedClasses.TryRemove(entityInfo.value, out _);

            // 타인: 이름+jobCode 이후 75~108 바이트 내에서 serverId 스캔
            int afterNameOff = FindAfterName07Offset(packet, offset);
            int otherServerId = -1;
            if (afterNameOff >= 0)
            {
                // jobCode VarInt 1개 스킵 후 FindServerId
                var jobVi = ReadVarInt(packet, afterNameOff);
                int scanFrom = afterNameOff + (jobVi.length > 0 ? jobVi.length : 1);
                otherServerId = FindServerId(packet, scanFrom, packet.Length);
            }
            OnEntityInfoEvent?.Invoke(((uint)entityInfo.value, name, false, otherServerId));
        }
    }

    /// <summary>0x07 구분자 뒤의 이름을 찾아 반환</summary>
    private static string? ParseNameAfter07(byte[] packet, int offset)
    {
        int splitter = -1;
        for (int i = 0; i < Math.Min(12, packet.Length - offset); i++)
        {
            if (packet[offset + i] == 0x07) { splitter = i; break; }
        }
        if (splitter == -1) return null;
        offset += splitter + 1;

        // LZ4 압축 닉네임 처리 (A2Viewer DecompressNick)
        // 0xFF 마커로 시작하면 압축된 닉네임
        if (offset < packet.Length && packet[offset] == 0xFF)
            return TryDecompressNick(packet, offset + 1);

        var nli = ReadVarInt(packet, offset);
        if (nli.length <= 0 || nli.value < 1 || nli.value > 71) return null;
        int nameStart = offset + nli.length;
        if (nameStart + nli.value > packet.Length) return null;

        string name = Encoding.UTF8.GetString(packet, nameStart, nli.value);
        return IsValidNickname(name) ? name : null;
    }

    /// <summary>LZ4 압축된 닉네임 해제 (A2Viewer DecompressNick)</summary>
    private static string? TryDecompressNick(byte[] packet, int offset)
    {
        try
        {
            if (offset + 2 >= packet.Length) return null;
            int compLen = packet[offset] | (packet[offset+1] << 8);
            int origLen = packet[offset+2];
            offset += 3;
            if (compLen <= 0 || origLen <= 0 || origLen > 71) return null;
            if (offset + compLen > packet.Length) return null;

            var dst = new byte[origLen];
            int dec = LZ4Decompress(packet, offset, compLen, dst, 0, origLen);
            if (dec < 0) return null;

            string name = Encoding.UTF8.GetString(dst, 0, dec);
            return IsValidNickname(name) ? name : null;
        }
        catch { return null; }
    }

    /// <summary>오프셋부터 maxTry 범위 내에서 유효한 이름 찾기</summary>
    private static string? TryFindName(byte[] packet, int offset, int maxTry = 8)
    {
        for (int i = 0; i < maxTry; i++)
        {
            int tryOff = offset + i;
            if (tryOff >= packet.Length) break;

            // LZ4 압축 닉네임
            if (packet[tryOff] == 0xFF)
            {
                var name = TryDecompressNick(packet, tryOff + 1);
                if (name != null) return name;
                continue;
            }

            var nli = ReadVarInt(packet, tryOff);
            if (nli.length <= 0 || nli.value < 1 || nli.value > 71) continue;
            int nameStart = tryOff + nli.length;
            if (nameStart + nli.value > packet.Length) continue;

            string candidate = Encoding.UTF8.GetString(packet, nameStart, nli.value);
            if (IsValidNickname(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A2Viewer TryReadLeadingJob: 패킷에서 직업 코드 추출</summary>
    private void TryExtractJobCode(byte[] packet, int offset, int entityId)
    {
        // jobCode = 2바이트 정수, IsPlausibleJobCode 범위(11~18)
        for (int i = 0; i < Math.Min(6, packet.Length - offset - 1); i++)
        {
            int val = packet[offset + i];
            if (IsPlausibleJobCode(val))
            {
                _entityJobCodes[entityId] = val;
                if (_jobNames.TryGetValue(val, out var cls))
                    SetEntityClass(entityId, cls);
                return;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 스폰 파싱 (TryParseMobInfo)
    // ══════════════════════════════════════════════════════════════════

    private bool ParseSpawn(byte[] packet, VarIntResult li, bool extra)
    {
        int offset = li.length;
        if (extra) offset++;
        if (offset + 2 >= packet.Length) return false;
        if (packet[offset] != 0x40 || packet[offset+1] != 0x36) return false;
        offset += 2;

        var entityInfo = ReadVarInt(packet, offset);
        if (entityInfo.length < 0) return false;

        // A2Viewer ScanMobCodeMarker 방식
        int codeIdx = FindBytes(packet, 0x00, 0x40, 0x02);
        if (codeIdx < 3) codeIdx = FindBytes(packet, 0x00, 0x00, 0x02);
        if (codeIdx < 3) return false;

        int mobCode = (packet[codeIdx-1] << 16) | (packet[codeIdx-2] << 8) | packet[codeIdx-3];
        if (mobCode <= 0) return false;

        _mobCodeMap[entityInfo.value] = mobCode;
        _confirmedMobIds.TryAdd(entityInfo.value, 0);  // 몬스터 확정

        EnsureMobDb();
        string mobName;
        bool isBoss;
        if (_mobDb.TryGetValue(mobCode, out var mob))
        {
            mobName = mob.name;
            isBoss  = mob.boss;
        }
        else
        {
            // mobs.json 미등록 몬스터: 패킷에서 직접 이름 추출 시도
            string? extracted = TryExtractMobNameFromPacket(packet, codeIdx + 3);
            mobName = extracted ?? $"Mob_{mobCode}";
            isBoss  = false;
        }

        // 허수아비/샌드백(훈련용 더미): DPS 추적 완전 차단
        if (mobName.Contains("허수아비") || mobName.Contains("샌드백"))
        {
            _confirmedMobIds.TryRemove(entityInfo.value, out _);
            // 파서 측 _confirmedPlayerIds 등록 → ParseBossHp 필터에서 HP 패킷 무시
            _confirmedPlayerIds.TryAdd(entityInfo.value, 0);
            // 트래커 측 _confirmedPlayerIds 등록 → ProcessEvent의 타겟 필터가 차단
            OnEntityInfoEvent?.Invoke(((uint)entityInfo.value, mobName, false, -1));
            return false;
        }

        _entityNames[entityInfo.value] = mobName;

        // 스폰 패킷에서 maxHp 추출 (몹 코드 마커 이후 67바이트 스캔)
        // A2Viewer TryParseMobInfo 방식: 0x01 마커 → currentHp VarInt → maxHp VarInt
        if (isBoss)
        {
            long maxHp = TryExtractMobMaxHp(packet, codeIdx + 3, packet.Length);
            if (maxHp > 0)
            {
                // 가라앉은 에몬 HP 보정 (A2Viewer ResolveEmonHp)
                if (mobName == "가라앉은 에몬")
                    maxHp = ResolveEmonHp(maxHp);
                OnBossHpEvent?.Invoke(((uint)entityInfo.value, mobName, maxHp, maxHp));
            }
        }

        Console.WriteLine($"[SPAWN] entityId={entityInfo.value} mobCode={mobCode} name={mobName} boss={isBoss}");
        OnSpawnEvent?.Invoke(((uint)entityInfo.value, mobName, isBoss));
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    // 데미지 파싱 (TryParseDamage)
    // ══════════════════════════════════════════════════════════════════

    private bool ParseDamage(byte[] packet, bool extra)
    {
        var li = ReadVarInt(packet);
        if (li.length < 0) return false;
        int offset = li.length;
        if (extra) offset++;
        if (offset + 2 >= packet.Length) return false;
        if (packet[offset] != 0x04 || packet[offset+1] != 0x38) return false;
        offset += 2;

        var targetInfo = ReadVarInt(packet, offset); if (targetInfo.length < 0) return false; offset += targetInfo.length;
        var switchInfo = ReadVarInt(packet, offset); if (switchInfo.length < 0) return false; offset += switchInfo.length;
        var flagInfo   = ReadVarInt(packet, offset); if (flagInfo.length < 0)   return false; offset += flagInfo.length;
        var actorInfo  = ReadVarInt(packet, offset); if (actorInfo.length < 0)  return false; offset += actorInfo.length;

        if (offset + 5 >= packet.Length) return false;
        int skillCode = ReadInt32LE(packet, offset); offset += 5;
        if (_subHitParentMap.TryGetValue(skillCode, out int parentSkill))
            skillCode = parentSkill;

        var typeInfo = ReadVarInt(packet, offset); if (typeInfo.length < 0) return false; offset += typeInfo.length;

        int sw = switchInfo.value & 0x0F;
        int categorySkip = sw switch { 4 => 8, 5 => 12, 6 => 10, 7 => 14, _ => -1 };
        if (categorySkip < 0) return false;

        if (offset + categorySkip > packet.Length) return false;
        offset += categorySkip;

        var unknownInfo = ReadVarInt(packet, offset); if (unknownInfo.length < 0) return false; offset += unknownInfo.length;
        var damageInfo  = ReadVarInt(packet, offset); if (damageInfo.length < 0)  return false; offset += damageInfo.length;

        if (actorInfo.value == targetInfo.value) return false;
        if (damageInfo.value <= 0 || damageInfo.value >= 1_000_000_000L) return true;

        long totalDamage = (long)damageInfo.value;

        // 몬스터 누적 피해 추적 (A2Viewer TryConfirmBossDeathByDamage)
        TrackMobDamage(targetInfo.value, totalDamage);

        DetectClassFromSkill(actorInfo.value, skillCode);

        // 스킬 코드 정규화: 변형 코드(12092351)를 기본 코드(12092300 등)로 통일
        // → 같은 스킬이 여러 SkillId로 분산되지 않도록
        long normalizedSkill = NormalizeSkillCode(skillCode);

        OnDamageEvent?.Invoke(new
        {
            type         = "damage",
            attackerId   = (uint)actorInfo.value,
            attackerName = GetDisplayName(actorInfo.value, $"플레이어_{actorInfo.value % 1000:D3}"),
            targetId     = (uint)targetInfo.value,
            targetName   = GetDisplayName(targetInfo.value, $"플레이어_{targetInfo.value % 1000:D3}"),
            skillId      = (uint)normalizedSkill,
            skillName    = GetSkillName(skillCode),
            damage       = totalDamage,
            isCritical   = typeInfo.value == 3,
            isDot        = false
        });
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    // DoT 파싱 (TryParseDot)
    // ══════════════════════════════════════════════════════════════════

    private bool ParseDoT(byte[] packet, bool extra)
    {
        var li = ReadVarInt(packet);
        if (li.length < 0) return false;
        int offset = li.length;
        if (extra) offset++;
        if (offset + 2 >= packet.Length) return false;
        if (packet[offset] != 0x05 || packet[offset+1] != 0x38) return false;
        offset += 2;

        var targetInfo = ReadVarInt(packet, offset); if (targetInfo.length < 0) return false; offset += targetInfo.length;

        if (offset >= packet.Length) return false;
        if ((packet[offset] & 0x02) == 0) return true;
        offset++;

        var actorInfo = ReadVarInt(packet, offset); if (actorInfo.length < 0) return false;
        if (actorInfo.value == targetInfo.value) return false;
        offset += actorInfo.length;

        var unknownInfo = ReadVarInt(packet, offset); if (unknownInfo.length < 0) return false; offset += unknownInfo.length;
        if (offset + 4 >= packet.Length) return false;
        int skillCode = ReadInt32LE(packet, offset); offset += 4;

        var damageInfo = ReadVarInt(packet, offset);
        if (damageInfo.length < 0 || damageInfo.value <= 0) return false;

        TrackMobDamage(targetInfo.value, damageInfo.value);
        DetectClassFromSkill(actorInfo.value, skillCode);

        long normalizedDotSkill = NormalizeSkillCode(skillCode);

        OnDamageEvent?.Invoke(new
        {
            type         = "damage",
            attackerId   = (uint)actorInfo.value,
            attackerName = GetDisplayName(actorInfo.value, $"플레이어_{actorInfo.value % 1000:D3}"),
            targetId     = (uint)targetInfo.value,
            targetName   = GetDisplayName(targetInfo.value, $"플레이어_{targetInfo.value % 1000:D3}"),
            skillId      = (uint)normalizedDotSkill,
            skillName    = GetSkillName(skillCode),
            damage       = (long)damageInfo.value,
            isCritical   = false,
            isDot        = true
        });
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    // 보스 HP 파싱 (TryParseBossHp)
    // ══════════════════════════════════════════════════════════════════

    private bool ParseBossHp(byte[] packet, VarIntResult li, bool extra)
    {
        int end = packet.Length;
        // A2Viewer 방식: 0x8D(141) 바이트를 전체 스캔
        for (int i = li.length; i < end - 10; i++)
        {
            if (packet[i] != 0x8D) continue;

            int offset = i + 1;
            var entityInfo = ReadVarInt(packet, offset);
            if (entityInfo.length < 0 || entityInfo.value == 0) continue;
            offset += entityInfo.length;

            // 유효성 마커: [0x02, 0x01, 0x00]
            if (offset + 7 > end) continue;
            if (packet[offset] != 0x02 || packet[offset + 1] != 0x01 || packet[offset + 2] != 0x00) continue;
            offset += 3;

            int currentHp = ReadInt32LE(packet, offset);
            offset += 4;

            // 뒤 4바이트 반드시 0x00000000 (A2Viewer 검증 조건)
            // maxHp는 이 패킷에 없음 - 스폰 패킷에서 획득
            if (offset + 4 > end || ReadInt32LE(packet, offset) != 0) continue;
            if (currentHp <= 0) continue;

            // 플레이어 엔티티 제외
            if (_confirmedPlayerIds.ContainsKey(entityInfo.value)) continue;

            string bossName = _entityNames.TryGetValue(entityInfo.value, out var bn) ? bn : $"Boss_{entityInfo.value}";
            OnBossHpEvent?.Invoke(((uint)entityInfo.value, bossName, (long)currentHp, 0));
            // HP 스냅샷 저장 (TryConfirmBossDeathByDamage용)
            long curDmg = _mobTotalDamageTaken.TryGetValue(entityInfo.value, out var d) ? d : 0;
            _mobHpSnapshot[(int)entityInfo.value] = ((long)currentHp, curDmg);
            return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════
    // 엔티티 제거 파싱 (TryParseEntityRemoved)
    // ══════════════════════════════════════════════════════════════════

    private bool TryParseEntityRemoved(byte[] packet, VarIntResult li)
    {
        int end = packet.Length;
        for (int i = li.length; i < end - 3; i++)
        {
            if (packet[i] != 0x21 || packet[i + 1] != 0x8D) continue;
            int offset = i + 2;
            var entityInfo = ReadVarInt(packet, offset);
            if (entityInfo.length < 0 || entityInfo.value == 0) continue;

            _confirmedMobIds.TryRemove(entityInfo.value, out _);
            OnEntityRemovedEvent?.Invoke((uint)entityInfo.value);
            return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════
    // 소환수 파싱 (TryParseSummon)
    // ══════════════════════════════════════════════════════════════════

    private bool TryParseSummon(byte[] packet, VarIntResult li, bool extra)
    {
        int offset = li.length;
        if (extra) offset++;
        if (offset >= packet.Length) return false;

        var actorInfo = ReadVarInt(packet, offset);
        if (actorInfo.length < 0 || actorInfo.value == 0) return false;
        offset += actorInfo.length;

        // 소환 경계 [0xFF × 8] 탐색
        int boundaryIdx = -1;
        for (int i = offset; i <= packet.Length - _summonBoundary.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < _summonBoundary.Length; j++)
                if (packet[i + j] != _summonBoundary[j]) { match = false; break; }
            if (match) { boundaryIdx = i; break; }
        }
        if (boundaryIdx < 0) return false;

        // 소환수 액터 헤더 [0x07, 0x02, 0x06] 탐색
        int searchStart = boundaryIdx + _summonBoundary.Length;
        int headerIdx   = -1;
        for (int i = searchStart; i <= packet.Length - _summonActorHeader.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < _summonActorHeader.Length; j++)
                if (packet[i + j] != _summonActorHeader[j]) { match = false; break; }
            if (match) { headerIdx = i; break; }
        }
        if (headerIdx < 0) return false;

        int summonOffset = headerIdx + _summonActorHeader.Length;
        if (summonOffset + 2 > packet.Length) return false;

        uint summonId = (uint)(packet[summonOffset] | (packet[summonOffset + 1] << 8));
        if (summonId == 0) return false;

        uint ownerId = (uint)actorInfo.value;
        _summonOwners[(int)summonId] = (int)ownerId;
        _confirmedPlayerIds.TryAdd((int)summonId, 0);
        OnSummonEvent?.Invoke((summonId, ownerId));
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    // CP 패킷 닉네임 파싱 (FindNickAndServerInCpPacket)
    // ══════════════════════════════════════════════════════════════════

    private void TryScanCombatPower(byte[] packet, int offset, int length)
    {
        int end = Math.Min(offset + length, packet.Length);
        for (int i = offset; i < end - 1; i++)
        {
            if (packet[i] != _cpPacketMarker[0] || packet[i + 1] != _cpPacketMarker[1]) continue;

            var (nick, serverId) = FindNickAndServerInCpPacket(packet, i);
            if (nick != null)
            {
                OnCombatPowerNameEvent?.Invoke((nick, serverId));
                return;
            }
        }
    }

    private static (string? nick, int serverId) FindNickAndServerInCpPacket(byte[] packet, int cpOffset)
    {
        // CP 마커 이전에서 역방향으로 [0x06, 0x00, 0x36] 탐색
        int scanFrom = Math.Max(0, cpOffset - 64);
        for (int i = cpOffset - 3; i >= scanFrom; i--)
        {
            if (i + 2 >= packet.Length) continue;
            if (packet[i] != 0x06 || packet[i + 1] != 0x00 || packet[i + 2] != 0x36) continue;

            int offset = i + 3;
            var serverInfo = ReadVarInt(packet, offset);
            if (serverInfo.length < 0 || serverInfo.value == 0) continue;
            int serverId = serverInfo.value;
            offset += serverInfo.length;

            string? nick = TryFindName(packet, offset, maxTry: 12);
            if (nick != null) return (nick, serverId);
        }
        return (null, 0);
    }

    // ══════════════════════════════════════════════════════════════════
    // A2Viewer TryConfirmBossDeathByDamage
    // 보스 HP 패킷이 없어도 데미지 누적으로 사망 확인 가능
    // ══════════════════════════════════════════════════════════════════

    private void TrackMobDamage(int entityId, long damage)
    {
        long newTotal = _mobTotalDamageTaken.AddOrUpdate(entityId, damage, (_, old) => old + damage);

        // A2Viewer TryConfirmBossDeathByDamage: HP 스냅샷 대비 누적 피해로 보스 사망 확인
        if (_mobHpSnapshot.TryGetValue(entityId, out var snap) && snap.hp > 0)
        {
            long dmgSinceSnapshot = newTotal - snap.dmgAtSnapshot;
            if (dmgSinceSnapshot >= snap.hp)
            {
                string bossName = _entityNames.TryGetValue(entityId, out var bn) ? bn : $"Boss_{entityId}";
                OnBossHpEvent?.Invoke(((uint)entityId, bossName, 0, 0));
                _mobHpSnapshot.TryRemove(entityId, out _);
            }
        }
    }

    /// <summary>
    /// A2Viewer TryParseMultiHit: 멀티히트 추가 피해 합산.
    /// damageInfo 이후 바이트에서 N개 VarInt 히트 데미지 읽기.
    /// 조건: mainDamage의 0.5% 이상이어야 유효.
    /// </summary>
    private static int ParseMultiHit(byte[] packet, int pos, int end, uint mainDamage)
    {
        // 0x01 마커 선택적 스킵
        int tryPos = pos;
        if (tryPos < end && packet[tryPos] == 0x01) tryPos++;

        var countVi = ReadVarInt(packet, tryPos);
        if (countVi.length < 0 || countVi.value == 0 || countVi.value >= 100) return 0;
        int offset = tryPos + countVi.length;

        long total = 0;
        uint read = 0;
        for (; read < countVi.value; read++)
        {
            if (offset >= end) break;
            var vi = ReadVarInt(packet, offset);
            if (vi.length < 0) break;
            total += vi.value;
            offset += vi.length;
        }

        if (read != countVi.value || total <= 0) return 0;
        if (mainDamage != 0 && total < mainDamage * 0.005) return 0;
        return (int)total;
    }

    // ══════════════════════════════════════════════════════════════════
    // 유틸리티
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A2Viewer ParseLoop/ParseCharView 방식:
    /// 원시 TCP 청크에서 NickAnchor(0x4F 0x36 0x00 0x00) 패턴을 스캔.
    /// entityId 없이 닉네임만 추출 → OnCombatPowerNameEvent로 발행.
    /// </summary>
    private void TryScanNickAnchor(byte[] chunk)
    {
        if (chunk.Length < 10) return;
        for (int i = 0; i <= chunk.Length - 8; i++)
        {
            if (chunk[i] != 0x4F || chunk[i+1] != 0x36 || chunk[i+2] != 0x00 || chunk[i+3] != 0x00) continue;
            int pos = i + 4;
            if (pos + 2 >= chunk.Length) break;
            if (chunk[pos] != 0x07) continue;

            // 0x07 뒤: VarInt(length) + nick bytes
            int nameStart = pos + 1;
            var nli = ReadVarInt(chunk, nameStart);
            if (nli.length <= 0 || nli.value < 2 || nli.value > 72) continue;
            int dataStart = nameStart + nli.length;
            if (dataStart + nli.value > chunk.Length) continue;

            string nick;
            try { nick = Encoding.UTF8.GetString(chunk, dataStart, nli.value); }
            catch { continue; }
            if (!IsValidNickname(nick)) continue;

            // serverId: nick 이후 +16 오프셋 (A2Viewer ParseCharView)
            int afterNick = dataStart + nli.value;
            int serverId = -1;
            if (afterNick + 18 <= chunk.Length)
            {
                int sid = chunk[afterNick + 16] | (chunk[afterNick + 17] << 8);
                if (_validServerIds.Contains(sid)) serverId = sid;
            }
            // serverId 못 찾으면 직접 스캔
            if (serverId < 0) serverId = FindServerId(chunk, afterNick, chunk.Length);

            Console.WriteLine($"[NICKANCHOR] nick={nick} serverId={serverId}");
            OnCombatPowerNameEvent?.Invoke((nick, serverId));
            i = dataStart + nli.value; // 중복 방지
        }
    }

    /// <summary>
    /// A2Viewer FindServerId: from 이후 75~108 바이트 범위에서
    /// 유효 서버 ID(1001~2021) 2바이트 LE 값을 찾아 반환.
    /// </summary>
    private static int FindServerId(byte[] data, int from, int end)
    {
        int scanStart = Math.Min(from + 75, end);
        int scanEnd   = Math.Min(from + 108, end) - 1;
        for (int i = scanStart; i < scanEnd; i++)
        {
            int sid = data[i] | (data[i + 1] << 8);
            if (!_validServerIds.Contains(sid)) continue;
            // 6~12 바이트 뒤에 같은 값이 반복되면 확정
            for (int j = 6; j <= 12 && i + j + 1 < end; j++)
            {
                if ((data[i+j] | (data[i+j+1] << 8)) == sid) return sid;
            }
            return sid; // 단독 등장도 수용
        }
        return -1;
    }

    /// <summary>
    /// 0x07 마커 뒤의 이름이 끝나는 오프셋을 반환.
    /// TryFindName과 같은 영역을 스캔하되 '끝 위치'를 반환.
    /// </summary>
    private static int FindAfterName07Offset(byte[] packet, int offset)
    {
        for (int i = 0; i < Math.Min(8, packet.Length - offset); i++)
        {
            int tryOff = offset + i;
            if (tryOff >= packet.Length) break;
            if (packet[tryOff] != 0x07) continue;
            var nli = ReadVarInt(packet, tryOff + 1);
            if (nli.length <= 0 || nli.value < 1 || nli.value > 72) continue;
            return tryOff + 1 + nli.length + nli.value; // 이름 끝 다음 위치
        }
        return -1;
    }

    private static int FindBytes(byte[] data, params byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
                if (data[i+j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static bool IsValidNickname(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 71) return false;
        bool hasKorOrEng = s.Any(c =>
            (c >= '\uAC00' && c <= '\uD7A3') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
        bool noControl = s.All(c => c >= 0x20);
        return hasKorOrEng && noControl;
    }

    /// <summary>
    /// A2Viewer TryParseMobInfo HP \uC2A4\uCE94 \uBC29\uC2DD:
    /// \uBAB9 \uCF54\uB4DC \uB9C8\uCEE4 \uC774\uD6C4 67\uBC14\uC774\uD2B8 \uB0B4 0x01 \uB9C8\uCEE4 \u2192 currentHp VarInt \u2192 maxHp VarInt.
    /// maxHp >= currentHp \uAC80\uC99D \uD6C4 \uBC18\uD658.
    /// </summary>
    private static long TryExtractMobMaxHp(byte[] packet, int start, int end)
    {
        int limit = Math.Min(start + 67, end - 2);
        for (int i = start; i < limit; i++)
        {
            if (packet[i] != 0x01) continue;
            int offset = i + 1;
            var currentHp = ReadVarInt(packet, offset);
            if (currentHp.length < 0 || currentHp.value == 0) continue;
            offset += currentHp.length;
            var maxHp = ReadVarInt(packet, offset);
            if (maxHp.length < 0) continue;
            if (maxHp.value >= currentHp.value && maxHp.value > 0)
                return maxHp.value;
        }
        return 0;
    }

    /// <summary>
    /// A2Viewer ResolveEmonHp: \uAC00\uB77C\uC549\uC740 \uC5D0\uBAAC HP \uBCF4\uC815.
    /// \uD328\uD0B7 HP \uAC12\uC740 \uC2E4\uC81C HP\uC640 \uB2E4\uB984 (\uB450 \uB2E8\uACC4 \uC804\uD22C \uC804\uD658 \uAE30\uC900).
    /// </summary>
    private static long ResolveEmonHp(long packetHp)
    {
        (long packet, long real)[] tiers = { (22200000L, 32200000L), (60750000L, 85100000L) };
        foreach (var (ph, rh) in tiers)
        {
            if (Math.Abs(packetHp - ph) < ph * 0.05) return rh;
        }
        if (packetHp < 15_000_000) return packetHp;
        return (long)(packetHp * 1.4);
    }

    /// <summary>
    /// \uC2A4\uD3F0 \uD328\uD0B7\uC5D0\uC11C mobs.json \uBBF8\uB4F1\uB85D \uBAAC\uC2A4\uD130\uC758 \uC774\uB984 \uCD94\uCD9C \uC2DC\uB3C4.
    /// \uBAB9 \uCF54\uB4DC \uB9C8\uCEE4 \uC774\uD6C4 \uAD6C\uAC04\uC744 \uC2A4\uCE94\uD574 \uAE38\uC774 \uC811\uB450 UTF-8 \uBB38\uC790\uC5F4\uC744 \uCC3E\uC74C.
    /// </summary>
    private static string? TryExtractMobNameFromPacket(byte[] packet, int startOffset)
    {
        int limit = Math.Min(startOffset + 96, packet.Length);
        for (int i = startOffset; i < limit; i++)
        {
            var nli = ReadVarInt(packet, i);
            if (nli.length <= 0 || nli.value < 2 || nli.value > 64) continue;
            int nameStart = i + nli.length;
            if (nameStart + nli.value > packet.Length) continue;

            string candidate;
            try { candidate = Encoding.UTF8.GetString(packet, nameStart, nli.value); }
            catch { continue; }

            if (IsValidMobName(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsValidMobName(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 2 || s.Length > 64) return false;
        // \uD55C\uAD6D\uC5B4, \uC601\uC5B4, CJK \uD1B5\uD569 \uD55C\uC790 \uC911 \uD558\uB098 \uC774\uC0C1
        bool hasMeaningful = s.Any(c =>
            (c >= '\uAC00' && c <= '\uD7A3') ||
            (c >= '\u4E00' && c <= '\u9FFF') ||
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
        bool noControl = s.All(c => c >= 0x20);
        return hasMeaningful && noControl;
    }

    // ── LZ4 순수 C# ─────────────────────────────────────────────────

    private static int LZ4Decompress(byte[] src, int si, int srcLen, byte[] dst, int di, int dstLen)
    {
        int sEnd = si + srcLen, dEnd = di + dstLen, dStart = di;
        while (si < sEnd)
        {
            int token = src[si++];
            int litLen = (token >> 4) & 0xF;
            if (litLen == 15) { int e; do { e = src[si++]; litLen += e; } while (e == 255); }
            if (di + litLen > dEnd) return -1;
            Buffer.BlockCopy(src, si, dst, di, litLen); si += litLen; di += litLen;
            if (si >= sEnd) break;
            int matchOff = src[si] | (src[si+1] << 8); si += 2;
            int matchLen = (token & 0xF) + 4;
            if (matchLen - 4 == 15) { int e; do { e = src[si++]; matchLen += e; } while (e == 255); }
            int ms = di - matchOff;
            if (ms < dStart || di + matchLen > dEnd) return -1;
            for (int k = 0; k < matchLen; k++) dst[di++] = dst[ms++];
        }
        return di - dStart;
    }

    private static int ReadInt32LE(byte[] b, int o) =>
        b[o] | (b[o+1] << 8) | (b[o+2] << 16) | (b[o+3] << 24);

    private static long ReadUInt32LE(byte[] b, int o) =>
        (long)(uint)(b[o] | (b[o+1] << 8) | (b[o+2] << 16) | (b[o+3] << 24));

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
}
