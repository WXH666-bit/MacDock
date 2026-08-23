using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MacDock.UI.Converters;

/// <summary>
/// 将图标状态键（speaker_0/1/2/3 或 "sun"）映射为 Geometry，供 Path.Data 绑定。
/// 作为 <see cref="IValueConverter"/> 使用，直接绑定 <see cref="MenuBarIconCatalog"/>。
/// </summary>
public sealed class IconStateToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value as string;
        if (string.Equals(state, "sun", StringComparison.Ordinal))
            return MenuBarIconCatalog.GetSunGeometry();

        return MenuBarIconCatalog.GetVolumeGeometry(state);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
