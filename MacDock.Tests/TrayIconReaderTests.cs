using MacDock.Core.Models;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// TrayIconReader 逻辑：两链合并、IsOverflow 标记、0 窗口跳过、空结果与失败区分、键格式、转发常量。
/// 注射假扫描器，不触碰真实 explorer（测试环境也无托盘窗口可读）。
/// </summary>
public sealed class TrayIconReaderTests
{
    private sealed class FakeScan : ITrayToolbarScan
    {
        public IReadOnlyList<RawTrayButton> Visible { get; set; } = Array.Empty<RawTrayButton>();

        public IReadOnlyList<RawTrayButton> Overflow { get; set; } = Array.Empty<RawTrayButton>();

        public bool Throw { get; set; }

        public uint VisibleCount { get; set; }

        public uint OverflowCount { get; set; }

        public bool OverflowAvailable { get; set; } = true;

        public TrayToolbarScanResult ScanChain(bool overflow)
        {
            if (Throw)
                throw new InvalidOperationException("boom");

            return overflow
                ? new TrayToolbarScanResult(OverflowAvailable, Overflow)
                : new TrayToolbarScanResult(true, Visible);
        }

        public uint? CountChain(bool overflow)
        {
            if (Throw)
                throw new InvalidOperationException("boom");

            return overflow
                ? OverflowAvailable ? OverflowCount : null
                : VisibleCount;
        }
    }

    private static RawTrayButton Button(IntPtr hwnd, uint uid, IntPtr? icon = null, string? tooltip = null)
        => new(0, hwnd, uid, 0x00C1, icon ?? (IntPtr)0x1234, tooltip);

    [Fact]
    public void Read_MergesVisibleAndOverflow()
    {
        var scan = new FakeScan
        {
            Visible = new[] { Button((IntPtr)0x10, 1), Button((IntPtr)0x11, 2) },
            Overflow = new[] { Button((IntPtr)0x20, 3) },
        };
        using var reader = new TrayIconReader(scan);

        var result = reader.Read();

        Assert.True(result.OverflowAvailable);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(2, result.Items.Count(i => !i.IsOverflow));
        Assert.Equal(1, result.Items.Count(i => i.IsOverflow));

        var first = result.Items[0];
        Assert.Equal(TrayIconInfo.BuildKey((IntPtr)0x10, 1), first.Key);
        Assert.Equal((IntPtr)0x10, first.HwndTarget);
        Assert.Equal(0x00C1u, first.UCallbackMessage);
        Assert.Equal((IntPtr)0x1234, first.HIcon);
    }

    [Fact]
    public void Read_ZeroHwndRejectsIncompleteSnapshot()
    {
        var scan = new FakeScan
        {
            Visible = new[] { Button(IntPtr.Zero, 1), Button((IntPtr)0x12, 2) },
        };
        using var reader = new TrayIconReader(scan);

        Assert.Throws<TrayIconReaderException>(() => reader.Read());
    }

    [Fact]
    public void Read_ScannerThrows_ExposesFailureWithoutConfusingItWithEmpty()
    {
        var scan = new FakeScan { Throw = true };
        using var reader = new TrayIconReader(scan);

        var exception = Assert.Throws<TrayIconReaderException>(() => reader.Read());
        Assert.Contains("读取托盘图标失败", exception.Message);
    }

    [Fact]
    public void Read_EmptyChains_IsAValidEmptyResult()
    {
        using var reader = new TrayIconReader(new FakeScan());

        Assert.Empty(reader.Read().Items);
    }

    [Fact]
    public void Probe_ScannerThrows_ExposesFailure()
    {
        using var reader = new TrayIconReader(new FakeScan { Throw = true });

        Assert.Throws<TrayIconReaderException>(() => reader.ProbeVisibleCount());
    }

    [Fact]
    public void ProbeCounts_ReflectScanner()
    {
        var scan = new FakeScan { VisibleCount = 4, OverflowCount = 2 };
        using var reader = new TrayIconReader(scan);

        Assert.Equal(4u, reader.ProbeVisibleCount());
        Assert.Equal(2u, reader.ProbeOverflowCount());
    }

    [Fact]
    public void Read_UnavailableOverflowIsDistinctFromEmptyOverflow()
    {
        var scan = new FakeScan
        {
            Visible = new[] { Button((IntPtr)0x10, 1) },
            OverflowAvailable = false,
        };
        using var reader = new TrayIconReader(scan);

        var result = reader.Read();

        Assert.False(result.OverflowAvailable);
        Assert.Single(result.Items);
        Assert.Null(reader.ProbeOverflowCount());
    }

    [Fact]
    public void UnsupportedTopologyException_IsAReaderFailure()
    {
        var exception = new TrayIconTopologyUnsupportedException("modern taskbar");

        Assert.IsAssignableFrom<TrayIconReaderException>(exception);
    }

    [Fact]
    public void BuildKey_IsStableAndDistinct()
    {
        var a = TrayIconInfo.BuildKey((IntPtr)0x100, 1);
        var b = TrayIconInfo.BuildKey((IntPtr)0x100, 2);
        var c = TrayIconInfo.BuildKey((IntPtr)0x101, 1);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, TrayIconInfo.BuildKey((IntPtr)0x100, 1));
    }

    [Fact]
    public void Forwarder_Constants_HaveExpectedMouseMessages()
    {
        Assert.Equal(0x0202u, TrayIconForwarder.MouseLeftButtonUp);
        Assert.Equal(0x0205u, TrayIconForwarder.MouseRightButtonUp);
        Assert.Equal(0x0203u, TrayIconForwarder.MouseLeftDoubleClick);
    }
}
