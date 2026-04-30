using Aion2Meter.Models;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace Aion2Meter.Services;

/// <summary>
/// CombatEvent를 받아 PlayerStats를 집계하고 전투 세션을 관리.
/// 
/// DispatcherTimer 사용 이유:
/// System.Timers.Timer는 백그라운드 스레드에서 콜백 실행 →
/// ObservableCollection 조작 시 cross-thread 예외 또는 데드락 발생.
/// DispatcherTimer는 UI 스레드에서 실행되므로 안전.
/// </summary>
public class CombatTrackerService : IDisposable
{
    private readonly object _lock = new();
    private CombatSession? _currentSession;
    private DispatcherTimer? _dpsTimer;
    private bool _disposed = false;
    private long _totalPartyDamageCache = 0;

    // 닉네임 캐시: 패킷 수신 순서와 무관하게 이름을 보존
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _nameCache = new();

    // 서버 ID 캐시: Plaync API 조회에 사용
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> _serverIdCache = new();

    // SPAWN/ENTITY 패킷 기반 확정 분류 (A2Viewer ActorStats.IsPlayer 방식)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _confirmedPlayerIds = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _confirmedMobIds    = new();

    // 소환수 → 주인 매핑 (A2Viewer TryParseSummon)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> _summons = new();

    /// <summary>ENTITY 패킷 수신 시 플레이어로 등록</summary>
    public void RegisterPlayer(uint entityId)   => _confirmedPlayerIds.TryAdd(entityId, 0);

    /// <summary>SPAWN 패킷 수신 시 몬스터로 등록</summary>
    public void RegisterMob(uint entityId)      => _confirmedMobIds.TryAdd(entityId, 0);

    /// <summary>소환수 등록 (소환수의 피해를 주인에게 귀속)</summary>
    public void RegisterSummon(uint summonId, uint ownerId)
    {
        _summons[summonId] = ownerId;
        _confirmedPlayerIds.TryAdd(summonId, 0);
    }

    /// <summary>엔티티 제거 (몬스터 사망/해제 시 호출)</summary>
    public void EntityRemoved(uint entityId)
    {
        _confirmedMobIds.TryRemove(entityId, out _);
        _summons.TryRemove(entityId, out _);
    }

    /// <summary>서버 ID 등록 (Plaync API 조회에 사용)</summary>
    public void RegisterServerId(uint entityId, int serverId)
    {
        if (serverId <= 0) return;
        _serverIdCache[entityId] = serverId;
        lock (_lock)
        {
            if (_currentSession?.Players.TryGetValue(entityId, out var p) == true)
                p.ServerId = serverId;
        }
    }

    /// <summary>Plaync API 조회 결과로 플레이어 전투력 업데이트</summary>
    public void UpdatePlayerCombatPower(uint entityId, int cp)
    {
        if (cp <= 0) return;
        lock (_lock)
        {
            if (_currentSession?.Players.TryGetValue(entityId, out var p) == true)
                p.CombatPower = cp;
        }
    }

    /// <summary>서버 ID가 있는 미확인 플레이어 목록 반환 (API 조회 트리거용)</summary>
    public IEnumerable<(uint entityId, string name, int serverId)> GetPlayersNeedingLookup()
    {
        lock (_lock)
        {
            if (_currentSession == null) yield break;
            foreach (var kv in _currentSession.Players)
            {
                if (kv.Value.CombatPower > 0) continue;          // 이미 조회됨
                if (!_serverIdCache.TryGetValue(kv.Key, out var sid) || sid <= 0) continue;
                if (!_nameCache.TryGetValue(kv.Key, out var name)) continue;
                if (name.StartsWith("플레이어_") || System.Text.RegularExpressions.Regex.IsMatch(
                    name, @"^(검성|수호성|살성|궁성|마도성|정령성|치유성|호법성)\d*$")) continue;
                yield return (kv.Key, name, sid);
            }
        }
    }

    /// <summary>
    /// CP 패킷 닉네임 등록. entityId 없이 이름만 수신될 때,
    /// 현재 세션에서 직업명으로만 표시되는 미확인 플레이어에 매핑 시도.
    /// </summary>
    public void RegisterCpName(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return;
        // 이미 이 닉네임이 캐시에 있으면 중복 처리 불필요
        if (_nameCache.Values.Contains(nick)) return;

        lock (_lock)
        {
            if (_currentSession == null) return;
            // 현재 세션에서 아직 실명이 없는 플레이어 중 하나에 할당
            // (복수 미확인자가 있으면 순서대로 할당 - 불완전하지만 없는 것보다 낫다)
            foreach (var kv in _currentSession.Players)
            {
                if (_nameCache.ContainsKey(kv.Key)) continue;
                if (!kv.Value.Name.StartsWith("플레이어_") &&
                    !System.Text.RegularExpressions.Regex.IsMatch(kv.Value.Name, @"^(검성|수호성|살성|궁성|마도성|정령성|치유성|호법성)\d*$"))
                    continue;
                // 직업명이나 플레이어_XXX인 경우 실명으로 교체
                _nameCache[kv.Key] = nick;
                kv.Value.Name = nick;
                break;
            }
        }
    }

