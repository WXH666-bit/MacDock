using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacDock.Core.Models;

namespace MacDock.UI.ViewModels;

/// <summary>
/// Dock 单个项目的视图模型。图标异步加载，先占位后更新。
/// </summary>
public partial class DockItemViewModel : ObservableObject
{
    /// <summary>底层数据模型。</summary>
    public DockItem Model { get; }

    /// <summary>显示名称。</summary>
    public string Name => Model.Name;

    /// <summary>启动路径。</summary>
    public string Path => Model.Path;

    /// <summary>是否为内置默认项。</summary>
    public bool IsBuiltIn => Model.IsBuiltIn;

    /// <summary>内置图标资源（pack URI）；非空时优先于系统图标。</summary>
    public string? IconOverride => Model.IconOverride;

    /// <summary>图标（已冻结，线程安全；异步加载完成后更新）。</summary>
    [ObservableProperty]
    private BitmapSource? _icon;

    /// <summary>是否有可见顶层窗口在运行（运行指示小圆点）。</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>是否为用户固定项；false 表示只在应用运行期间显示的临时项。</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>启动命令。</summary>
    public ICommand LaunchCommand { get; }

    /// <summary>移除命令。</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>把临时运行项保留在 Dock 的命令。</summary>
    public ICommand PinCommand { get; }

    /// <summary>创建一个固定或临时的 Dock 项视图模型。</summary>
    public DockItemViewModel(
        DockItem model,
        BitmapSource? icon,
        bool isPinned,
        Action<DockItemViewModel> onLaunch,
        Action<DockItemViewModel> onRemove,
        Action<DockItemViewModel> onPin)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _icon = icon;
        _isPinned = isPinned;
        LaunchCommand = new RelayCommand(() => onLaunch(this));
        RemoveCommand = new RelayCommand(() => onRemove(this));
        PinCommand = new RelayCommand(() => onPin(this));
    }
}
