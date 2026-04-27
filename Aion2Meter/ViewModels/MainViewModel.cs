using Aion2Meter.Models;
using Aion2Meter.Services;
using System.Collections.ObjectModel;

namespace Aion2Meter.ViewModels;

/// <summary>
/// 메인 창의 ViewModel. 모든 서비스를 연결하고 UI 상태를 관리.
/// 
/// 의존성 흐름:
/// PacketCaptureService → PacketParserService → CombatTrackerService → MainViewModel → UI
/// </summary>
public class MainViewModel : BaseViewModel
{
    private readonly PacketParserService _parser;
    private readonly PacketCaptureService _capture;
    private readonly CombatTrackerService _tracker;
    private readonly SettingsService _settings;

    // ── UI 바인딩 프로퍼티 ─────────────────────────────────────

    private string _statusMessage = "대기 중...";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private string _bossName = "-";
    public string BossName
    {
        get => _bossName;
        set => SetProperty(ref _bossName, value);
    }

    private string _bossHpText = "";
    public string BossHpText
    {
        get => _bossHpText;
        set => SetProperty(ref _bossHpText, value);
    }

    private double _bossHpPercent = 1.0;
    public double BossHpPercent
    {
        get => _bossHpPercent;
        set => SetProperty(ref _bossHpPercent, value);
    }

    private string _combatTimer = "00:00";
    public string CombatTimer
    {
        get => _combatTimer;
        set => SetProperty(ref _combatTimer, value);
    }

    private bool _isInCombat = false;
    public bool IsInCombat
    {
        get => _isInCombat;
        set => SetProperty(ref _isInCombat, value);
    }

    private bool _isCapturing = false;
    public bool IsCapturing
    {
        get => _isCapturing;
        set => SetProperty(ref _isCapturing, value);
    }

    /// <summary>플레이어 목록 (CombatTrackerService의 컬렉션을 직접 노출)</summary>
    public ObservableCollection<PlayerStats> Players => _tracker.Players;

    /// <summary>플레이어 없을 때 대기 메시지 표시용</summary>
    public bool HasNoPlayers => _tracker.Players.Count == 0;

    /// <summary>전투 기록 목록</summary>
    public ObservableCollection<CombatSession> History => _tracker.History;

    public AppSettings Settings => _settings.Settings;

    // ── 커맨드 ────────────────────────────────────────────────

    public RelayCommand ResetCommand { get; }
    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    // ── 타이머 ────────────────────────────────────────────────
    private System.Timers.Timer? _timerRefresh;

    public MainViewModel()
    {
        _settings = new SettingsService();
        _settings.Load();

        _parser = new PacketParserService();
        _capture = new PacketCaptureService(_parser);
        _tracker = new CombatTrackerService();

        // ⑥ 이벤트 핸들러를 명명된 메서드로 연결 → Cleanup에서 -= 로 정확히 해제 가능
        //    람다로 연결하면 해제 불가 (다른 인스턴스로 인식됨)
        _capture.OnCombatEvent += OnCombatEvent;
        _capture.OnEntityInfo += OnEntityInfo;
        _capture.OnError += OnCaptureError;
        _parser.OnBossHp += OnBossHp;
        _tracker.Players.CollectionChanged += OnPlayersChanged;

        ResetCommand = new RelayCommand(OnReset);
        ToggleCaptureCommand = new RelayCommand(OnToggleCapture);
        SaveSettingsCommand = new RelayCommand(OnSaveSettings);

        _timerRefresh = new System.Timers.Timer(1000);
        _timerRefresh.Elapsed += (_, _) => RefreshTimer();
        _timerRefresh.Start();

        StartCapture();
    }

    // ⑥ 명명된 이벤트 핸들러
    private void OnCombatEvent(object? s, CombatEvent e) => _tracker.ProcessEvent(e);
    private void OnEntityInfo(object? s, (uint entityId, string name) e) =>
        _tracker.UpdateEntityName(e.entityId, e.name);
    private void OnCaptureError(object? s, string msg) =>
        App.Current.Dispatcher.Invoke(() => StatusMessage = msg);
    private void OnBossHp(object? s, (uint bossId, string bossName, long currentHp, long maxHp) e) =>
        UpdateBossHp(e.bossId, e.bossName, e.currentHp, e.maxHp);
    private void OnPlayersChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasNoPlayers));

    private void StartCapture()
    {
        bool ok = _capture.Start(
            _settings.Settings.NetworkInterface,
            _settings.Settings.ServerIp);

        IsCapturing = ok;
        StatusMessage = ok ? "캡처 중..." : "캡처 시작 실패";
    }

    private void OnToggleCapture()
    {
        if (IsCapturing)
        {
            _capture.Stop();
            IsCapturing = false;
            StatusMessage = "캡처 중단됨";
        }
        else
        {
            StartCapture();
        }
    }

    private void OnReset()
    {
        _parser.ClearEntityCache();
        _tracker.Reset();
    }

    private void OnSaveSettings()
    {
        _settings.Save();
        // 창 투명도 즉시 반영 (View에서 Settings.Opacity를 바인딩)
        OnPropertyChanged(nameof(Settings));
    }

    private void UpdateBossHp(uint bossId, string bossName, long current, long max)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            BossName = bossName;
            BossHpPercent = max > 0 ? (double)current / max : 0;
            BossHpText = $"{FormatNumber(current)} / {FormatNumber(max)}";
            IsInCombat = current > 0;

            if (current <= 0)
                _tracker.EndCombat();
        });
    }

    private void RefreshTimer()
    {
        var session = _tracker.CurrentSession;
        if (session?.IsActive == true)
        {
            var elapsed = session.ElapsedSeconds;
            int minutes = (int)(elapsed / 60);
            int seconds = (int)(elapsed % 60);
            App.Current.Dispatcher.Invoke(() =>
            {
                CombatTimer = $"{minutes:D2}:{seconds:D2}";
                IsInCombat = true;
            });
        }
        else
        {
            // 전투 종료 or 세션 없음 → 인디케이터 끄기
            App.Current.Dispatcher.Invoke(() => IsInCombat = false);
        }
    }

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000 ? $"{n / 1_000.0:F1}K" : n.ToString();

    public void Cleanup()
    {
        // ⑥ 이벤트 핸들러 명시적 해제 → GC가 ViewModel을 수집할 수 있도록
        _capture.OnCombatEvent -= OnCombatEvent;
        _capture.OnEntityInfo -= OnEntityInfo;
        _capture.OnError -= OnCaptureError;
        _parser.OnBossHp -= OnBossHp;
        _tracker.Players.CollectionChanged -= OnPlayersChanged;

        _timerRefresh?.Stop();
        _timerRefresh?.Dispose();
        _capture.Stop();
        _capture.Dispose();
        _tracker.Dispose();
        _settings.Save();
    }
}
