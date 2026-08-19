using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Models;
using MacDock.Core.Services;
using NLog;

namespace MacDock.UI.ViewModels;

/// <summary>
/// Dock 主视图模型：管理 Dock 项目列表与启动/移除/新增操作。
/// 图标在后台线程提取，先渲染占位图标，完成后回 UI 线程更新。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly DockItemStore _store = new();
    private readonly IconService _iconService = IconService.Instance;

    /// <summary>Dock 项目列表。</summary>
    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    /// <summary>启动失败时触发（参数为用户可读消息），由视图层显示托盘气泡。</summary>
    public event Action<string>? LaunchFailed;

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
            // .lnk 用快捷方式自身文件名（通常已是中文名）；.exe 用目标文件名
            Name = ShortcutResolver.IsShortcut(path)
                ? Path.GetFileNameWithoutExtension(path)
                : Path.GetFileNameWithoutExtension(info.TargetPath),
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
        var vm = new DockItemViewModel(item, IconService.GetPlaceholderIcon(), Launch, Remove);
        _ = LoadIconAsync(vm);
        return vm;
    }

    /// <summary>后台线程提取图标，完成后回 UI 线程更新（BitmapSource 均已冻结）。</summary>
    private async Task LoadIconAsync(DockItemViewModel vm)
    {
        var item = vm.Model;
        BitmapSource? icon;
        try
        {
            if (!string.IsNullOrWhiteSpace(item.IconOverride))
            {
                var packUri = item.IconOverride!;
                icon = await Task.Run(() => LoadResourceIcon(packUri));
            }
            else
            {
                var iconPath = string.IsNullOrWhiteSpace(item.IconPath) ? item.Path : item.IconPath!;
                icon = await Task.Run(() => _iconService.GetIcon(iconPath));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "图标加载失败：{0}", item.Name);
            return; // 保留占位图标
        }

        if (icon is null || !Items.Contains(vm))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        await dispatcher.InvokeAsync(() => vm.Icon = icon);
    }

    /// <summary>加载内置 pack URI 图标资源并冻结。</summary>
    private static BitmapSource LoadResourceIcon(string packUri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(packUri, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Launch(DockItemViewModel vm)
    {
        try
        {
            ProcessLauncher.Launch(vm.Model);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启动失败：{0} ({1})", vm.Name, vm.Path);
            LaunchFailed?.Invoke($"无法启动「{vm.Name}」");
        }
    }

    private void Remove(DockItemViewModel vm)
    {
        Items.Remove(vm);
        Persist();
    }

    private void Persist() => _store.Save(Items.Select(vm => vm.Model).ToList());
}
