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

    public uint LocalPlayerId { get; set; }

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

            var session = _currentSession!;

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
        lock (_lock)
        {
            if (_currentSession != null)
            {
                _currentSession.EndTime = DateTime.Now;
                if (_currentSession.Events.Count > 0)
                    SaveToHistory(_currentSession);
            }
            _currentSession = null;
            _totalPartyDamageCache = 0;
        }
        // DispatcherTimer가 다음 tick에 UI 갱신
    }

    public void EndCombat()
    {
        if (_disposed) return;
        CombatSession? ended = null;
        lock (_lock)
        {
            if (_currentSession == null) return;
            _currentSession.EndTime = DateTime.Now;
            SaveToHistory(_currentSession);
            ended = _currentSession;
        }
        if (ended != null)
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

        var sorted = snapshot.OrderByDescending(p => p.TotalDamage).ToList();

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
