using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Aion2Meter.Converters;

/// <summary>
/// double(0.0~1.0)을 픽셀 너비로 변환.
/// DPS 바 너비 = 전체 너비 * DamagePercent
/// ConverterParameter로 최대 너비(컨트롤 ActualWidth)를 전달.
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = (double)value;
        double maxWidth = parameter is string s && double.TryParse(s, out var w) ? w : 300;
        return Math.Max(2, percent * maxWidth); // 최소 2px (0%라도 보이게)
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool → Visibility 변환.
/// IsLocalPlayer=true → 초록색 강조를 위한 별도 처리에도 사용.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool bVal && bVal;
        // parameter="Inverse"면 반전
        if (parameter is string s && s == "Inverse") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 플레이어가 로컬 플레이어인지 여부에 따라 이름 색상 변경.
/// IsLocalPlayer=true → 초록(#4CAF50), false → 흰색
/// </summary>
[ValueConversion(typeof(bool), typeof(Brush))]
public class LocalPlayerColorConverter : IValueConverter
{
    private static readonly SolidColorBrush LocalColor =
        new(Color.FromRgb(0x4C, 0xAF, 0x50)); // Material Green

    private static readonly SolidColorBrush OtherColor =
        new(Colors.White);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? LocalColor : OtherColor;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// double DPS 값을 "12,345" 또는 "1.2M" 포맷으로 변환
/// </summary>
[ValueConversion(typeof(double), typeof(string))]
public class DpsFormatter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double d) return "0";
        return d >= 1_000_000 ? $"{d / 1_000_000.0:F2}M" :
               d >= 1_000 ? $"{d / 1_000.0:F1}K" :
               $"{d:F0}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// long 피해량을 단축 포맷으로 변환 ("1,234,567" → "1.23M")
/// </summary>
[ValueConversion(typeof(long), typeof(string))]
public class DamageFormatter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long n = value is long l ? l : 0;
        return n >= 1_000_000 ? $"{n / 1_000_000.0:F2}M" :
               n >= 1_000 ? $"{n / 1_000.0:F1}K" :
               n.ToString("N0");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// double(0.0~1.0) → 퍼센트 문자열 ("87.3%")
/// </summary>
[ValueConversion(typeof(double), typeof(string))]
public class PercentFormatter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:F1}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
