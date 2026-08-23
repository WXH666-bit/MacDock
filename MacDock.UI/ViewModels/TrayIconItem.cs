using System.Windows.Media;
using MacDock.Core.Models;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 菜单栏托盘区单个图标的展示模型：携带已转换（冻结）的位图与点击转发的原始信息。
/// </summary>
public sealed class TrayIconItem
{
    /// <summary>已冻结的图标位图（线程安全）。</summary>
    public ImageSource Icon { get; }

    /// <summary>原始托盘信息（含点击转发所需的 hWnd / 回调消息 / 工具提示）。</summary>
    public TrayIconInfo Info { get; }

    /// <summary>悬停提示文本。</summary>
    public string? Tooltip => Info.Tooltip;

    public TrayIconItem(ImageSource icon, TrayIconInfo info)
    {
        Icon = icon;
        Info = info;
    }
}
