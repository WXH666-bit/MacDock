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

    /// <summary>
    /// 共享的窗口监控实例：菜单栏复用同一份 WinEventHook，避免重复挂钩。
    /// 生命周期归 MainViewModel（Dispose 时统一注销）。
    /// </summary>
    public WindowMonitor WindowMonitor => _windowMonitor;

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
            Items.Add(CreateViewModel(item));

        // 加载完成后刷新全部运行状态（同一套匹配规则，UWP 项也能亮）
        _windowMonitor.Refresh();
        var running = _windowMonitor.RunningProcesses;
        foreach (var vm in Items)
        {
            vm.IsRunning = running.Any(exe => Matches(vm.Model, exe));
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
                if (!Matches(vm.Model, exeName))
                    continue;

                vm.IsRunning = isRunning;
                vm.Model.IsRunning = isRunning;
                return;
            }

            // Debug 级：任何进程开关窗口都会走到这里，Warn 会刷屏（v10 审查遗留项）
            Logger.Debug(
                "运行状态上报未匹配到 Dock 项：exeName={0}，当前项={1}",
                exeName,
                string.Join(", ", Items.Select(i => $"{i.Model.Name}[path={i.Model.Path};store={i.Model.StoreAppName}]")));
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 判断 WindowMonitor 上报的进程名是否属于该 Dock 项。
    /// 除路径 exe 名与 StoreAppName 全等外，还处理 PFN 形式的商店应用
    /// （Microsoft.WindowsCalculator_8wekyb3d8bbwe → Microsoft.WindowsCalculator → WindowsCalculator）。
    /// </summary>
    private static bool Matches(DockItem item, string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return false;

        var itemExe = Path.GetFileNameWithoutExtension(item.Path);
        if (!string.IsNullOrWhiteSpace(itemExe)
            && string.Equals(itemExe, exeName, StringComparison.OrdinalIgnoreCase))
            return true;

        var store = item.StoreAppName;
        if (string.IsNullOrWhiteSpace(store))
            return false;

        if (string.Equals(store, exeName, StringComparison.OrdinalIgnoreCase))
            return true;

        // PFN 格式：去掉末尾的 _发布者哈希 段
        var underscore = store.LastIndexOf('_');
        if (underscore <= 0)
            return false;

        var prefix = store[..underscore];
        if (string.Equals(prefix, exeName, StringComparison.OrdinalIgnoreCase))
            return true;

        // 再退一步：与包名最后一段比较（Microsoft.WindowsCalculator → WindowsCalculator）
        var lastSegment = prefix[(prefix.LastIndexOf('.') + 1)..];
        return lastSegment.Length > 0
            && string.Equals(lastSegment, exeName, StringComparison.OrdinalIgnoreCase);
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
                icon = await Task.Run(() => _iconService.GetIcon(iconPath));
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

            // 启动后补一次运行状态（新进程刚创建时窗口可能还没显示完）。
            // 只做点亮，不做熄灭：熄灭统一交给 WindowMonitor 的销毁事件，
            // 否则 UWP 项（Path 为空）会被这里误判成未运行。
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (vm.IsRunning)
                        return;

                    if (_windowMonitor.RunningProcesses.Any(exe => Matches(vm.Model, exe)))
                    {
                        vm.IsRunning = true;
                        vm.Model.IsRunning = true;
                    }
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
