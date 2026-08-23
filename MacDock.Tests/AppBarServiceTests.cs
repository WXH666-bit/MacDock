using MacDock.Core.Interop;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// AppBarService 逻辑单测：用假 Shell 验证注册/SETPOS/DPI 变化/全屏让位/注销的状态机，
/// 不触碰真实 SHAppBarMessage（测试环境无 UI 线程消息循环，真调用只会得到假失败）。
/// </summary>
public sealed class AppBarServiceTests
{
    private const uint FakeCallbackMessage = 0xC001;

    private sealed class FakeAppBarShell : IAppBarShell
    {
        public List<(uint Message, NativeMethods.APPBARDATA Data)> Calls { get; } = new();

        public bool FailRegisterMessage { get; set; }

        public bool FailNew { get; set; }

        public bool FailSetPos { get; set; }

        public bool FailMonitor { get; set; }

        public uint RegisterMessage(string messageName)
            => FailRegisterMessage ? 0 : FakeCallbackMessage;

        public IntPtr SendMessage(uint message, ref NativeMethods.APPBARDATA data)
        {
            Calls.Add((message, data));
            return message switch
            {
                NativeMethods.ABM_NEW => FailNew ? IntPtr.Zero : (IntPtr)1,
                NativeMethods.ABM_SETPOS => FailSetPos ? IntPtr.Zero : (IntPtr)1,
                _ => IntPtr.Zero,
            };
        }

        public bool GetPrimaryMonitorBounds(out RECT bounds)
        {
            bounds = new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
            return !FailMonitor;
        }
    }

    private static NativeMethods.APPBARDATA? LastCall(
        FakeAppBarShell shell, uint message)
        => shell.Calls.Any(c => c.Message == message)
            ? shell.Calls.Last(c => c.Message == message).Data
            : null;

    [Fact]
    public void Register_SendsNewThenQueryPosThenSetPos()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);

        var ok = service.Register((IntPtr)0x1234, 32);

        Assert.True(ok);
        Assert.True(service.IsRegistered);
        Assert.Equal((uint)0xC001, service.CallbackMessage);

        var messages = shell.Calls.Select(c => c.Message).ToArray();
        Assert.Equal(
            new[] { NativeMethods.ABM_NEW, NativeMethods.ABM_QUERYPOS, NativeMethods.ABM_SETPOS },
            messages);
    }

    [Fact]
    public void Register_SetPosRequestsPrimaryTopStrip()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);

        service.Register((IntPtr)0x1234, 40);

        var setPos = shell.Calls.First(c => c.Message == NativeMethods.ABM_SETPOS).Data;
        Assert.Equal(NativeMethods.ABE_TOP, (int)setPos.uEdge);
        Assert.Equal(0, setPos.rc.left);
        Assert.Equal(0, setPos.rc.top);
        Assert.Equal(1920, setPos.rc.right);
        Assert.Equal(40, setPos.rc.bottom);
    }

    [Fact]
    public void Register_NewFailure_DegradesWithoutThrowing()
    {
        var shell = new FakeAppBarShell { FailNew = true };
        using var service = new AppBarService(shell);

        var ok = service.Register((IntPtr)0x1234, 32);

        Assert.False(ok);
        Assert.False(service.IsRegistered);
    }

    [Fact]
    public void Register_RegisterMessageFailure_DegradesWithoutThrowing()
    {
        var shell = new FakeAppBarShell { FailRegisterMessage = true };
        using var service = new AppBarService(shell);

        Assert.False(service.Register((IntPtr)0x1234, 32));
        Assert.False(service.IsRegistered);
        Assert.Equal(0u, service.CallbackMessage);
    }

    [Fact]
    public void Register_SetPosFailure_DegradesWithoutThrowing()
    {
        var shell = new FakeAppBarShell { FailSetPos = true };
        using var service = new AppBarService(shell);

        Assert.False(service.Register((IntPtr)0x1234, 32));
        Assert.False(service.IsRegistered);
    }

    [Fact]
    public void Register_Twice_DoesNotDuplicateAbmNew()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);

        service.Register((IntPtr)0x1234, 32);
        service.Register((IntPtr)0x1234, 48);

        Assert.Equal(1, shell.Calls.Count(c => c.Message == NativeMethods.ABM_NEW));
        Assert.Equal(2, shell.Calls.Count(c => c.Message == NativeMethods.ABM_SETPOS));
    }

    [Fact]
    public void Register_InvalidArguments_ReturnsFalse()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);

        Assert.False(service.Register(IntPtr.Zero, 32));
        Assert.False(service.Register((IntPtr)0x1234, 0));
        Assert.False(service.Register((IntPtr)0x1234, -1));
    }

    [Fact]
    public void UpdatePosition_AfterRegister_ReappliesSetPos()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);
        service.Register((IntPtr)0x1234, 32);

        var ok = service.UpdatePosition(64);

        Assert.True(ok);
        var lastSetPos = shell.Calls.Last(c => c.Message == NativeMethods.ABM_SETPOS).Data;
        Assert.Equal(64, lastSetPos.rc.bottom);
    }

    [Fact]
    public void UpdatePosition_BeforeRegister_Fails()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);

        Assert.False(service.UpdatePosition(32));
    }

    [Fact]
    public void HandleCallback_FullscreenApp_EntersAndExits()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);
        service.Register((IntPtr)0x1234, 32);

        var entering = service.HandleCallback((IntPtr)NativeMethods.ABN_FULLSCREENAPP, (IntPtr)1);
        var exiting = service.HandleCallback((IntPtr)NativeMethods.ABN_FULLSCREENAPP, IntPtr.Zero);

        Assert.True(entering);
        Assert.False(exiting);
    }

    [Fact]
    public void HandleCallback_OtherNotification_ReturnsNull()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);
        service.Register((IntPtr)0x1234, 32);

        Assert.Null(service.HandleCallback((IntPtr)0x0000001, IntPtr.Zero));
    }

    [Fact]
    public void HandleCallback_BeforeRegister_ReturnsNull()
    {
        var shell = new FakeAppBarShell();
        using var service = new AppBarService(shell);

        Assert.Null(service.HandleCallback((IntPtr)NativeMethods.ABN_FULLSCREENAPP, (IntPtr)1));
    }

    [Fact]
    public void Unregister_SendsAbmRemoveOnce()
    {
        var shell = new FakeAppBarShell();
        var service = new AppBarService(shell);
        service.Register((IntPtr)0x1234, 32);

        service.Unregister();
        service.Unregister();

        Assert.Equal(1, shell.Calls.Count(c => c.Message == NativeMethods.ABM_REMOVE));
        Assert.False(service.IsRegistered);
    }

    [Fact]
    public void Dispose_Unregisters()
    {
        var shell = new FakeAppBarShell();
        var service = new AppBarService(shell);
        service.Register((IntPtr)0x1234, 32);

        service.Dispose();

        Assert.Contains(shell.Calls, c => c.Message == NativeMethods.ABM_REMOVE);
    }

    [Fact]
    public void MonitorFailure_DegradesWithoutThrowing()
    {
        var shell = new FakeAppBarShell { FailMonitor = true };
        using var service = new AppBarService(shell);

        Assert.False(service.Register((IntPtr)0x1234, 32));
    }
}
