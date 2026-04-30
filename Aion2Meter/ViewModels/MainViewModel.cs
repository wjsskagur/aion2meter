using Aion2Meter.Models;
using Aion2Meter.Services;
using System.Collections.ObjectModel;

namespace Aion2Meter.ViewModels;

/// <summary>
/// 메인 창의 ViewModel.
/// 의존성 흐름:
///   CaptureProcessService (별도 프로세스) → Named Pipe → CombatTrackerService → MainViewModel → UI
/// </summary>
public class MainViewModel : BaseViewModel
{
    private readonly CaptureProcessService _capture;
    private readonly CombatTrackerService _tracker;
    private readonly SettingsService _settings;
    private readonly CombatUploadService _uploader = new();
    private System.Windows.Threading.DispatcherTimer? _timerRefresh;
    private DateTime _lastApiLookup = DateTime.MinValue;

    // ── UI 바인딩 프로퍼티 ────────────────────────────────────

    private string _statusMessage = "시작 대기 중...";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private bool _isCapturing = false;
    public bool IsCapturing
    {
        get => _isCapturing;
        set => SetProperty(ref _isCapturing, value);
    }

    private bool _isInCombat = false;
    public bool IsInCombat
    {
        get => _isInCombat;
        set => SetProperty(ref _isInCombat, value);
    }