    // 자동 종료: 마지막 데미지 이후 N초 무활동
    private DateTime _lastDamageTime = DateTime.MinValue;

    public uint LocalPlayerId { get; set; }
    public bool FilterByBossTarget { get; set; } = false;
    public bool FilterByKnownPlayers { get; set; } = true;
    public string SortBy { get; set; } = "TotalDamage";
    public int AutoEndSeconds { get; set; } = 10;
    public bool PinLocalPlayer { get; set; } = false;
    /// <summary>보스 HP 패킷 기준 생존 여부 — true이면 자동 종료 억제</summary>
    public bool BossIsAlive { get; set; } = false;

    public ObservableCollection<PlayerStats> Players { get; } = new();
    public ObservableCollection<CombatSession> History { get; } = new();

    public bool IsInCombat => _currentSession?.IsActive == true;
    public CombatSession? CurrentSession => _currentSession;

    public event EventHandler? OnCombatUpdated;
    public event EventHandler<CombatSession>? OnCombatEnded;

    public CombatTrackerService() { }

    /// <summary>UI 스레드에서 호출해야 함 (DispatcherTimer 생성)</summary>
    public void StartTimer()
    {
        _dpsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _dpsTimer.Tick += (_, _) => RefreshDps();
        _dpsTimer.Start();
    }

    public void ProcessEvent(CombatEvent evt)
    {
        if (_disposed) return;

        lock (_lock)
        {
            // 소환수 피해 → 주인에게 귀속 (A2Viewer 소환수 처리)
            if (_summons.TryGetValue(evt.AttackerId, out var ownerId))
            {
                evt.AttackerId   = ownerId;
                evt.AttackerName = _nameCache.TryGetValue(ownerId, out var ownerName) ? ownerName : evt.AttackerName;
            }

            // 타겟이 확정된 플레이어이면 무시
            // (몬스터→플레이어, 플레이어→플레이어 데미지 제외)
            if (_confirmedPlayerIds.ContainsKey(evt.TargetId)) return;
            if (_nameCache.ContainsKey(evt.TargetId) && !_confirmedMobIds.ContainsKey(evt.TargetId)) return;

            // 확인된 몬스터는 공격자로 처리하지 않음 (몬스터→몬스터 데미지 제외)
            if (_confirmedMobIds.ContainsKey(evt.AttackerId)) return;

            // ── 세션 시작 ────────────────────────────────────────────
            if (_currentSession == null || !_currentSession.IsActive)
            {
                _currentSession = new CombatSession
                {
                    BossId    = evt.TargetId,
                    BossName  = _nameCache.TryGetValue(evt.TargetId, out var bossName) ? bossName : evt.TargetName,
                    StartTime = DateTime.Now
                };
                _totalPartyDamageCache = 0;
            }
            else if (FilterByBossTarget && evt.TargetId != _currentSession.BossId)
            {
                // 보스타겟 필터 ON 상태에서 다른 타겟 → 무시
                return;
            }

            var session = _currentSession!;

            // FilterByKnownPlayers=ON : ENTITY 패킷으로 확인된 플레이어(파티원)만 표시
            // 단, 본인(LocalPlayerId)은 항상 표시
            // 예외: 아직 ENTITY 패킷이 한 건도 수신되지 않은 경우(미터기 시작 전 입장 등)
            //   → 필터를 완화해 확인된 몬스터가 아닌 모든 공격자를 허용
            bool isLocalPlayer = LocalPlayerId != 0 && evt.AttackerId == LocalPlayerId;
            if (FilterByKnownPlayers && !isLocalPlayer && !_nameCache.ContainsKey(evt.AttackerId))
            {
                if (_confirmedPlayerIds.Count > 0) return;
                // _confirmedPlayerIds가 비어있으면: 이미 위에서 _confirmedMobIds 체크로 몬스터 제외됨
            }

            if (session.Events.Count >= 5000)
                session.Events.RemoveAt(0);
            session.Events.Add(evt);

            if (!session.Players.ContainsKey(evt.AttackerId))
                session.Players[evt.AttackerId] = new PlayerStats
                {
                    EntityId      = evt.AttackerId,
                    Name          = _nameCache.TryGetValue(evt.AttackerId, out var cachedName) ? cachedName : evt.AttackerName,
                    IsLocalPlayer = evt.AttackerId == LocalPlayerId
                };

            var player = session.Players[evt.AttackerId];
            // 캐시에 실제 이름이 있으면 사용, 없으면 이벤트 이름 사용 (플레이어_XXX 덮어쓰기 방지)
            if (_nameCache.TryGetValue(evt.AttackerId, out var realName))
                player.Name = realName;
            else if (!player.Name.StartsWith("플레이어_"))
                player.Name = evt.AttackerName;
            player.TotalDamage += evt.Damage;
            player.HitCount++;
            if (evt.IsCritical) player.CritCount++;

            _totalPartyDamageCache += evt.Damage;

            if (!player.Skills.ContainsKey(evt.SkillId))
                player.Skills[evt.SkillId] = new SkillStats
                {
                    SkillId   = evt.SkillId,
                    SkillName = evt.SkillName
                };

            var skill = player.Skills[evt.SkillId];
            skill.TotalDamage += evt.Damage;
            skill.HitCount++;
            if (evt.IsCritical) skill.CritCount++;
            if (evt.Damage > skill.MaxHit) skill.MaxHit = evt.Damage;
            skill.IsDot = evt.IsDot;

            // MaxHit, DoT 분리
            if (evt.Damage > player.MaxHit) player.MaxHit = evt.Damage;
            if (evt.IsDot) player.DotDamage += evt.Damage;
            else player.DirectDamage += evt.Damage;

            _lastDamageTime = DateTime.Now;
        }

        // UI 업데이트는 DispatcherTimer에서 처리 (500ms마다)
        // 매 이벤트마다 UI 갱신하면 과부하
    }

