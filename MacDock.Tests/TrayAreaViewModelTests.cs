using System.Collections.Concurrent;
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

        public bool OverflowAvailable { get; set; } = true;

        public bool ThrowOnProbe { get; set; }

        public bool ThrowOnRead { get; set; }

        public Exception? ProbeException { get; set; }

        public ManualResetEventSlim? ReadStarted { get; set; }

        public ManualResetEventSlim? ReadGate { get; set; }

        public int ReadCalls;

        public int VisibleProbeCalls;

        public int OverflowProbeCalls;

        public ConcurrentBag<int> ReaderThreadIds { get; } = new();

        public TrayIconReadResult Read()
        {
            Interlocked.Increment(ref ReadCalls);
            ReaderThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            ReadStarted?.Set();
            ReadGate?.Wait();
            if (ThrowOnRead)
                throw new InvalidOperationException("fake read failure");

            return new TrayIconReadResult(Items, OverflowAvailable);
        }

        public uint ProbeVisibleCount()
        {
            Interlocked.Increment(ref VisibleProbeCalls);
            ReaderThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            if (ProbeException is not null)
                throw ProbeException;

            if (ThrowOnProbe)
                throw new InvalidOperationException("fake probe failure");

            return VisibleProbe;
        }

        public uint? ProbeOverflowCount()
        {
            Interlocked.Increment(ref OverflowProbeCalls);
            ReaderThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            if (ProbeException is not null)
                throw ProbeException;

            if (ThrowOnProbe)
                throw new InvalidOperationException("fake probe failure");

            return OverflowAvailable ? OverflowProbe : null;
        }

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

    private static TrayAreaViewModel CreateTestViewModel(
        FakeReader reader,
        bool enabled = true,
        Func<IntPtr, ImageSource>? iconFactory = null,
        Func<DateTime>? utcNow = null)
        => new(
            reader,
            enabled,
            iconFactory ?? (_ => FakeImage),
            action =>
            {
                action();
                return true;
            },
            utcNow);

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
        Assert.Equal(0, reader.VisibleProbeCalls);
        Assert.Equal(0, reader.OverflowProbeCalls);
        Assert.Equal(0, reader.ReadCalls);
        vm.Dispose();
    }

    [Fact]
    public async Task Refresh_ReaderAndIconFactoryRunOffUiThread()
    {
        var reader = new FakeReader
        {
            Items = new[] { Info((IntPtr)0x10, 1) },
            VisibleProbe = 1,
        };
        var iconThreads = new ConcurrentBag<int>();
        var vm = CreateTestViewModel(
            reader,
            iconFactory: _ =>
            {
                iconThreads.Add(Thread.CurrentThread.ManagedThreadId);
                return FakeImage;
            });

        Task? refresh = null;
        var requestThreadId = 0;
        using var submitted = new ManualResetEventSlim();
        var requestThread = new Thread(() =>
        {
            requestThreadId = Thread.CurrentThread.ManagedThreadId;
            refresh = vm.RequestRefreshForTests();
            submitted.Set();
        });
        requestThread.Start();
        Assert.True(submitted.Wait(TimeSpan.FromSeconds(5)));
        requestThread.Join();

        await Assert.IsAssignableFrom<Task>(refresh).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, reader.ReadCalls);
        Assert.Equal(1, reader.VisibleProbeCalls);
        Assert.Equal(1, reader.OverflowProbeCalls);
        Assert.NotEmpty(reader.ReaderThreadIds);
        Assert.DoesNotContain(requestThreadId, reader.ReaderThreadIds);
        Assert.DoesNotContain(requestThreadId, iconThreads);
        Assert.Single(vm.Visible);
        vm.Dispose();
    }

    [Fact]
    public async Task Refresh_IsSingleFlightWithoutBlockingSecondRequest()
    {
        using var readStarted = new ManualResetEventSlim();
        using var readGate = new ManualResetEventSlim();
        var reader = new FakeReader
        {
            Items = new[] { Info((IntPtr)0x10, 1) },
            VisibleProbe = 1,
            ReadStarted = readStarted,
            ReadGate = readGate,
        };
        var vm = CreateTestViewModel(reader);

        var first = vm.RequestRefreshForTests();
        Assert.True(readStarted.Wait(TimeSpan.FromSeconds(5)));

        var second = vm.RequestRefreshForTests();
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, reader.ReadCalls);
        Assert.True(vm.RefreshInFlightForTests);

        readGate.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.RefreshInFlightForTests);
        Assert.Single(vm.Visible);
        vm.Dispose();
    }

    [Fact]
    public async Task Refresh_UsesExponentialBackoffAndSuccessResetsIt()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var reader = new FakeReader { ThrowOnProbe = true };
        var vm = CreateTestViewModel(
            reader,
            utcNow: () => new DateTime(Volatile.Read(ref nowTicks), DateTimeKind.Utc));
        var expected = new[]
        {
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(10),
        };

        for (var index = 0; index < expected.Length; index++)
        {
            await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsRefreshDegraded);
            Assert.NotNull(vm.LastRefreshError);
            Assert.Equal(expected[index], vm.RetryDelayForTests);
            Assert.Equal(index + 1, vm.FailureStreakForTests);

            Volatile.Write(ref nowTicks, vm.NextAttemptUtcForTests.Ticks);
        }

        reader.ThrowOnProbe = false;
        reader.VisibleProbe = 0;
        reader.OverflowProbe = 0;
        reader.Items = Array.Empty<TrayIconInfo>();
        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsRefreshDegraded);
        Assert.Null(vm.LastRefreshError);
        Assert.Equal(0, vm.FailureStreakForTests);
        Assert.Equal(TimeSpan.FromMilliseconds(500), vm.RetryDelayForTests);
        Assert.Empty(vm.Visible);
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_DropsBackgroundResultWithoutWritingCollections()
    {
        using var readStarted = new ManualResetEventSlim();
        using var readGate = new ManualResetEventSlim();
        var reader = new FakeReader
        {
            Items = new[] { Info((IntPtr)0x10, 1) },
            VisibleProbe = 1,
            ReadStarted = readStarted,
            ReadGate = readGate,
        };
        var vm = CreateTestViewModel(reader);
        var refresh = vm.RequestRefreshForTests();

        Assert.True(readStarted.Wait(TimeSpan.FromSeconds(5)));
        vm.Dispose();
        readGate.Set();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(vm.Visible);
        Assert.Empty(vm.Overflow);
        Assert.False(vm.HasOverflow);
    }

    [Fact]
    public async Task Refresh_FailedFullReadPreservesLastSuccessfulCollections()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var reader = new FakeReader
        {
            VisibleProbe = 1,
            Items = new[] { Info((IntPtr)0x10, 1) },
        };
        var vm = CreateTestViewModel(
            reader,
            utcNow: () => new DateTime(Volatile.Read(ref nowTicks), DateTimeKind.Utc));

        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(vm.Visible);

        reader.VisibleProbe = 2;
        reader.ThrowOnRead = true;
        Volatile.Write(ref nowTicks, vm.NextAttemptUtcForTests.Ticks);
        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(vm.Visible);
        Assert.Equal(TrayIconInfo.BuildKey((IntPtr)0x10, 1), vm.Visible[0].Info.Key);
        Assert.True(vm.IsRefreshDegraded);
        Assert.Contains("fake read failure", vm.LastRefreshError);
        vm.Dispose();
    }

    [Fact]
    public async Task Refresh_SessionFailureHidesAndClearsStaleClickableItems()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var reader = new FakeReader
        {
            VisibleProbe = 1,
            OverflowProbe = 1,
            Items = new[]
            {
                Info((IntPtr)0x10, 1),
                Info((IntPtr)0x20, 2, overflow: true),
            },
        };
        var vm = CreateTestViewModel(
            reader,
            utcNow: () => new DateTime(Volatile.Read(ref nowTicks), DateTimeKind.Utc));

        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(vm.Visible);
        Assert.Single(vm.Overflow);

        reader.ProbeException = new TrayIconSessionUnavailableException("unsafe session");
        Volatile.Write(ref nowTicks, vm.NextAttemptUtcForTests.Ticks);
        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(vm.Visible);
        Assert.Empty(vm.Overflow);
        Assert.False(vm.HasOverflow);
        Assert.False(vm.IsTrayEnabled);
        Assert.Equal(DateTime.MaxValue, vm.NextAttemptUtcForTests);
        Assert.Contains("unsafe session", vm.LastRefreshError);
        vm.Dispose();
    }

    [Fact]
    public async Task Refresh_UnavailableOverflowWindowPreservesLastOverflowCollection()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var reader = new FakeReader
        {
            OverflowProbe = 1,
            Items = new[] { Info((IntPtr)0x20, 2, overflow: true) },
        };
        var vm = CreateTestViewModel(
            reader,
            utcNow: () => new DateTime(Volatile.Read(ref nowTicks), DateTimeKind.Utc));

        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(vm.Overflow);

        reader.OverflowAvailable = false;
        reader.Items = Array.Empty<TrayIconInfo>();
        Volatile.Write(ref nowTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(vm.Overflow);
        Assert.True(vm.HasOverflow);
        vm.Dispose();
    }

    [Fact]
    public async Task ResetBeforeStart_DoesNotBypassLoadedStartupGate()
    {
        var reader = new FakeReader();
        var vm = CreateTestViewModel(reader);

        vm.ResetForExplorerRestart();
        await Task.Delay(50);

        Assert.Equal(0, reader.VisibleProbeCalls);
        Assert.Equal(0, reader.ReadCalls);
        vm.Dispose();
    }

    [Fact]
    public void ExplorerRestartDuringRefresh_QueuesImmediateReplacementScan()
    {
        using var readStarted = new ManualResetEventSlim();
        using var readGate = new ManualResetEventSlim();
        var reader = new FakeReader
        {
            VisibleProbe = 1,
            Items = new[] { Info((IntPtr)0x10, 1) },
            ReadStarted = readStarted,
            ReadGate = readGate,
        };
        var vm = CreateTestViewModel(reader);

        vm.Start();
        Assert.True(readStarted.Wait(TimeSpan.FromSeconds(5)));
        vm.ResetForExplorerRestart();
        readGate.Set();

        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref reader.ReadCalls) >= 2 && vm.Visible.Count == 1,
            TimeSpan.FromSeconds(5)));
        vm.Dispose();
    }

    [Fact]
    public async Task Refresh_UnsupportedTopologyStopsFurtherRequestsUntilExplorerRestart()
    {
        var reader = new FakeReader
        {
            ProbeException = new TrayIconTopologyUnsupportedException("modern tray"),
        };
        var vm = CreateTestViewModel(reader);

        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsTrayEnabled);
        Assert.True(vm.IsRefreshDegraded);
        Assert.Contains("modern tray", vm.LastRefreshError);
        Assert.Equal(DateTime.MaxValue, vm.NextAttemptUtcForTests);

        await vm.RequestRefreshForTests().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reader.VisibleProbeCalls);

        vm.Start();
        reader.ProbeException = null;
        vm.ResetForExplorerRestart();
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref reader.ReadCalls) == 1 && !vm.IsRefreshDegraded,
            TimeSpan.FromSeconds(5)));
        Assert.True(vm.IsTrayEnabled);
        Assert.False(vm.IsRefreshDegraded);
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