    // 보스 패널 전용: HP 패킷 기준 (IsInCombat과 별도 관리)
    private bool _isBossActive = false;
    public bool IsBossActive
    {
        get => _isBossActive;
        set => SetProperty(ref _isBossActive, value);
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

    private double _bossHpPercent = 0;
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

    public ObservableCollection<PlayerStats> Players => _tracker.Players;
    public bool HasNoPlayers => _tracker.Players.Count == 0;
    public ObservableCollection<CombatSession> History => _tracker.History;
    public CombatSession? CurrentSession => _tracker.CurrentSession;
    public double ElapsedSeconds => _tracker.CurrentSession?.ElapsedSeconds ?? 0;
    public AppSettings Settings => _settings.Settings;

    public double RowHeight     => Settings.CompactMode ? 16 : 22;
    public double NameFontSize   => Math.Round(11 * Settings.FontScale, 1);
    public double DamageFontSize => Math.Round(10 * Settings.FontScale, 1);
    public double DpsFontSize    => Math.Round(11 * Settings.FontScale, 1);

    // ── 커맨드 ────────────────────────────────────────────────
    public RelayCommand ResetCommand { get; }
    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    public MainViewModel()
    {
        _settings = new SettingsService();
        _settings.Load();

        _capture = new CaptureProcessService();
        _tracker = new CombatTrackerService();

        // 이벤트 연결 (명명된 메서드 → Cleanup에서 정확히 해제 가능)
        _capture.OnCombatEvent    += OnCombatEventReceived;
        _capture.OnEntityInfo     += OnEntityInfoReceived;
        _capture.OnSpawn          += OnSpawnReceived;
        _capture.OnBossHp         += OnBossHpReceived;
        _capture.OnEntityRemoved  += OnEntityRemovedReceived;
        _capture.OnSummon         += OnSummonReceived;
        _capture.OnCpName         += OnCpNameReceived;
        _capture.OnError          += OnCaptureError;
        _capture.OnStatus         += OnCaptureStatus;
        _tracker.Players.CollectionChanged += OnPlayersChanged;
        _tracker.OnCombatEnded += OnCombatEndedForUpload;

        ResetCommand         = new RelayCommand(OnReset);
        ToggleCaptureCommand = new RelayCommand(OnToggleCapture);
        SaveSettingsCommand  = new RelayCommand(OnSaveSettings);

        _timerRefresh = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timerRefresh.Tick += (_, _) => RefreshTimer();
        // Timer는 StartCapture() 호출 시 시작
    }

    // ── 이벤트 핸들러 ─────────────────────────────────────────

    private void OnCombatEndedForUpload(object? s, CombatSession session)
    {
        if (!_settings.Settings.AutoUpload) return;
        // 백그라운드로 전송 (게임 플레이 방해 없음)
        _ = Task.Run(async () =>
        {
            bool ok = await _uploader.UploadAsync(session, _settings.Settings);
            WriteLog($"Upload result: {ok}");
        });
    }

    private void OnCombatEventReceived(object? s, CombatEvent e) =>
        _tracker.ProcessEvent(e);

    private void OnEntityInfoReceived(object? s, (uint entityId, string name, bool isLocalPlayer, int serverId) e)
    {
        if (e.isLocalPlayer) _tracker.LocalPlayerId = e.entityId;
        _tracker.RegisterPlayer(e.entityId);
        _tracker.UpdateEntityName(e.entityId, e.name);
        if (e.serverId > 0) _tracker.RegisterServerId(e.entityId, e.serverId);
    }

    private void OnSpawnReceived(object? s, (uint entityId, string name, bool isBoss) e)
    {
        _tracker.RegisterMob(e.entityId);      // 몬스터 확정
        _tracker.UpdateEntityName(e.entityId, e.name);
        if (e.isBoss)
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (BossName == $"Boss_{e.entityId}" || string.IsNullOrEmpty(BossName))
                    BossName = e.name;
            });
    }

    private long _bossMaxHp = 0;

    private void OnEntityRemovedReceived(object? s, uint entityId) =>
        _tracker.EntityRemoved(entityId);

    private void OnSummonReceived(object? s, (uint summonId, uint ownerId) e) =>
        _tracker.RegisterSummon(e.summonId, e.ownerId);

    private void OnCpNameReceived(object? s, string nick) =>
        _tracker.RegisterCpName(nick);

    // Plaync API 조회: 전투가 시작된 후 serverId가 있는 플레이어에 대해 CP를 가져옴
    private void TriggerCombatPowerLookup()
    {
        foreach (var (entityId, name, serverId) in _tracker.GetPlayersNeedingLookup())
        {
            var eid = entityId;
            _ = Task.Run(async () =>
            {
                var result = await PlayncApiService.FetchCombatPowerAsync(name, serverId);
                if (result.HasValue)
                    _tracker.UpdatePlayerCombatPower(eid, result.Value.cp);
            });
        }
    }

    private void OnBossHpReceived(object? s, (uint bossId, string bossName, long currentHp, long maxHp) e)
    {
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            BossName = e.bossName;

            // maxHp 추적 (처음 받은 최댓값 유지)
            if (e.maxHp > 0) _bossMaxHp = e.maxHp;
            else if (e.currentHp > _bossMaxHp) _bossMaxHp = e.currentHp;

            BossHpPercent = _bossMaxHp > 0 ? (double)e.currentHp / _bossMaxHp : 1.0;
            BossHpText    = _bossMaxHp > 0
                ? $"{FormatNumber(e.currentHp)} / {FormatNumber(_bossMaxHp)}"
                : FormatNumber(e.currentHp);
            IsBossActive          = e.currentHp > 0;
            _tracker.BossIsAlive  = e.currentHp > 0;

            if (e.currentHp <= 0)
                _tracker.EndCombat();
        });
    }

    private void OnCaptureError(object? s, string msg) =>
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusMessage = msg.Split('\n')[0];
            IsCapturing = false;
        });

    private void OnCaptureStatus(object? s, string msg) =>
        App.Current?.Dispatcher.BeginInvoke(() => StatusMessage = msg);

    private void OnPlayersChanged(object? s,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasNoPlayers));

    // ── 캡처 제어 ─────────────────────────────────────────────

    /// <summary>
    /// 캡처 시작. MainWindow.Loaded 이후에만 호출.
    /// CaptureProcessService가 별도 프로세스를 띄우므로 UI 블로킹 없음.
    /// </summary>
    public void StartCapture()
    {
        WriteLog("StartCapture - begin");
        StatusMessage = "캡처 초기화 중...";

        // FilterByBossTarget은 항상 true (보스 타겟만 추적)
        _tracker.FilterByBossTarget   = true;
        _tracker.FilterByKnownPlayers = _settings.Settings.FilterByKnownPlayers;
        _tracker.SortBy               = _settings.Settings.SortBy;
        _tracker.AutoEndSeconds       = _settings.Settings.AutoEndSeconds;
        _tracker.PinLocalPlayer       = _settings.Settings.PinLocalPlayer;

        WriteLog("StartCapture - calling _capture.Start");
        _capture.Start(_settings.Settings.AionPort);
        WriteLog("StartCapture - _capture.Start returned");
        _tracker.StartTimer();
        _timerRefresh?.Start();
        IsCapturing = true;
        WriteLog("StartCapture - done");
    }

    public void NotifyChanged(string propertyName) => OnPropertyChanged(propertyName);

    private static void WriteLog(string msg)
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Aion2Meter", "init.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
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
        _tracker.Reset();
        BossName             = "-";
        BossHpText           = "";
        BossHpPercent        = 0;
        IsBossActive         = false;
        _tracker.BossIsAlive = false;
        _bossMaxHp           = 0;
        // 캡처 중이 아니면 재시도
        if (!IsCapturing)
            StartCapture();
    }

    private void OnSaveSettings()
    {
        _settings.Save();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(NameFontSize));
        OnPropertyChanged(nameof(DamageFontSize));
        OnPropertyChanged(nameof(DpsFontSize));
    }

    // ── 타이머 ────────────────────────────────────────────────

    // DispatcherTimer Tick → 이미 UI 스레드, BeginInvoke 불필요
    private void RefreshTimer()
    {
        var session = _tracker.CurrentSession;
        if (session?.IsActive == true)
        {
            var elapsed = session.ElapsedSeconds;
            int h = (int)(elapsed / 3600);
            int m = (int)(elapsed % 3600 / 60);
            int s = (int)(elapsed % 60);
            CombatTimer = h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
            IsInCombat  = true;

            // BossHp 패킷이 없어도 세션 이름으로 보스명 표시
            if (!string.IsNullOrEmpty(session.BossName) && BossName != session.BossName)
                BossName = session.BossName;
        }
        else
        {
            IsInCombat = false;
        }

        // 전투 중 30초마다 Plaync API 조회 (serverId 확보된 플레이어 한정)
        if (_tracker.IsInCombat &&
            (DateTime.Now - _lastApiLookup).TotalSeconds >= 30)
        {
            _lastApiLookup = DateTime.Now;
            TriggerCombatPowerLookup();
        }
    }

    // ── 정리 ──────────────────────────────────────────────────

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}K"     : n.ToString();

    public void Cleanup()
    {
        _capture.OnCombatEvent   -= OnCombatEventReceived;
        _capture.OnEntityInfo    -= OnEntityInfoReceived;
        _capture.OnSpawn         -= OnSpawnReceived;
        _capture.OnBossHp        -= OnBossHpReceived;
        _capture.OnEntityRemoved -= OnEntityRemovedReceived;
        _capture.OnSummon        -= OnSummonReceived;
        _capture.OnCpName        -= OnCpNameReceived;
        _capture.OnError         -= OnCaptureError;
        _capture.OnStatus        -= OnCaptureStatus;
        _tracker.Players.CollectionChanged -= OnPlayersChanged;
        _tracker.OnCombatEnded -= OnCombatEndedForUpload;

        _timerRefresh?.Stop();
        _timerRefresh = null;

        _capture.Stop();
        _capture.Dispose();
        _tracker.Dispose();
        _settings.Save();
    }
}
