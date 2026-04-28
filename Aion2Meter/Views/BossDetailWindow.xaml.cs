using Aion2Meter.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Aion2Meter.Views;

public class SkillRow
{
    public string SkillName { get; set; } = "";
    public long TotalDamage { get; set; }
    public int HitCount { get; set; }
    public double AverageDamage => HitCount > 0 ? (double)TotalDamage / HitCount : 0;
    public double DamageShare { get; set; }
}

public partial class BossDetailWindow : Window
{
    private readonly List<PlayerStats> _players;
    private readonly long _totalDamage;
    private PlayerStats? _selectedPlayer;

    public BossDetailWindow(CombatSession session)
    {
        InitializeComponent();

        _players = session.Players.Values.OrderByDescending(p => p.TotalDamage).ToList();
        _totalDamage = _players.Sum(p => p.TotalDamage);

        BossTitleText.Text = $"⚔ {session.BossName}";
        ElapsedText.Text = $"  {session.ElapsedSeconds:F0}초  |  {FormatNumber(_totalDamage)} 총 피해량";

        // 플레이어 탭 버튼 생성
        AddTabButton("전체", null);
        foreach (var p in _players)
            AddTabButton(p.Name, p);

        ShowSkills(null); // 전체 탭 기본
    }

    private void AddTabButton(string label, PlayerStats? player)
    {
        var btn = new Button
        {
            Content = label,
            Style = (Style)FindResource("IconButton"),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 4, 0),
            Tag = player
        };
        btn.Click += (_, _) =>
        {
            _selectedPlayer = player;
            ShowSkills(player);
            // 선택된 탭 하이라이트
            foreach (Button b in PlayerTabPanel.Children)
                b.Foreground = Brushes.White;
            btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E94560")!);
        };
        if (player == null) // 전체 탭 기본 선택
            btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E94560")!);
        PlayerTabPanel.Children.Add(btn);
    }

    private void ShowSkills(PlayerStats? player)
    {
        List<SkillRow> rows;

        if (player == null)
        {
            // 전체: 모든 플레이어의 스킬 합산
            var merged = new Dictionary<uint, SkillRow>();
            foreach (var p in _players)
            {
                foreach (var s in p.Skills.Values)
                {
                    if (!merged.TryGetValue(s.SkillId, out var row))
                        merged[s.SkillId] = row = new SkillRow { SkillName = s.SkillName };
                    row.TotalDamage += s.TotalDamage;
                    row.HitCount    += s.HitCount;
                }
            }
            rows = merged.Values.OrderByDescending(r => r.TotalDamage).ToList();
        }
        else
        {
            rows = player.Skills.Values
                .OrderByDescending(s => s.TotalDamage)
                .Select(s => new SkillRow
                {
                    SkillName   = s.SkillName,
                    TotalDamage = s.TotalDamage,
                    HitCount    = s.HitCount
                }).ToList();
        }

        long total = rows.Sum(r => r.TotalDamage);
        foreach (var r in rows)
            r.DamageShare = total > 0 ? (double)r.TotalDamage / total : 0;

        SkillListView.ItemsSource = rows;
    }

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}K" : n.ToString();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
