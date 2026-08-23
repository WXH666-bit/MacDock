using System.Windows.Media;

namespace MacDock.UI.Converters;

/// <summary>
/// 菜单栏矢量图标几何目录（喇叭四态 + 太阳）。返回 Geometry 供 Path.Data 直接绑定。
/// 纯矢量，任意 DPI 下都锐利；颜色由 Path.Fill 决定。
/// </summary>
public static class MenuBarIconCatalog
{
    private static readonly Dictionary<string, Geometry> GeometryCache = new()
    {
        // 喇叭本体（左喇叭箱 + 三角号角）
        ["speaker_body"] = GeometryParse("M3,9 L6,9 L11,4 L11,20 L6,15 L3,15 Z"),

        // 音量波（高）
        ["speaker_3_wave"] = GeometryParse(
            "M12,6.5 A8.5,8.5 0 0 1 12,17.5 L10.6,15.6 A6,6 0 0 0 10.6,8.4 Z"),

        // 音量波（中）
        ["speaker_2_wave"] = GeometryParse(
            "M12,7.2 A7,7 0 0 1 12,16.8 L10.8,15 A4.8,4.8 0 0 0 10.8,9 Z"),

        // 音量波（低）
        ["speaker_1_wave"] = GeometryParse(
            "M12,8.6 A4.8,4.8 0 0 1 12,15.4 L11,13.8 A3,3 0 0 0 11,10.2 Z"),

        // 静音斜线
        ["speaker_0_mute"] = GeometryParse(
            "M14,8 L20,16 L18.5,17 L12.5,9 Z"),

        // 太阳（实心圆 + 中央亮心）
        ["sun_core"] = GeometryParse("M12,5 A2,2 0 0 1 12,9 A2,2 0 0 1 12,5 Z"),
        ["sun_rays"] = GeometryParse(
            "M12,1.5 L12,4 M12,20 L12,22.5 M1.5,12 L4,12 M20,12 L22.5,12 "
            + "M4.5,4.5 L6.4,6.4 M17.6,17.6 L19.5,19.5 M19.5,4.5 L17.6,6.4 "
            + "M6.4,17.6 L4.5,19.5"),
    };

    /// <summary>每个状态由哪些几何组成（空格分隔的名）。</summary>
    private static readonly Dictionary<string, string> SpeakerStateParts = new(StringComparer.Ordinal)
    {
        ["speaker_3"] = "speaker_body speaker_3_wave",
        ["speaker_2"] = "speaker_body speaker_2_wave",
        ["speaker_1"] = "speaker_body speaker_1_wave",
        ["speaker_0"] = "speaker_body speaker_0_mute",
    };

    /// <summary>取喇叭四态几何（箱体 + 对应波形/静音线，合并为一个 GeometryGroup）。</summary>
    public static Geometry GetVolumeGeometry(string? state)
    {
        var parts = !string.IsNullOrWhiteSpace(state) && SpeakerStateParts.ContainsKey(state)
            ? SpeakerStateParts[state]
            : SpeakerStateParts["speaker_3"];

        var group = new GeometryGroup();
        foreach (var part in parts.Split(' '))
        {
            if (GeometryCache.TryGetValue(part, out var geometry))
                group.Children.Add(geometry.Clone());
        }

        group.Freeze();
        return group;
    }

    /// <summary>取太阳几何（实心核心 + 放射光线）。</summary>
    public static Geometry GetSunGeometry()
    {
        var group = new GeometryGroup();
        group.Children.Add(GeometryCache["sun_core"].Clone());
        group.Children.Add(GeometryCache["sun_rays"].Clone());
        group.Freeze();
        return group;
    }

    private static Geometry GeometryParse(string data) => Geometry.Parse(data);
}
