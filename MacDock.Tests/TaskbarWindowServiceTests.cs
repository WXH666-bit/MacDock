using MacDock.Core.Interop;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class TaskbarWindowServiceTests
{
    [Fact]
    public void CapturePrimary_ReturnsOnlyVerifiedPrimaryShellTrayWindow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);

        var snapshot = new TaskbarWindowService(platform).CapturePrimary();

        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot!.Handle);
        Assert.Equal((uint)100, snapshot.ProcessId);
        Assert.Equal(1234, snapshot.ProcessStartTimeUtcTicks);
        Assert.Equal("Shell_TrayWnd", snapshot.ClassName);
        Assert.Equal(7, snapshot.MonitorHandle);
        Assert.True(snapshot.WasVisible);
        Assert.Equal(NativeMethods.SW_SHOW, snapshot.ShowCommand);
        Assert.Equal(TaskbarWindowMutationState.Unchanged, snapshot.MutationState);
    }

    [Fact]
    public void CapturePrimary_FiltersRequestedClassWhenAdditionalWindowsExist()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        platform.AddWindow(
            handle: 99,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW,
            className: "OtherWindow",
            processName: "other");

        var snapshot = new TaskbarWindowService(platform).CapturePrimary();

        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot!.Handle);
    }

    [Theory]
    [InlineData("ApplicationFrameWindow", "ApplicationFrameHost", 7)]
    [InlineData("CabinetWClass", "explorer", 7)]
    [InlineData("Shell_SecondaryTrayWnd", "explorer", 7)]
    [InlineData("Shell_TrayWnd", "explorer", 8)]
    public void CapturePrimary_RejectsAnythingOutsideExactScope(
        string className,
        string processName,
        long monitor)
    {
        var platform = FakeTaskbarPlatform.Window(42, className, processName, monitor);

        Assert.Null(new TaskbarWindowService(platform).CapturePrimary());
    }

    [Fact]
    public void CapturePrimary_PreservesInvisiblePrimaryShellTrayWindow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);

        var snapshot = new TaskbarWindowService(platform).CapturePrimary();

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.WasVisible);
        Assert.Equal(TaskbarWindowMutationState.Unchanged, snapshot.MutationState);
    }

    [Fact]
    public void CapturePrimary_RejectsMissingWindowPlacement()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        platform.SetShowCommand(42, showCommand: null);

        Assert.Null(new TaskbarWindowService(platform).CapturePrimary());
    }

    [Fact]
    public void TryHide_ReturnsFalseWhenHideDoesNotChangeVisibility()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.HideChangesVisibility = false;

        Assert.False(service.TryHide(snapshot));
        Assert.True(platform.IsWindowVisible(42));
    }

    [Fact]
    public void TryHide_ReturnsFalseWhenAnotherActorWonThePreCallRace()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.SetWindowVisible(42, visible: false);

        Assert.False(service.TryHide(snapshot));
        Assert.Contains("ShowWindow:42:0", platform.Mutations);
    }

    [Fact]
    public void TryHideDetailed_ReturnsAlreadyHiddenForExternalPreCallHide()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.SetWindowVisible(42, visible: false);

        Assert.Equal(TaskbarHideOutcome.AlreadyHidden, service.TryHideDetailed(snapshot));
        Assert.Equal(1, platform.HideCalls);
        Assert.False(service.TryHide(snapshot));
    }

    [Fact]
    public void TryHideDetailed_ReturnsNotHiddenWithoutCallingWhenIdentityPreconditionFails()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.SetClassName(42, "ReusedWindow");

        Assert.Equal(TaskbarHideOutcome.NotHidden, service.TryHideDetailed(snapshot));
        Assert.Equal(0, platform.HideCalls);
    }

    [Fact]
    public void TryHideDetailed_ReturnsIndeterminateWhenIdentityIsLostAfterPossibleHide()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.DestroyAfterHide = true;

        Assert.Equal(TaskbarHideOutcome.Indeterminate, service.TryHideDetailed(snapshot));
        Assert.False(service.TryHide(snapshot));
    }

    [Fact]
    public void TryRestoreDetailed_ReturnsAlreadyVisibleWithoutCallingShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;

        Assert.Equal(TaskbarRestoreOutcome.AlreadyVisible, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
        Assert.True(service.TryRestore(snapshot));
    }

    [Fact]
    public void TryRestoreDetailed_ReturnsStaleIdentityWithoutCallingShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.SetClassName(42, "ReusedWindow");

        Assert.Equal(TaskbarRestoreOutcome.StaleIdentity, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_TreatsUnavailableClassInspectionAsIndeterminateWithoutShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.ReturnNullClassName = true;

        Assert.Equal(TaskbarRestoreOutcome.Indeterminate, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_TreatsUnavailableStartInspectionAsIndeterminateWithoutShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.ReturnNullProcessStartTime = true;

        Assert.Equal(TaskbarRestoreOutcome.Indeterminate, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_TreatsZeroPidInspectionAsIndeterminateWithoutShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.ReturnZeroProcessId = true;

        Assert.Equal(TaskbarRestoreOutcome.Indeterminate, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_TreatsConcretePidMismatchAsStaleWithoutShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()! with { ProcessId = 999 };

        Assert.Equal(TaskbarRestoreOutcome.StaleIdentity, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_RevalidatesIdentityImmediatelyBeforeShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.AfterNextVisibilityRead = () => platform.SetClassName(42, "ReusedWindow");

        Assert.Equal(TaskbarRestoreOutcome.StaleIdentity, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_DoesNotShowWhenIdentityBecomesUnknownBeforeShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.AfterNextVisibilityRead = () => platform.ThrowOnClassNameInspection = true;

        Assert.Equal(TaskbarRestoreOutcome.Indeterminate, service.TryRestoreDetailed(snapshot));
        Assert.Equal(0, platform.ShowCalls);
    }

    [Fact]
    public void TryRestoreDetailed_ReturnsIndeterminateWhenIdentityIsLostAfterShow()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.InvalidateIdentityAfterShow = true;

        Assert.Equal(TaskbarRestoreOutcome.Indeterminate, service.TryRestoreDetailed(snapshot));
        Assert.False(service.TryRestore(snapshot));
    }

    [Fact]
    public void TryHide_ReturnsFalseWhenWindowDestroyedAfterHideCall()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.DestroyAfterHide = true;

        Assert.False(service.TryHide(snapshot));
        Assert.False(platform.IsWindow((nint)42));
    }

    [Fact]
    public void TryHide_ReturnsFalseWhenIdentityChangesAfterHideCall()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.InvalidateIdentityAfterHide = true;

        Assert.False(service.TryHide(snapshot));
        Assert.False(platform.GetWindowClassName((nint)42) == snapshot.ClassName);
    }

    [Fact]
    public void TryRestore_WhenSameWindowMovedMonitors_RestoresLeaseChange()
    {
        var harness = TaskbarWindowHarness.HiddenPrimaryThenMoved(handle: 42);

        Assert.True(harness.Service.TryRestore(harness.Snapshot));

        Assert.True(harness.Platform.IsWindowVisible((nint)42));
    }

    [Fact]
    public void TryRestore_UsesVisibilityPostconditionInsteadOfShowWindowPriorVisibility()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.SetWindowVisible(42, visible: false);

        Assert.True(service.TryRestore(snapshot));
        Assert.True(platform.IsWindowVisible(42));
    }

    [Fact]
    public void TryRestore_WithSwRestoreShowCommand_RestoresVisibility()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()! with
        {
            ShowCommand = NativeMethods.SW_RESTORE,
        };
        platform.SetWindowVisible(42, visible: false);

        Assert.True(service.TryRestore(snapshot));
        Assert.True(platform.IsWindowVisible(42));
    }

    [Fact]
    public void TryRestore_RejectsWindowAfterProcessRestart()
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()!;
        platform.SetWindowVisible(42, visible: false);
        platform.SetShowCommand(42, showCommand: NativeMethods.SW_SHOW);

        Assert.False(service.TryRestore(snapshot with { ProcessStartTimeUtcTicks = 5678 }));
        Assert.False(platform.IsWindowVisible(42));
    }
}
