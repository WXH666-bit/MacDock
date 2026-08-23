using MacDock.Core;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// 锁死 App 传给 WatchdogClient 的 appDataRoot 语义：
/// 必须是 %AppData% 本身（而非再深一层的 MacDock 目录），
/// 否则看门狗参数校验拼出的期望 journal 路径会多一级 MacDock，租约永远拿不到。
/// （v11 实测「隐藏任务栏一点就报错」的根因即在此。）
/// </summary>
public sealed class WatchdogClientAppPathTests
{
    [Fact]
    public void AppDataRoot_IsApplicationDataItself_NotMacDockSubdirectory()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(expected, AppPaths.AppDataRoot);
        Assert.NotEqual(AppPaths.AppDataDirectory, AppPaths.AppDataRoot);
    }

    [Fact]
    public void RealJournalPath_PassesWatchdogOptionsValidation()
    {
        // 用与 App.xaml.cs 相同的两处路径构造完整参数，TryParse 必须接受
        var appDataRoot = AppPaths.AppDataRoot;
        var args = new[]
        {
            "--parent-pid", "1234",
            "--parent-start-ticks", "638000000000000000",
            "--lease-id", "11111111-1111-1111-1111-111111111111",
            "--journal", AppPaths.TaskbarLeaseFile,
            "--ready-event", "Local\\MacDock.Taskbar.0123456789abcdef0123456789abcdef.ready",
            "--stop-event", "Local\\MacDock.Taskbar.0123456789abcdef0123456789abcdef.stop",
        };

        var parsed = TaskbarWatchdogOptions.TryParse(args, appDataRoot, out var options, out var error);

        Assert.True(parsed, $"真实 journal 路径被看门狗校验拒绝：{error}");
        Assert.NotNull(options);
        Assert.Equal(
            Path.GetFullPath(AppPaths.TaskbarLeaseFile),
            options!.JournalPath);
    }
}
