using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Aion2Meter.Converters;

/// <summary>
/// IsLocalPlayer bool → DPS 바 색상 (Color 타입 반환).
/// XAML에서 SolidColorBrush.Color에 바인딩할 때 사용.
/// Singleton Instance 패턴: x:Static으로 XAML에서 직접 참조 가능.
/// </summary>
[ValueConversion(typeof(bool), typeof(Color))]
public class LocalPlayerBarColorConverter : IValueConverter
{
    public static readonly LocalPlayerBarColorConverter Instance = new();

    private static readonly Color LocalColor = Color.FromRgb(0x4C, 0xAF, 0x50); // Green
    private static readonly Color OtherColor = Color.FromRgb(0x0F, 0x34, 0x60);  // Dark Blue

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? LocalColor : OtherColor;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
