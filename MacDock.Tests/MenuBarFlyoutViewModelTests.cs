using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// 浮窗 VM 逻辑：音量/亮度切换、系统值回灌不触发写回调、拖动防抖、四态图标映射。
/// </summary>
public sealed class MenuBarFlyoutViewModelTests
{
    [Fact]
    public void ShowVolume_ConfiguresTitleValueMuteAndIcon()
    {
        var vm = new MenuBarFlyoutViewModel(null, null);

        vm.ShowVolume(50, muted: false);

        Assert.Equal("音量", vm.Title);
        Assert.Equal(50, vm.Value);
        Assert.True(vm.ShowMuteButton);
        Assert.False(vm.IsMuted);
        Assert.Equal("speaker_2", vm.IconState);
        Assert.True(vm.IsVolumeMode);
    }

    [Fact]
    public void ShowBrightness_NoMuteButton()
    {
        var vm = new MenuBarFlyoutViewModel(null, null);

        vm.ShowBrightness(60);

        Assert.Equal("亮度", vm.Title);
        Assert.Equal(60, vm.Value);
        Assert.False(vm.ShowMuteButton);
        Assert.False(vm.IsVolumeMode);
        Assert.Equal("sun", vm.IconState);
    }

    [Fact]
    public void SystemValueBackfill_DoesNotTriggerWriteCallback()
    {
        var writeCount = 0;
        var vm = new MenuBarFlyoutViewModel(_ => writeCount++, null);
        vm.ShowVolume(30, muted: false);

        vm.SetValueFromSystem(80, muted: true, iconState: "speaker_3");

        Assert.Equal(80, vm.Value);
        Assert.True(vm.IsMuted);
        Assert.Equal(0, writeCount);
    }

    [Fact]
    public void UserValueChange_TriggersWriteCallback()
    {
        double? written = null;
        var vm = new MenuBarFlyoutViewModel(v => written = v, null);
        vm.ShowVolume(30, muted: false);

        // 直接设置 Value 走生成的 OnValueChanged 勾子（用户拖动同一路径）
        vm.Value = 45;

        Assert.Equal(45, written);
    }

    [Fact]
    public void DuringDrag_SystemBackfillIsIgnored()
    {
        double? written = null;
        var vm = new MenuBarFlyoutViewModel(v => written = v, null);
        vm.ShowVolume(30, muted: false);

        vm.BeginUserInput();
        vm.SetValueFromSystem(90, muted: false, iconState: "speaker_3");
        vm.EndUserInput();

        Assert.Equal(30, vm.Value);
        Assert.Null(written); // 拖动中不被外部回灌覆盖，未触发写回调
    }

    [Fact]
    public void ToggleMute_InvokesCallback()
    {
        var mutedToggles = 0;
        var vm = new MenuBarFlyoutViewModel(null, () => mutedToggles++);

        vm.ToggleMuteCommand.Execute(null);

        Assert.Equal(1, mutedToggles);
    }

    [Theory]
    [InlineData(100, false, "speaker_3")]
    [InlineData(67, false, "speaker_3")]
    [InlineData(66, false, "speaker_2")]
    [InlineData(34, false, "speaker_2")]
    [InlineData(33, false, "speaker_1")]
    [InlineData(1, false, "speaker_1")]
    [InlineData(0, false, "speaker_0")]
    [InlineData(0, true, "speaker_0")]
    [InlineData(80, true, "speaker_0")]
    public void VolumeIconState_BucketsCorrectly(double volume, bool muted, string expected)
    {
        Assert.Equal(expected, MenuBarFlyoutViewModel.VolumeIconState(volume, muted));
    }
}
