using Aion2Meter.Models;
using System.Collections.ObjectModel;

namespace Aion2Meter.Services;

/// <summary>
/// CombatEvent를 받아 PlayerStats를 집계하고 전투 세션을 관리.
///
/// 최적화 포인트:
/// ① Events 상한 제한: 최근 5000개만 유지 → 메모리 무제한 증가 방지
/// ② lock 범위 최소화: 스냅샷 복사 후 lock 해제, Dispatcher.Invoke는 lock 밖
/// ③ TotalPartyDamage 캐싱: 매번 LINQ Sum 대신 이벤트마다 누적
/// ④ Dispose 패턴: _disposed 플래그로 Timer 콜백 후처리 방지
/// </summary>
public class CombatTrackerService : IDisposable
{
    private readonly object _lock = new();
    private CombatSession? _currentSession;
    private System.Timers.Timer? _dpsTimer;
    private bool _disposed = false;

    // ③ TotalPartyDamage 캐싱 — ProcessEvent마다 누적, LINQ Sum 반복 제거
    private long _totalPartyDamageCache = 0;

    public uint LocalPlayerId { get; set; }

    public ObservableCollection<PlayerStats> Players { get; } = new();
    public ObservableCollection<CombatSession> History { get; } = new();

    public bool IsInCombat => _currentSession?.IsActive == true;
    public CombatSession? CurrentSession => _currentSession;

    public event EventHandler? OnCombatUpdated;
    public event EventHandler<CombatSession>? OnCombatEnded;

    public CombatTrackerService()
    {
        _dpsTimer = new System.Timers.Timer(500);
        _dpsTimer.Elapsed += (s, e) => RefreshDps();
        // Timer는 명시적으로 Start() 호출 시 시작
    }

    public void StartTimer() => _dpsTimer?.Start();

    public void ProcessEvent(CombatEvent evt)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_currentSession == null || !_currentSession.IsActive)
            {
                StartNewSession(evt.TargetId, evt.TargetName);
                _totalPartyDamageCache = 0;
            }

            var session = _currentSession!;

            // ① Events 상한: 최근 5000개 유지 (5000 x ~100bytes = ~500KB)
            if (session.Events.Count >= 5000)
                session.Events.RemoveAt(0);
            session.Events.Add(evt);

            if (!session.Players.ContainsKey(evt.AttackerId))
            {
                session.Players[evt.AttackerId] = new PlayerStats
                {
                    EntityId = evt.AttackerId,
                    Name = evt.AttackerName,
                    IsLocalPlayer = evt.AttackerId == LocalPlayerId
                };
            }

            var player = session.Players[evt.AttackerId];
            player.Name = evt.AttackerName;
            player.TotalDamage += evt.Damage;
            player.HitCount++;
            if (evt.IsCritical) player.CritCount++;

            // ③ 누적 캐시 갱신
            _totalPartyDamageCache += evt.Damage;

            if (!player.Skills.ContainsKey(evt.SkillId))
            {
                player.Skills[evt.SkillId] = new SkillStats
                {
                    SkillId = evt.SkillId,
                    SkillName = evt.SkillName
                };
            }
            var skill = player.Skills[evt.SkillId];
            skill.TotalDamage += evt.Damage;
            skill.HitCount++;
            if (evt.IsCritical) skill.CritCount++;
        }

        RefreshUi();
    }

    public void UpdateEntityName(uint entityId, string name)
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_currentSession?.Players.TryGetValue(entityId, out var player) == true)
                player.Name = name;
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
        RefreshUi();
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
        // ② lock 밖에서 이벤트 발행 (핸들러 내 lock 재진입 → 데드락 방지)
        if (ended != null)
            OnCombatEnded?.Invoke(this, ended);
        RefreshUi();
    }

    private void StartNewSession(uint bossId, string bossName)
    {
        _currentSession = new CombatSession
        {
            BossId = bossId,
            BossName = bossName,
            StartTime = DateTime.Now
        };
    }

    private void SaveToHistory(CombatSession session)
    {
        // 히스토리 저장 전 총 피해량 확정
        session.TotalPartyDamage = _totalPartyDamageCache;

        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            History.Insert(0, session);
            while (History.Count > 20)
                History.RemoveAt(History.Count - 1);
        });
    }

    private void RefreshDps()
    {
        if (_disposed) return;

        // ② 스냅샷 복사 후 lock 해제 → DPS 계산은 lock 밖에서
        List<PlayerStats>? snapshot = null;
        double elapsed = 0;
        long totalDmg = 0;

        lock (_lock)
        {
            if (_currentSession == null) return;
            elapsed = _currentSession.ElapsedSeconds;
            if (elapsed < 0.1) return;
            snapshot = _currentSession.Players.Values.ToList();
            totalDmg = _totalPartyDamageCache;
        }

        foreach (var player in snapshot)
        {
            player.Dps = player.TotalDamage / elapsed;
            player.DamagePercent = totalDmg > 0 ? (double)player.TotalDamage / totalDmg : 0;
        }

        RefreshUi();
    }

    private void RefreshUi()
    {
        if (_disposed) return;

        List<PlayerStats>? snapshot = null;
        lock (_lock)
        {
            if (_currentSession != null)
                snapshot = _currentSession.Players.Values
                    .OrderByDescending(p => p.TotalDamage)
                    .ToList();
        }

        // 세션도 없고 플레이어도 없으면 UI 갱신 불필요
        if (snapshot == null && Players.Count == 0) return;

        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            Players.Clear();
            if (snapshot != null)
                foreach (var p in snapshot)
                    Players.Add(p);
            OnCombatUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    // ④ 표준 Dispose 패턴 — GC.SuppressFinalize로 finalizer 중복 실행 방지
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _dpsTimer?.Stop();
            _dpsTimer?.Dispose();
            _dpsTimer = null;
        }
    }
}
