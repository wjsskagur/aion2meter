using Aion2Meter.Models;
using System.Collections.ObjectModel;

namespace Aion2Meter.Services;

/// <summary>
/// CombatEvent를 받아 PlayerStats를 집계하고 전투 세션을 관리.
/// 
/// 설계 포인트:
/// - UI 스레드와 분리하기 위해 lock으로 동기화
/// - DPS 계산은 Timer로 주기적 갱신 (실시간 느낌)
/// - ObservableCollection을 직접 관리해 ViewModel에서 바인딩
/// </summary>
public class CombatTrackerService
{
    private readonly object _lock = new();
    private CombatSession? _currentSession;
    private System.Timers.Timer? _dpsTimer;

    // 로컬 플레이어 EntityId (OPCODE_ENTITY_INFO에서 자신의 패킷으로 설정)
    public uint LocalPlayerId { get; set; }

    /// <summary>현재 전투 세션의 플레이어 목록 (UI 바인딩용)</summary>
    public ObservableCollection<PlayerStats> Players { get; } = new();

    /// <summary>전투 기록 (최대 MaxHistoryCount개)</summary>
    public ObservableCollection<CombatSession> History { get; } = new();

    public bool IsInCombat => _currentSession?.IsActive == true;
    public CombatSession? CurrentSession => _currentSession;

    public event EventHandler? OnCombatUpdated;
    public event EventHandler<CombatSession>? OnCombatEnded;

    public CombatTrackerService()
    {
        // 500ms마다 DPS 재계산 (너무 자주 하면 CPU 낭비)
        _dpsTimer = new System.Timers.Timer(500);
        _dpsTimer.Elapsed += (s, e) => RefreshDps();
        _dpsTimer.Start();
    }

    /// <summary>
    /// 패킷 파서에서 전달된 CombatEvent 처리.
    /// 별도 스레드에서 호출될 수 있으므로 lock 필수.
    /// </summary>
    public void ProcessEvent(CombatEvent evt)
    {
        lock (_lock)
        {
            // 첫 이벤트 → 세션 시작
            if (_currentSession == null || !_currentSession.IsActive)
            {
                StartNewSession(evt.TargetId, evt.TargetName);
            }

            var session = _currentSession!;
            session.Events.Add(evt);

            // 공격자 통계 업데이트
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
            player.Name = evt.AttackerName; // 이름 갱신 (뒤늦게 수신될 수 있음)
            player.TotalDamage += evt.Damage;
            player.HitCount++;
            if (evt.IsCritical) player.CritCount++;

            // 스킬별 통계
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

    /// <summary>엔티티 이름 정보 수신 시 기존 데이터 이름 갱신</summary>
    public void UpdateEntityName(uint entityId, string name)
    {
        lock (_lock)
        {
            if (_currentSession?.Players.TryGetValue(entityId, out var player) == true)
                player.Name = name;
        }
    }

    /// <summary>수동 리셋 (사용자가 버튼 클릭)</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_currentSession != null)
            {
                _currentSession.EndTime = DateTime.Now;
                if (_currentSession.Events.Count > 0)
                    SaveToHistory(_currentSession);
            }
            _currentSession = null;
        }
        RefreshUi();
    }

    /// <summary>보스 사망 등으로 전투 자동 종료</summary>
    public void EndCombat()
    {
        lock (_lock)
        {
            if (_currentSession == null) return;
            _currentSession.EndTime = DateTime.Now;
            SaveToHistory(_currentSession);
            OnCombatEnded?.Invoke(this, _currentSession);
        }
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
        // UI 스레드에서 ObservableCollection 조작
        App.Current?.Dispatcher.Invoke(() =>
        {
            History.Insert(0, session);
            while (History.Count > 20)
                History.RemoveAt(History.Count - 1);
        });
    }

    /// <summary>
    /// DPS 및 비율 재계산.
    /// Timer 콜백(별도 스레드)에서 호출되므로 Dispatcher로 UI 갱신.
    /// </summary>
    private void RefreshDps()
    {
        if (_currentSession == null) return;

        lock (_lock)
        {
            double elapsed = _currentSession.ElapsedSeconds;
            if (elapsed < 0.1) return;

            long totalDmg = _currentSession.TotalPartyDamage;
            foreach (var player in _currentSession.Players.Values)
            {
                player.Dps = elapsed > 0 ? player.TotalDamage / elapsed : 0;
                player.DamagePercent = totalDmg > 0 ? (double)player.TotalDamage / totalDmg : 0;
            }
        }

        RefreshUi();
    }

    /// <summary>
    /// UI 스레드에서 Players ObservableCollection 동기화.
    /// 
    /// 왜 매번 Clear/Add를 하는가:
    /// ObservableCollection은 Add/Remove 시 UI 갱신이 자동이지만,
    /// 정렬 순서 변경은 Clear 후 재삽입이 가장 단순함.
    /// 성능 민감하면 SortedObservableCollection 구현 고려.
    /// </summary>
    private void RefreshUi()
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            lock (_lock)
            {
                Players.Clear();
                if (_currentSession == null) return;

                // 피해량 내림차순 정렬
                var sorted = _currentSession.Players.Values
                    .OrderByDescending(p => p.TotalDamage);

                foreach (var p in sorted)
                    Players.Add(p);
            }
            OnCombatUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose()
    {
        _dpsTimer?.Stop();
        _dpsTimer?.Dispose();
    }
}
