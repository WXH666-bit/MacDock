using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacDock.Core.Models;
using MacDock.Core.Services;
using NLog;

namespace MacDock.UI.ViewModels;

/// <summary>启动台中单个可启动应用。</summary>
public sealed class LaunchpadAppViewModel : ObservableObject
{
    private BitmapSource _icon = IconService.GetPlaceholderIcon();

    public LaunchpadAppViewModel(InstalledApp model, Action<InstalledApp> launch)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(launch);
        LaunchCommand = new RelayCommand(() => launch(Model));
        Initial = StringInfo.GetNextTextElement(Model.Name);
    }

    public InstalledApp Model { get; }

    public string Name => Model.Name;

    public string Initial { get; }

    public BitmapSource Icon
    {
        get => _icon;
        internal set => SetProperty(ref _icon, value);
    }

    public IRelayCommand LaunchCommand { get; }
}

/// <summary>启动台：异步加载应用目录、模糊搜索、分页和按需图标解码。</summary>
public sealed class LaunchpadViewModel : ObservableObject, IDisposable
{
    internal const int PageSize = 28;

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly InstalledAppCatalog _catalog;
    private readonly IconService _iconService;
    private readonly Action<InstalledApp> _launcher;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _iconCancellation;
    private IReadOnlyList<InstalledApp> _allApps = [];
    private IReadOnlyList<InstalledApp> _filteredApps = [];
    private bool _hasLoaded;
    private bool _disposed;
    private bool _isLoading;
    private string _searchText = string.Empty;
    private int _currentPage;
    private string? _error;

    public LaunchpadViewModel(
        InstalledAppCatalog? catalog = null,
        IconService? iconService = null,
        Action<InstalledApp>? launcher = null)
    {
        _catalog = catalog ?? new InstalledAppCatalog(
            errorSink: exception => Logger.Debug(exception, "跳过不可读取的启动台项目"));
        _iconService = iconService ?? IconService.Instance;
        _launcher = launcher ?? ProcessLauncher.Launch;

        PreviousPageCommand = new RelayCommand(PreviousPage, () => CanGoPrevious);
        NextPageCommand = new RelayCommand(NextPage, () => CanGoNext);
    }

    public ObservableCollection<LaunchpadAppViewModel> VisibleApps { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
                return;

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasApps));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;

            CurrentPage = 0;
            ApplyFilterAndPage();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            var maximum = Math.Max(0, PageCount - 1);
            var clamped = Math.Clamp(value, 0, maximum);
            if (!SetProperty(ref _currentPage, clamped))
                return;

            NotifyPagingState();
        }
    }

    public int PageCount => _filteredApps.Count == 0
        ? 0
        : (int)Math.Ceiling(_filteredApps.Count / (double)PageSize);

    public string PageText => PageCount == 0
        ? "0 / 0"
        : $"{CurrentPage + 1} / {PageCount}";

    public bool CanGoPrevious => CurrentPage > 0;

    public bool CanGoNext => CurrentPage + 1 < PageCount;

    public bool IsEmpty => !IsLoading && VisibleApps.Count == 0;

    public bool HasApps => !IsLoading && VisibleApps.Count > 0;

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    /// <summary>应用成功交给系统启动后触发，窗口据此收起。</summary>
    public event Action? AppLaunched;

    public async Task LoadAsync()
    {
        if (_hasLoaded || _disposed)
            return;

        _hasLoaded = true;
        IsLoading = true;
        Error = null;
        try
        {
            _allApps = await _catalog.LoadAsync(_lifetimeCancellation.Token);
            ApplyFilterAndPage();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 窗口关闭时的正常路径。
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "加载启动台应用目录失败");
            Error = "应用列表加载失败，可关闭启动台后重试。";
            _allApps = [];
            ApplyFilterAndPage();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasApps));
        }
    }

    private void ApplyFilterAndPage()
    {
        if (_disposed)
            return;

        _filteredApps = LaunchpadSearch.Filter(_allApps, SearchText);
        if (CurrentPage >= PageCount)
            CurrentPage = Math.Max(0, PageCount - 1);

        VisibleApps.Clear();
        foreach (var app in _filteredApps
                     .Skip(CurrentPage * PageSize)
                     .Take(PageSize))
        {
            VisibleApps.Add(new LaunchpadAppViewModel(app, Launch));
        }

        NotifyPagingState();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasApps));
        StartIconLoading();
    }

    private void PreviousPage()
    {
        if (!CanGoPrevious)
            return;

        CurrentPage--;
        ApplyFilterAndPage();
    }

    private void NextPage()
    {
        if (!CanGoNext)
            return;

        CurrentPage++;
        ApplyFilterAndPage();
    }

    private void Launch(InstalledApp app)
    {
        Error = null;
        try
        {
            _launcher(app);
            AppLaunched?.Invoke();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "启动台无法启动应用：{0}", app.Name);
            Error = $"无法启动「{app.Name}」。";
        }
    }

    private void StartIconLoading()
    {
        _iconCancellation?.Cancel();
        _iconCancellation?.Dispose();
        _iconCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _ = LoadVisibleIconsAsync(VisibleApps.ToArray(), _iconCancellation.Token);
    }

    private async Task LoadVisibleIconsAsync(
        IReadOnlyList<LaunchpadAppViewModel> apps,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var app in apps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = app.Model.IconPath ?? app.Model.LaunchTarget;
                var icon = await Task.Run(
                    () => _iconService.GetIcon(path),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested
                    || _disposed
                    || !VisibleApps.Contains(app))
                {
                    continue;
                }

                app.Icon = icon;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // 图标是装饰信息；目录和启动能力继续可用，背景字母作为回退。
            Logger.Debug(exception, "启动台图标加载提前结束");
        }
    }

    private void NotifyPagingState()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _iconCancellation?.Cancel();
        _iconCancellation?.Dispose();
        _iconCancellation = null;
        _lifetimeCancellation.Dispose();
    }
}

/// <summary>启动台名称搜索的纯逻辑实现。</summary>
internal static class LaunchpadSearch
{
    public static IReadOnlyList<InstalledApp> Filter(
        IReadOnlyList<InstalledApp> apps,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
            return apps.ToArray();

        return apps
            .Select((app, index) => new
            {
                App = app,
                Index = index,
                Rank = MatchRank(Normalize(app.Name), normalizedQuery),
            })
            .Where(static result => result.Rank >= 0)
            .OrderBy(static result => result.Rank)
            .ThenBy(static result => result.Index)
            .Select(static result => result.App)
            .ToArray();
    }

    private static int MatchRank(string candidate, string query)
    {
        if (candidate.StartsWith(query, StringComparison.Ordinal))
            return 0;
        if (candidate.Contains(query, StringComparison.Ordinal))
            return 1;

        var queryIndex = 0;
        foreach (var character in candidate)
        {
            if (queryIndex < query.Length && character == query[queryIndex])
                queryIndex++;
        }

        return queryIndex == query.Length ? 2 : -1;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}
