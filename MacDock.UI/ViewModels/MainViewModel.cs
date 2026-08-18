using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Models;
using MacDock.Core.Services;

namespace MacDock.UI.ViewModels;

/// <summary>
/// Dock 主视图模型：管理 Dock 项目列表与启动/移除/新增操作。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly DockItemStore _store = new();
    private readonly IconService _iconService = IconService.Instance;

    /// <summary>Dock 项目列表。</summary>
    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    public MainViewModel()
    {
        Load();
    }

    /// <summary>从磁盘加载并构建视图模型。</summary>
    private void Load()
    {
        Items.Clear();
        foreach (var item in _store.Load())
            Items.Add(CreateViewModel(item));
    }

    /// <summary>通过拖入的路径新增项目（.lnk / .exe）。</summary>
    public void AddFromPath(string path)
    {
        var info = ShortcutResolver.Resolve(path);
        var item = new DockItem
        {
            Name = Path.GetFileNameWithoutExtension(info.TargetPath),
            Path = info.TargetPath,
            IconPath = info.IconPath,
            Arguments = info.Arguments,
            IsBuiltIn = false,
        };
        Items.Add(CreateViewModel(item));
        Persist();
    }

    private DockItemViewModel CreateViewModel(DockItem item)
    {
        var iconPath = string.IsNullOrWhiteSpace(item.IconPath) ? item.Path : item.IconPath;
        BitmapSource? icon = null;
        try
        {
            icon = _iconService.GetIcon(iconPath);
        }
        catch
        {
            // 图标加载失败时显示占位图标
        }

        return new DockItemViewModel(item, icon, Launch, Remove);
    }

    private void Launch(DockItemViewModel vm) => ProcessLauncher.Launch(vm.Model);

    private void Remove(DockItemViewModel vm)
    {
        Items.Remove(vm);
        Persist();
    }

    private void Persist() => _store.Save(Items.Select(vm => vm.Model).ToList());
}
