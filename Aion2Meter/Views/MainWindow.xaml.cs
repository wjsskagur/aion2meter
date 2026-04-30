using Aion2Meter.ViewModels;
using System.Windows;

namespace Aion2Meter.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        _vm = new MainViewModel();
        DataContext = _vm;

        // 설정값 코드로 적용 (바인딩 대신)
        // WindowStyle=None + AllowsTransparency=True 에서
        // Left/Top/Width/Opacity 바인딩은 WPF 레이아웃 무한루프 유발
        var s = _vm.Settings;
        Left    = s.WindowLeft;
        Top     = s.WindowTop;
        Width   = s.WindowWidth;
        Opacity = s.Opacity;
        Topmost = s.AlwaysOnTop;

        _vm.StartCapture();
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_vm != null) _vm.Settings.CompactMode = !_vm.Settings.CompactMode;
            return;
        }
        DragMove();
        if (_vm != null)
        {
            _vm.Settings.WindowLeft = Left;
            _vm.Settings.WindowTop  = Top;
        }
    }

    public void RetryCapture()
    {
        _vm?.StartCapture();
    }

    private void PlayerRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Models.PlayerStats player && _vm != null)
        {
            var win = new PlayerDetailWindow(player, _vm.ElapsedSeconds);
            win.Owner = this;
            win.Show();
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _vm?.CurrentSession;
        if (session == null || session.Players.Count == 0)
        {
            MessageBox.Show("내보낼 전투 데이터가 없습니다.", "알림",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExportSession(session);
    }

    public static void ExportSession(Models.CombatSession session)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"보스: {session.BossName}");
        sb.AppendLine($"시작: {session.StartTime:yyyy-MM-dd HH:mm:ss}");
        double elapsed = session.ElapsedSeconds;
        int m = (int)(elapsed / 60), s = (int)(elapsed % 60);
        sb.AppendLine($"시간: {m:D2}:{s:D2}");
        sb.AppendLine($"총 피해량: {session.TotalPartyDamage:N0}");
        sb.AppendLine();
        sb.AppendLine("플레이어,피해량,DPS,기여%,최대단타,직접피해,DoT피해,타격수,치명타율");

        foreach (var p in session.Players.Values.OrderByDescending(x => x.TotalDamage))
        {
            double dps = elapsed > 0 ? p.TotalDamage / elapsed : 0;
            double pct = session.TotalPartyDamage > 0
                ? (double)p.TotalDamage / session.TotalPartyDamage * 100 : 0;
            sb.AppendLine($"{p.Name},{p.TotalDamage},{dps:F0},{pct:F1}%," +
                          $"{p.MaxHit},{p.DirectDamage},{p.DotDamage},{p.HitCount},{p.CritRate:P1}");
        }

        sb.AppendLine();
        sb.AppendLine("=== 스킬 상세 ===");
        foreach (var p in session.Players.Values.OrderByDescending(x => x.TotalDamage))
        {
            sb.AppendLine($"\n[{p.Name}]");
            sb.AppendLine("스킬,DoT,피해량,횟수,최대,평균,치명타율");
            foreach (var sk in p.Skills.Values.OrderByDescending(x => x.TotalDamage))
                sb.AppendLine($"{sk.SkillName},{(sk.IsDot ? "Y" : "")},{sk.TotalDamage}," +
                              $"{sk.HitCount},{sk.MaxHit},{sk.AverageDamage:F0},{sk.CritRate:P1}");
        }

        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"Aion2Meter_{session.BossName}_{session.StartTime:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
            MessageBox.Show($"저장됨:\n{path}", "내보내기 완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"저장 실패: {ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var win = new HistoryWindow(_vm.History) { Owner = this };
        win.Show();
    }

    private void BossArea_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 더블클릭: 보스 이름 클립보드 복사
            if (_vm?.CurrentSession != null)
            {
                var session = _vm.CurrentSession;
                var elapsed = session.ElapsedSeconds;
                string text = $"[{session.BossName}] {(int)(elapsed/60):D2}:{(int)(elapsed%60):D2}\n";
                foreach (var p in session.Players.Values.OrderByDescending(x => x.TotalDamage))
                {
                    double dps = elapsed > 0 ? p.TotalDamage / elapsed : 0;
                    double pct = session.TotalPartyDamage > 0
                        ? (double)p.TotalDamage / session.TotalPartyDamage * 100 : 0;
                    text += $"{p.Name}: {dps:N0} DPS ({pct:F1}%)\n";
                }
                Clipboard.SetText(text);
                MessageBox.Show("클립보드에 복사됨", "복사 완료",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        if (_vm?.CurrentSession == null) return;
        var detailWin = new BossDetailWindow(_vm.CurrentSession) { Owner = this };
        detailWin.Show();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RightEdge_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var edge = (FrameworkElement)sender;
        edge.CaptureMouse();
        var startX = e.GetPosition(this).X;
        var startW = Width;

        void onMove(object s2, System.Windows.Input.MouseEventArgs e2)
        {
            var dx = e2.GetPosition(this).X - startX;
            Width = Math.Max(240, startW + dx);
        }
        void onUp(object s2, System.Windows.Input.MouseButtonEventArgs e2)
        {
            edge.ReleaseMouseCapture();
            edge.MouseMove -= onMove;
            edge.MouseLeftButtonUp -= onUp;
            if (_vm != null) _vm.Settings.WindowWidth = Width;
        }
        edge.MouseMove += onMove;
        edge.MouseLeftButtonUp += onUp;
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 하단 그립도 동일하게 처리
        RightEdge_MouseLeftButtonDown(sender, e);
    }

    public void ApplySettings()
    {
        if (_vm == null) return;
        var s = _vm.Settings;
        Width   = s.WindowWidth;
        Opacity = s.Opacity;
        Topmost = s.AlwaysOnTop;
        _vm.NotifyChanged(nameof(MainViewModel.RowHeight));
        _vm.NotifyChanged(nameof(MainViewModel.NameFontSize));
        _vm.NotifyChanged(nameof(MainViewModel.DamageFontSize));
        _vm.NotifyChanged(nameof(MainViewModel.DpsFontSize));
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var settingsWindow = new SettingsWindow(_vm) { Owner = this };
        settingsWindow.ShowDialog();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm != null)
            _vm.Settings.WindowWidth = Width;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm != null)
        {
            _vm.Settings.WindowLeft  = Left;
            _vm.Settings.WindowTop   = Top;
            _vm.Settings.WindowWidth = Width;
        }
        _vm?.Cleanup();
    }
}