    public void UpdateEntityName(uint entityId, string name)
    {
        if (_disposed) return;
        _nameCache[entityId] = name;
        lock (_lock)
        {
            if (_currentSession?.Players.TryGetValue(entityId, out var player) == true)
                player.Name = name;
            // 보스 이름도 업데이트
            if (_currentSession != null && _currentSession.BossId == entityId)
                _currentSession.BossName = name;
        }
    }

    public void Reset()
    {
        if (_disposed) return;
        CombatSession? toSave = null;
        lock (_lock)
        {
            if (_currentSession != null)
            {
                _currentSession.EndTime = DateTime.Now;
                if (_currentSession.Events.Count > 0)
                    toSave = _currentSession;
            }
            _currentSession = null;
            _totalPartyDamageCache = 0;
        }
        if (toSave != null)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(() => SaveToHistory(toSave));
            else
                SaveToHistory(toSave);
        }
    }

    public void EndCombat()
    {
        if (_disposed) return;
        CombatSession? ended = null;
        lock (_lock)
        {
            if (_currentSession == null) return;
            _currentSession.EndTime = DateTime.Now;
            ended = _currentSession;
        }
        if (ended == null) return;

        // SaveToHistory는 UI 스레드에서만 가능
        // DispatcherTimer(UI스레드)에서 호출되면 직접, 아니면 Invoke
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => SaveToHistory(ended));
        else
            SaveToHistory(ended);

        OnCombatEnded?.Invoke(this, ended);
    }

    private void SaveToHistory(CombatSession session)
    {
        session.TotalPartyDamage = _totalPartyDamageCache;
        // DispatcherTimer 콜백(UI 스레드)에서 호출되므로 BeginInvoke 불필요
        History.Insert(0, session);
        while (History.Count > 20)
            History.RemoveAt(History.Count - 1);
    }

    /// <summary>
    /// DispatcherTimer Tick에서 호출 → UI 스레드에서 실행됨.
    /// BeginInvoke 불필요, cross-thread 없음.
    /// </summary>
    private void RefreshDps()
    {
        if (_disposed) return;

        List<PlayerStats>? snapshot = null;
        double elapsed = 0;
        long totalDmg = 0;

        lock (_lock)
        {
            if (_currentSession == null)
            {
                // 세션 없으면 Players 비우기만
                if (Players.Count > 0) Players.Clear();
                return;
            }
            elapsed = _currentSession.ElapsedSeconds;
            if (elapsed < 0.1) return;
            snapshot = _currentSession.Players.Values.ToList();
            totalDmg = _totalPartyDamageCache;
        }

        if (snapshot == null) return;

        // DPS 계산 및 정렬
        foreach (var player in snapshot)
        {
            player.Dps           = player.TotalDamage / elapsed;
            player.DamagePercent = totalDmg > 0 ? (double)player.TotalDamage / totalDmg : 0;
        }

        // 자동 종료 체크 (보스가 살아있으면 패턴 파훼 구간이므로 억제)
        if (!BossIsAlive &&
            _lastDamageTime != DateTime.MinValue &&
            (DateTime.Now - _lastDamageTime).TotalSeconds > AutoEndSeconds &&
            _currentSession?.IsActive == true)
        {
            EndCombat();
            return;
        }

        var sorted = SortBy switch
        {
            "Dps"      => snapshot.OrderByDescending(p => p.Dps).ToList(),
            "HitCount" => snapshot.OrderByDescending(p => p.HitCount).ToList(),
            _          => snapshot.OrderByDescending(p => p.TotalDamage).ToList()
        };

        // 본인 캐릭터 상단 고정
        if (PinLocalPlayer && LocalPlayerId != 0)
        {
            var local = sorted.FirstOrDefault(p => p.EntityId == LocalPlayerId);
            if (local != null)
            {
                sorted.Remove(local);
                sorted.Insert(0, local);
            }
        }

        // UI 업데이트 (이미 UI 스레드이므로 직접 조작)
        Players.Clear();
        foreach (var p in sorted)
            Players.Add(p);

        OnCombatUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dpsTimer?.Stop();
        _dpsTimer = null;
        GC.SuppressFinalize(this);
    }
}
