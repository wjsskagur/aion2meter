using Aion2Meter.Services;
using Aion2Meter.ViewModels;
using System.Windows;
using System.Windows.Media;

namespace Aion2Meter.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Npcap 상태 표시
        bool installed = NpcapHelper.IsNpcapInstalled();
        NpcapStatus.Text = installed ? "✓ 설치됨" : "✗ 미설치";
        NpcapStatus.Foreground = installed
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xE9, 0x45, 0x60));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveSettingsCommand.Execute(null);
        (Owner as Views.MainWindow)?.ApplySettings();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
