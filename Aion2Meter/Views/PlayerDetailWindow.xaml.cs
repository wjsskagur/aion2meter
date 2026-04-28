using Aion2Meter.Models;
using System.Windows;
using System.Windows.Input;

namespace Aion2Meter.Views;

public partial class PlayerDetailWindow : Window
{
    public PlayerDetailWindow(PlayerStats player, double elapsedSeconds)
    {
        InitializeComponent();

        PlayerNameText.Text = player.Name;
        PlayerDpsText.Text  = $"  {player.Dps:N0} DPS  |  {player.DamagePercent:P1}";
        TotalDamageText.Text = FormatNumber(player.TotalDamage);
        HitCountText.Text    = player.HitCount.ToString("N0");
        CritRateText.Text    = $"{player.CritRate:P1}";

        var skills = player.Skills.Values
            .OrderByDescending(s => s.TotalDamage)
            .ToList();
        SkillListView.ItemsSource = skills;
    }

    private static string FormatNumber(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}K" : n.ToString();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
