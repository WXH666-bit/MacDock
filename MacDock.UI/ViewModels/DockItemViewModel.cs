using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using MacDock.Core.Models;

namespace MacDock.UI.ViewModels;

/// <summary>
/// Dock 单个项目的视图模型。
/// </summary>
public sealed class DockItemViewModel
{
    /// <summary>底层数据模型。</summary>
    public DockItem Model { get; }

    /// <summary>显示名称。</summary>
    public string Name => Model.Name;

    /// <summary>启动路径。</summary>
    public string Path => Model.Path;

    /// <summary>是否为内置默认项。</summary>
    public bool IsBuiltIn => Model.IsBuiltIn;

    /// <summary>图标（已冻结，线程安全）。</summary>
    public BitmapSource? Icon { get; }

    /// <summary>启动命令。</summary>
    public ICommand LaunchCommand { get; }

    /// <summary>移除命令。</summary>
    public ICommand RemoveCommand { get; }

    public DockItemViewModel(DockItem model, BitmapSource? icon,
        Action<DockItemViewModel> onLaunch, Action<DockItemViewModel> onRemove)
    {
        Model = model;
        Icon = icon;
        LaunchCommand = new RelayCommand(() => onLaunch(this));
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}
