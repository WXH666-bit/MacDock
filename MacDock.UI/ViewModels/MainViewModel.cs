using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Models;
using MacDock.Core.Services;
using NLog;

namespace MacDock.UI.ViewModels;

/// <summary>
/// Dock 主视图模型：管理 Dock 项目列表与启动/移除/新增操作，跟踪运行状态。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly DockItemStore _store = new();
    private readonly IconService _iconService = IconService.Instance;
    private readonly WindowMonitor _windowMonitor = new();

    /// <summary>Dock 项目列表。</summary>
    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    /// <summary>启动失败时触发（参数为用户可读消息），由视图层显示托盘气泡。</summary>
    public event Action<string>? LaunchFailed;

    /// <summary>Dock 实例是否已被销毁。</summary>
    private bool _disposed;

    public MainViewModel()
    {
        // 监听窗口运行状态变化，更新对应 DockItem 的 IsRunning
        _windowMonitor.RunningStateChanged += OnRunningStateChanged;

        Load();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _windowMonitor.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>从磁盘加载并构建视图模型。</summary>
    private void Load()
    {
        Items.Clear();
        var items = _store.Load();
        Logger.Info("从存储加载了 {0} 个项目", items.Count);
        foreach (var item in items)
        {
            Logger.Info("  项目: {0} path={1} iconOverride={2}", item.Name, item.Path, item.IconOverride ?? "(null)");
            Items.Add(CreateViewModel(item));
        }

        // 加载完成后刷新全部运行状态
        _windowMonitor.Refresh();
        foreach (var vm in Items)
        {
            vm.IsRunning = _windowMonitor.IsProcessRunning(vm.Model.Path);
            vm.Model.IsRunning = vm.IsRunning;
        }
    }

    private void OnRunningStateChanged(string exeName, bool isRunning)
    {
        // 在主线程更新（WindowMonitor 回调在原生线程）
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(() =>
        {
            foreach (var vm in Items)
            {
                var itemExe = Path.GetFileNameWithoutExtension(vm.Model.Path);
                if (string.Equals(itemExe, exeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(vm.Model.StoreAppName, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    vm.IsRunning = isRunning;
                    vm.Model.IsRunning = isRunning;
                    break;
                }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>通过拖入的路径新增项目（.lnk / .exe）。</summary>
    public void AddFromPath(string path)
    {
        var info = ShortcutResolver.Resolve(path);
        var item = new DockItem
        {
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
        Logger.Info("创建 ViewModel: {0}", item.Name);
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
                icon = await Task.Run(() => LoadResourceIcon(item.IconOverride!));
            }
            else
            {
                var iconPath = string.IsNullOrWhiteSpace(item.IconPath) ? item.Path : item.IconPath!;
                Logger.Info("  提取图标: {0} from {1}", item.Name, iconPath);
                icon = await Task.Run(() => _iconService.GetIcon(iconPath));
                Logger.Info("  图标结果: {0} -> {1}", item.Name, icon is null ? "NULL" : $"ok ({icon.Width}x{icon.Height})");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "图标加载失败：{0}", item.Name);
            return;
        }

        if (icon is null || _disposed || !Items.Contains(vm))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        await dispatcher.InvokeAsync(() => vm.Icon = icon);
        Logger.Info("  图标已设置到 UI: {0}", item.Name);
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

            // 启动后刷新运行状态（新进程可能刚创建，窗口尚未显示）
            // 短暂延迟等窗口出现
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    vm.IsRunning = _windowMonitor.IsProcessRunning(vm.Model.Path);
                    vm.Model.IsRunning = vm.IsRunning;
                });
            });
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
