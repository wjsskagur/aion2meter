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
    private System.Timers.Timer? _timerRefresh;

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
    public AppSettings Settings => _settings.Settings;

    // FontScale 기반 동적 폰트 크기
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
        _capture.OnCombatEvent += OnCombatEventReceived;
        _capture.OnEntityInfo  += OnEntityInfoReceived;
        _capture.OnBossHp      += OnBossHpReceived;
        _capture.OnError       += OnCaptureError;
        _capture.OnStatus      += OnCaptureStatus;
        _tracker.Players.CollectionChanged += OnPlayersChanged;

        ResetCommand         = new RelayCommand(OnReset);
        ToggleCaptureCommand = new RelayCommand(OnToggleCapture);
        SaveSettingsCommand  = new RelayCommand(OnSaveSettings);

        _timerRefresh = new System.Timers.Timer(1000);
        _timerRefresh.Elapsed += (_, _) => RefreshTimer();
        _timerRefresh.Start();
    }

    // ── 이벤트 핸들러 ─────────────────────────────────────────

    private void OnCombatEventReceived(object? s, CombatEvent e) =>
        _tracker.ProcessEvent(e);

    private void OnEntityInfoReceived(object? s, (uint entityId, string name) e) =>
        _tracker.UpdateEntityName(e.entityId, e.name);

    private void OnBossHpReceived(object? s, (uint bossId, string bossName, long currentHp, long maxHp) e)
    {
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            BossName      = e.bossName;
            BossHpPercent = e.maxHp > 0 ? (double)e.currentHp / e.maxHp : 0;
            BossHpText    = $"{FormatNumber(e.currentHp)} / {FormatNumber(e.maxHp)}";
            IsInCombat    = e.currentHp > 0;

            if (e.currentHp <= 0)
                _tracker.EndCombat();
        });
    }

    private void OnCaptureError(object? s, string msg) =>
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusMessage = msg.Split('\n')[0]; // 첫 줄만 상태바에
            IsCapturing = false;
            // 상세 오류는 MessageBox로 표시
            System.Windows.MessageBox.Show(msg, "캡처 오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
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
        App.Current?.Dispatcher.BeginInvoke(() => StatusMessage = "캡처 초기화 중...");
        _capture.Start(_settings.Settings.AionPort, _settings.Settings.ServerIp);
        App.Current?.Dispatcher.BeginInvoke(() => IsCapturing = true);
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

    private void OnReset() => _tracker.Reset();

    private void OnSaveSettings()
    {
        _settings.Save();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(NameFontSize));
        OnPropertyChanged(nameof(DamageFontSize));
        OnPropertyChanged(nameof(DpsFontSize));
    }

    // ── 타이머 ────────────────────────────────────────────────

    private void RefreshTimer()
    {
        var session = _tracker.CurrentSession;
        if (session?.IsActive == true)
        {
            var elapsed = session.ElapsedSeconds;
            int min = (int)(elapsed / 60);
            int sec = (int)(elapsed % 60);
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                CombatTimer = $"{min:D2}:{sec:D2}";
                IsInCombat = true;
            });
        }
        else
        {
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsInCombat = false;
            });
        }
    }

    // ── 정리 ──────────────────────────────────────────────────

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}K"     : n.ToString();

    public void Cleanup()
    {
        _capture.OnCombatEvent -= OnCombatEventReceived;
        _capture.OnEntityInfo  -= OnEntityInfoReceived;
        _capture.OnBossHp      -= OnBossHpReceived;
        _capture.OnError       -= OnCaptureError;
        _capture.OnStatus      -= OnCaptureStatus;
        _tracker.Players.CollectionChanged -= OnPlayersChanged;

        _timerRefresh?.Stop();
        _timerRefresh?.Dispose();
        _timerRefresh = null;

        _capture.Stop();
        _capture.Dispose();
        _tracker.Dispose();
        _settings.Save();
    }
}
