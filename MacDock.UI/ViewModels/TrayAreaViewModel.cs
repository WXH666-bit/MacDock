using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Models;
using MacDock.Core.Services;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 菜单栏托盘区视图模型：维护可见托盘图标与溢出图标的集合，
/// 500ms 节流探测按钮数（仅 SendMessage，微秒级），计数变化才全量重读并差量更新；
/// 计数不变但内容变化（微信红点等）由 5 秒周期兜底全量感知。
/// 监听 explorer 重启（TaskbarCreated）时重置重枚举。
/// 图标转换放在后台线程批量完成，不逐个卡 UI。
/// 点击转发由对话框代码通过 <see cref="ForwardClick"/> 完成。
/// </summary>
public sealed partial class TrayAreaViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 内容变化兜底全量间隔。计数节流只对「增删」有效：图标内容变化（微信红点、OneDrive 状态切换）
    /// 不改变按钮数，故需周期兜底才不漏。一次全量 ≈ 每按钮 4 次跨进程调用 × 约 20 按钮 ≈ 毫秒级，
    /// 5 秒一次无感。
    /// </summary>
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ITrayIconReader _reader;
    private readonly Func<IntPtr, ImageSource> _iconFactory;
    private readonly DispatcherTimer _timer;
    private bool _enabled;
    private uint _lastVisibleCount = uint.MaxValue;
    private uint _lastOverflowCount = uint.MaxValue;
    private bool _updateInFlight;
    private bool _disposed;
    private DateTime _lastFullRefreshUtc = DateTime.MinValue;

    /// <summary>可见托盘图标（menu bar 横排）。</summary>
    public ObservableCollection<TrayIconItem> Visible { get; } = new();

    /// <summary>溢出托盘图标（chevron 弹层内）。</summary>
    public ObservableCollection<TrayIconItem> Overflow { get; } = new();

    /// <summary>是否有溢出图标需要显示 chevron。</summary>
    [ObservableProperty]
    private bool _hasOverflow;

    /// <summary>是否接管托盘（TrayTakeover 开关，false 时整个托盘区隐藏）。</summary>
    [ObservableProperty]
    private bool _isTrayEnabled;

    /// <param name="reader">托盘读取器（生命周期由调用方持有）。</param>
    /// <param name="enabled">是否接管（TrayTakeover=false 时不渲染托盘区）。</param>
    /// <param name="iconFactory">HICON → ImageSource 转换（默认 IconService.FromHIcon）。</param>
    public TrayAreaViewModel(
        ITrayIconReader reader,
        bool enabled,
        Func<IntPtr, ImageSource>? iconFactory = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _enabled = enabled;
        _iconFactory = iconFactory ?? IconService.FromHIcon;

        IsTrayEnabled = enabled;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ProbeInterval,
        };
        _timer.Tick += OnProbeTick;
    }

    /// <summary>启动轮询与首次加载。</summary>
    public void Start()
    {
        if (!_enabled)
        {
            Visible.Clear();
            Overflow.Clear();
            HasOverflow = false;
            return;
        }

        _timer.Start();
        TriggerRefresh();
    }

    /// <summary>explorer 重启（TaskbarCreated 广播）后调用：重置计数并立即重枚举。</summary>
    public void ResetForExplorerRestart()
    {
        if (!_enabled || _disposed)
            return;

        _lastVisibleCount = uint.MaxValue;
        _lastOverflowCount = uint.MaxValue;
        TriggerRefresh();
    }

    /// <summary>转发一次托盘点击（调用方已把鼠标消息映射为 mouseMessage）。</summary>
    public void ForwardClick(TrayIconItem item, uint mouseMessage)
    {
        if (item?.Info is null)
            return;

        TrayIconForwarder.SendClick(
            item.Info.HwndTarget,
            item.Info.UCallbackMessage,
            item.Info.UId,
            mouseMessage);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Tick -= OnProbeTick;
        _timer.Stop();
    }

    private void OnProbeTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        // 轻量探测：只读两个 TB_BUTTONCOUNT，微秒级
        var visible = _reader.ProbeVisibleCount();
        var overflow = _reader.ProbeOverflowCount();
        var countsChanged = visible != _lastVisibleCount || overflow != _lastOverflowCount;

        // 计数没变也需周期兜底：图标内容变化（微信红点等）不改变按钮数，计数节流对此无效。
        // 距上次全量超 5s 也再刷一轮，保证内容变化被感知（性能见 FullRefreshInterval 注释）。
        var dueForFull = DateTime.UtcNow - _lastFullRefreshUtc >= FullRefreshInterval;
        if (!countsChanged && !dueForFull)
            return;

        TriggerRefresh();
    }

    private void TriggerRefresh()
    {
        if (!_enabled || _disposed)
            return;

        // 单飞行：上一次全量更新还没完成就不发新一轮
        if (_updateInFlight)
            return;

        _updateInFlight = true;
        _lastFullRefreshUtc = DateTime.UtcNow;
        _lastVisibleCount = _reader.ProbeVisibleCount();
        _lastOverflowCount = _reader.ProbeOverflowCount();

        _ = Task.Run(() =>
        {
            // 一次性读取并批量转换图标（IconService.FromHIcon 后台线程安全，位图已 Freeze）
            var infos = _reader.Read();
            var visible = new List<TrayIconItem>();
            var overflow = new List<TrayIconItem>();
            foreach (var info in infos)
            {
                var item = new TrayIconItem(_iconFactory(info.HIcon), info);
                if (info.IsOverflow)
                    overflow.Add(item);
                else
                    visible.Add(item);
            }

            return (VisibleItems: visible, OverflowItems: overflow);
        }).ContinueWith(t =>
        {
            _updateInFlight = false;
            if (_disposed || t.IsFaulted)
                return;

            ApplyDiff(Visible, t.Result.VisibleItems);
            ApplyDiff(Overflow, t.Result.OverflowItems);
            HasOverflow = Overflow.Count > 0;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 差量更新集合：移除已消失的，加入新的，替换内容变化的，保留未变的。
    /// 内容变化：Key 相同但 <see cref="TrayIconInfo.HIcon"/> 或 <see cref="TrayIconInfo.Tooltip"/>
    /// 不同（应用 NIM_MODIFY 换图标）→ 整项替换。用索引 setter 触发 ObservableCollection 的
    /// Replace 通知，只刷新这一项，其余不动不闪。
    /// </summary>
    internal static void ApplyDiff(ObservableCollection<TrayIconItem> current, List<TrayIconItem> fresh)
    {
        // 以 Key 为索引的 fresh 查找表，避免逐项线性搜索
        var freshByKey = new Dictionary<string, TrayIconItem>(fresh.Count);
        foreach (var item in fresh)
            freshByKey[item.Info.Key] = item;

        // 移除已消失
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!freshByKey.ContainsKey(current[i].Info.Key))
                current.RemoveAt(i);
        }

        // 内容变化（Key 相同但图标/提示不同）→ 该项整项替换，保持位置。
        // HIcon 是 IntPtr 用 != 直接比对；Tooltip 用序数比较（大小写敏感，与 Windows 一致）。
        for (int i = 0; i < current.Count; i++)
        {
            var kept = current[i];
            if (!freshByKey.TryGetValue(kept.Info.Key, out var replacement))
                continue;

            var freshInfo = replacement.Info;
            if (freshInfo.HIcon != kept.Info.HIcon
                || !string.Equals(freshInfo.Tooltip, kept.Info.Tooltip, StringComparison.Ordinal))
            {
                current[i] = replacement;
            }
        }

        // 加入新的（按 Key 排序稳定顺序）
        var currentKeys = new HashSet<string>(current.Select(i => i.Info.Key));
        foreach (var item in fresh.OrderBy(i => i.Info.Key, StringComparer.Ordinal))
        {
            if (!currentKeys.Contains(item.Info.Key))
                current.Add(item);
        }
    }
}
