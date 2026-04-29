using Aion2Meter.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Aion2Meter.Views;

public partial class HistoryWindow : Window
{
    private readonly IEnumerable<CombatSession> _history;

    public HistoryWindow(IEnumerable<CombatSession> history)
    {
        InitializeComponent();
        _history = history;
        SessionList.ItemsSource = history;
        if (SessionList.Items.Count > 0)
            SessionList.SelectedIndex = 0;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not CombatSession session) return;

        var elapsed = session.ElapsedSeconds;
        TotalDmgTxt.Text      = FormatNumber(session.TotalPartyDamage);
        ElapsedTxtDetail.Text = elapsed >= 3600
            ? $"{(int)(elapsed/3600)}:{(int)(elapsed%3600/60):D2}:{(int)(elapsed%60):D2}"
            : $"{(int)(elapsed/60):D2}:{(int)(elapsed%60):D2}";
        PlayerCountTxt.Text   = session.Players.Count.ToString();

        // 원본 PlayerStats 직접 수정하지 않도록 익명 타입으로 변환
        var players = session.Players.Values
            .Select(p => new
            {
                p.Name,
                p.TotalDamage,
                p.MaxHit,
                p.HitCount,
                Dps           = elapsed > 0 ? p.TotalDamage / elapsed : 0,
                DamagePercent = session.TotalPartyDamage > 0
                    ? (double)p.TotalDamage / session.TotalPartyDamage : 0
            })
            .OrderByDescending(p => p.TotalDamage)
            .ToList();

        PlayerList.ItemsSource = players;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not CombatSession session)
        {
            MessageBox.Show("세션을 선택해주세요.", "알림",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MainWindow.ExportSession(session);
    }

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}K" : n.ToString();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
