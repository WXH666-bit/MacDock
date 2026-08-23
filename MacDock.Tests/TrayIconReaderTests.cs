using MacDock.Core.Models;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// TrayIconReader 逻辑：两链合并、IsOverflow 标记、0 窗口跳过、失败返回空、键格式、转发常量。
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

        public IReadOnlyList<RawTrayButton> ScanChain(bool overflow)
        {
            if (Throw)
                throw new InvalidOperationException("boom");

            return overflow ? Overflow : Visible;
        }

        public uint CountChain(bool overflow) => overflow ? OverflowCount : VisibleCount;
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

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Count(i => !i.IsOverflow));
        Assert.Equal(1, result.Count(i => i.IsOverflow));

        var first = result[0];
        Assert.Equal(TrayIconInfo.BuildKey((IntPtr)0x10, 1), first.Key);
        Assert.Equal((IntPtr)0x10, first.HwndTarget);
        Assert.Equal(0x00C1u, first.UCallbackMessage);
        Assert.Equal((IntPtr)0x1234, first.HIcon);
    }

    [Fact]
    public void Read_SkipsZeroHwnd()
    {
        var scan = new FakeScan
        {
            Visible = new[] { Button(IntPtr.Zero, 1), Button((IntPtr)0x12, 2) },
        };
        using var reader = new TrayIconReader(scan);

        var result = reader.Read();

        Assert.Single(result);
        Assert.Equal((IntPtr)0x12, result[0].HwndTarget);
    }

    [Fact]
    public void Read_ScannerThrows_ReturnsEmptyWithoutThrowing()
    {
        var scan = new FakeScan { Throw = true };
        using var reader = new TrayIconReader(scan);

        Assert.Empty(reader.Read());
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
