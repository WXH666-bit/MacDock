using System.Collections.ObjectModel;
using System.Threading.Channels;
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
    private readonly RunningDockItemResolver _runningItemResolver = new();
    private readonly CancellationTokenSource _runningItemCancellation = new();
    private readonly Channel<string> _runningItemQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly object _pendingRunningItemsSync = new();
    private readonly HashSet<string> _pendingRunningItems = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Task _runningItemWorker;

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
        _runningItemWorker = Task.Run(ProcessRunningItemQueueAsync);

        // 监听窗口运行状态变化，更新对应 DockItem 的 IsRunning
        _windowMonitor.RunningStateChanged += OnRunningStateChanged;

        Load();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _windowMonitor.RunningStateChanged -= OnRunningStateChanged;
        _runningItemQueue.Writer.TryComplete();
        _runningItemCancellation.Cancel();
        _ = ObserveRunningItemWorkerShutdownAsync();
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
            Items.Add(CreateViewModel(item, isPinned: true));

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
        // 在主线程更新集合与绑定属性（WindowMonitor 回调在专用原生消息线程）。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;

            var item = FindItemForProcess(exeName);
            if (item is not null)
            {
                item.IsRunning = isRunning;
                item.Model.IsRunning = isRunning;
                if (!isRunning && !item.IsPinned)
                    Items.Remove(item);
                return;
            }

            if (isRunning)
                QueueRunningItemResolution(exeName);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 有界、去重地排队解析未固定运行应用。进程模块与 AppX 包访问都由单消费者
    /// 后台 worker 执行，避免启动时并发枚举和 UI 卡顿。
    /// </summary>
    private void QueueRunningItemResolution(string exeName)
    {
        lock (_pendingRunningItemsSync)
        {
            if (_disposed || !_pendingRunningItems.Add(exeName))
                return;

            if (_runningItemQueue.Writer.TryWrite(exeName))
                return;

            _pendingRunningItems.Remove(exeName);
            Logger.Debug("运行应用解析队列已满，跳过：{0}", exeName);
        }
    }

    private async Task ProcessRunningItemQueueAsync()
    {
        var cancellationToken = _runningItemCancellation.Token;
        try
        {
            await foreach (var exeName in _runningItemQueue.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                try
                {
                    if (!_windowMonitor.IsProcessRunning(exeName))
                        continue;

                    var model = _runningItemResolver.Resolve(exeName);
                    if (model is null || cancellationToken.IsCancellationRequested)
                        continue;

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is null || dispatcher.HasShutdownStarted)
                        continue;

                    await dispatcher.InvokeAsync(
                        () => AddResolvedRunningItem(exeName, model),
                        DispatcherPriority.Background,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Logger.Debug(exception, "解析临时运行应用失败：{0}", exeName);
                }
                finally
                {
                    lock (_pendingRunningItemsSync)
                        _pendingRunningItems.Remove(exeName);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常退出路径。
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "运行应用解析 worker 异常退出");
        }
    }

    private void AddResolvedRunningItem(string exeName, DockItem model)
    {
        if (_disposed
            || !_windowMonitor.IsProcessRunning(exeName)
            || FindItemForProcess(exeName) is not null)
        {
            return;
        }

        var viewModel = CreateViewModel(model, isPinned: false);
        viewModel.IsRunning = true;
        viewModel.Model.IsRunning = true;
        Items.Add(viewModel);
    }

    private async Task ObserveRunningItemWorkerShutdownAsync()
    {
        try
        {
            await _runningItemWorker.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "等待运行应用解析 worker 退出失败");
        }
        finally
        {
            _runningItemCancellation.Dispose();
        }
    }

    /// <summary>
    /// 按运行状态的同一套规则查找 Dock 项。调用方应在 UI 线程使用，
    /// 供 M4 把最小化窗口映射到目标图标。
    /// </summary>
    public DockItemViewModel? FindItemForProcess(string exeName)
        => string.IsNullOrWhiteSpace(exeName)
            ? null
            : Items.FirstOrDefault(item => Matches(item.Model, exeName));

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

        var processName = Path.GetFileNameWithoutExtension(item.Path);
        var existing = Items.FirstOrDefault(candidate =>
            SameLaunchTarget(candidate.Model, item)
            || (!candidate.IsPinned && Matches(candidate.Model, processName)));
        if (existing is not null)
        {
            if (existing.IsPinned)
                return;

            var index = Items.IndexOf(existing);
            var replacement = CreateViewModel(item, isPinned: true);
            replacement.IsRunning = existing.IsRunning;
            replacement.Model.IsRunning = existing.IsRunning;
            Items[index] = replacement;
            Persist();
            return;
        }

        Items.Add(CreateViewModel(item, isPinned: true));
        Persist();
    }

    private DockItemViewModel CreateViewModel(DockItem item, bool isPinned)
    {
        var vm = new DockItemViewModel(
            item,
            IconService.GetPlaceholderIcon(),
            isPinned,
            Launch,
            Remove,
            Pin);
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
        if (!vm.IsPinned)
            return;

        if (vm.IsRunning)
        {
            // 与 macOS 一致：取消固定不结束应用；图标保留到最后一个可见窗口关闭。
            vm.IsPinned = false;
            vm.Model.IsBuiltIn = false;
        }
        else
        {
            Items.Remove(vm);
        }

        Persist();
    }

    private void Pin(DockItemViewModel vm)
    {
        if (vm.IsPinned || !vm.IsRunning)
            return;

        vm.IsPinned = true;
        vm.Model.IsBuiltIn = false;
        Persist();
    }

    private static bool SameLaunchTarget(DockItem left, DockItem right)
    {
        if (!string.IsNullOrWhiteSpace(left.StoreAppName)
            || !string.IsNullOrWhiteSpace(right.StoreAppName))
        {
            return string.Equals(
                left.StoreAppName,
                right.StoreAppName,
                StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(left.Path) || string.IsNullOrWhiteSpace(right.Path))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left.Path),
                Path.GetFullPath(right.Path),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Persist()
        => _store.Save(SelectPersistentItems(Items));

    /// <summary>只选择固定项写入用户配置；临时运行项永远不进入持久化文件。</summary>
    internal static List<DockItem> SelectPersistentItems(
        IEnumerable<DockItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(static viewModel => viewModel.IsPinned)
            .Select(static viewModel => viewModel.Model)
            .ToList();
    }
}
