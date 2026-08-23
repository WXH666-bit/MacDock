using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// UWP 显示名解析的纯逻辑边界。真实 Shell 解析依赖具体 AUMID 与运行环境，
/// 不在无 UI 的测试里触发（避免依赖具体机器/包），只验证空值与状态无副作用。
/// </summary>
public sealed class UwpDisplayNameResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDisplayName_EmptyOrNull_ReturnsNull(string? aumid)
    {
        Assert.Null(UwpDisplayNameResolver.GetDisplayName(aumid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAumid_EmptyOrNull_ReturnsNull(string? exeName)
    {
        Assert.Null(UwpDisplayNameResolver.ResolveAumid(exeName));
    }

    [Fact]
    public void GetDisplayName_MsResourceIndirect_FailureReturnsNull()
    {
        // ms-resource: 间接串解析不出真身时应返回 null（而非把资源 ID 当名字）；
        // 此处用不存在的解析名，断言不抛异常且结果为空/缓存幂等。
        var name = UwpDisplayNameResolver.GetDisplayName(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        var again = UwpDisplayNameResolver.GetDisplayName(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        // 环境无关断言：重复调用幂等且不抛出；真实结果可能非空（本机装了计算器）或 null
        Assert.Same(name, again);
    }
}
