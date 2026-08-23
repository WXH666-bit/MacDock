using System.Collections.ObjectModel;
using System.Windows.Media;
using MacDock.Core.Models;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// 托盘区 VM 逻辑：差量更新（增/删/留）、禁用清理、点击转发不抛。
/// 用假读取器与假图标工厂，避开真实 explorer 与 WPF 调度器的环境依赖。
/// </summary>
public sealed class TrayAreaViewModelTests
{
    private sealed class FakeReader : ITrayIconReader
    {
        public IReadOnlyList<TrayIconInfo> Items { get; set; } = Array.Empty<TrayIconInfo>();

        public uint VisibleProbe { get; set; }

        public uint OverflowProbe { get; set; }

        public IReadOnlyList<TrayIconInfo> Read() => Items;

        public uint ProbeVisibleCount() => VisibleProbe;

        public uint ProbeOverflowCount() => OverflowProbe;

        public void Dispose()
        {
        }
    }

    private static readonly ImageSource FakeImage = CreateFrozenBitmap();

    private static ImageSource CreateFrozenBitmap()
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static TrayIconInfo Info(IntPtr hwnd, uint uid, bool overflow = false, IntPtr? hIcon = null, string? tooltip = "tip")
    {
        var icon = hIcon ?? new IntPtr(0x1234);
        return new(TrayIconInfo.BuildKey(hwnd, uid), icon, tooltip, overflow, hwnd, 0x00C1, uid);
    }

    private static TrayIconItem Item(IntPtr hwnd, uint uid, bool overflow = false, IntPtr? hIcon = null, string? tooltip = "tip")
        => new(FakeImage, Info(hwnd, uid, overflow, hIcon, tooltip));

    [Fact]
    public void Start_Disabled_ClearsCollectionsAndChevron()
    {
        var reader = new FakeReader { Items = new[] { Info((IntPtr)0x10, 1) } };
        var vm = new TrayAreaViewModel(reader, enabled: false);

        vm.Start();

        Assert.Empty(vm.Visible);
        Assert.Empty(vm.Overflow);
        Assert.False(vm.HasOverflow);
        Assert.False(vm.IsTrayEnabled);
        vm.Dispose();
    }

    [Fact]
    public void ApplyDiff_RemovesGoneKeepsSameAddsNew()
    {
        var current = new ObservableCollection<TrayIconItem>
        {
            Item((IntPtr)0x10, 1),
            Item((IntPtr)0x11, 2),
        };
        var fresh = new List<TrayIconItem>
        {
            Item((IntPtr)0x11, 2),   // 保留
            Item((IntPtr)0x12, 3),   // 新增
        };

        TrayAreaViewModel.ApplyDiff(current, fresh);

        Assert.Equal(2, current.Count);
        Assert.Contains(current, i => i.Info.Key == TrayIconInfo.BuildKey((IntPtr)0x11, 2));
        Assert.Contains(current, i => i.Info.Key == TrayIconInfo.BuildKey((IntPtr)0x12, 3));
        Assert.DoesNotContain(current, i => i.Info.Key == TrayIconInfo.BuildKey((IntPtr)0x10, 1));
    }

    [Fact]
    public void ApplyDiff_EmptyFresh_ClearsAll()
    {
        var current = new ObservableCollection<TrayIconItem> { Item((IntPtr)0x10, 1) };

        TrayAreaViewModel.ApplyDiff(current, new List<TrayIconItem>());

        Assert.Empty(current);
    }

    [Fact]
    public void ApplyDiff_SameKeyDifferentIcon_ReplacesItem()
    {
        var current = new ObservableCollection<TrayIconItem> { Item((IntPtr)0x10, 1) };
        // 同 Key（hWnd=0x10, uID=1）HIcon 不同（微信换红点图标）→ 该项应整项替换，保持位置
        var fresh = new List<TrayIconItem> { Item((IntPtr)0x10, 1, hIcon: new IntPtr(0x55AA)) };

        TrayAreaViewModel.ApplyDiff(current, fresh);

        Assert.Single(current);
        Assert.Equal(new IntPtr(0x55AA), current[0].Info.HIcon);
    }

    [Fact]
    public void ApplyDiff_SameKeyDifferentTooltip_ReplacesItem()
    {
        var current = new ObservableCollection<TrayIconItem> { Item((IntPtr)0x10, 1) };
        var fresh = new List<TrayIconItem> { Item((IntPtr)0x10, 1, tooltip: "3 条新消息") };

        TrayAreaViewModel.ApplyDiff(current, fresh);

        Assert.Single(current);
        Assert.Equal("3 条新消息", current[0].Info.Tooltip);
    }

    [Fact]
    public void ApplyDiff_SameKeySameIconAndTooltip_KeepsOriginalInstance()
    {
        var original = Item((IntPtr)0x10, 1);
        var current = new ObservableCollection<TrayIconItem> { original };
        var fresh = new List<TrayIconItem> { Item((IntPtr)0x10, 1) };

        TrayAreaViewModel.ApplyDiff(current, fresh);

        // 图标与提示都没变 → 保留原实例（引用相同，不触发放置/刷新）
        Assert.Same(original, current[0]);
    }

    [Fact]
    public void ForwardClick_DoesNotThrow_OnInvalidTarget()
    {
        var reader = new FakeReader();
        var vm = new TrayAreaViewModel(reader, enabled: true);
        var item = Item((IntPtr)0xDEAD, 7);

        // 目标窗口不存在，PostMessage 返回 false，但不应抛异常
        vm.ForwardClick(item, TrayIconForwarder.MouseLeftButtonUp);
        vm.ForwardClick(item, TrayIconForwarder.MouseRightButtonUp);

        vm.Dispose();
    }
}
