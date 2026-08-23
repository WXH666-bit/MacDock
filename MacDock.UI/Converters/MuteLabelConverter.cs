using System.Globalization;
using System.Windows.Data;

namespace MacDock.UI.Converters;

/// <summary>
/// 将静音 bool 映射为按钮文案：静音或有声。
/// 命中可空 bool 的 null 视为未静音。
/// </summary>
public sealed class MuteLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "已静音" : "静音";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
