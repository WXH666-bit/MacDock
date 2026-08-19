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

    /// <summary>启动命令。</summary>
    public ICommand LaunchCommand { get; }

    /// <summary>移除命令。</summary>
    public ICommand RemoveCommand { get; }

    public DockItemViewModel(DockItem model, BitmapSource? icon,
        Action<DockItemViewModel> onLaunch, Action<DockItemViewModel> onRemove)
    {
        Model = model;
        _icon = icon;
        LaunchCommand = new RelayCommand(() => onLaunch(this));
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}
