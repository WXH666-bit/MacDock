using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class ShellMessageClassifierTests
{
    private const uint FixedTaskbarCreatedId = 0xC123;
    private const uint WmDisplayChange = 0x007E;

    [Fact]
    public void IsShellEnvironmentChange_FixedTaskbarCreatedId_ReturnsTrue()
    {
        var classifier = new ShellMessageClassifier(FixedTaskbarCreatedId);

        Assert.True(classifier.IsShellEnvironmentChange(FixedTaskbarCreatedId));
    }

    [Fact]
    public void IsShellEnvironmentChange_WmDisplayChange_ReturnsTrue()
    {
        var classifier = new ShellMessageClassifier(FixedTaskbarCreatedId);

        Assert.True(classifier.IsShellEnvironmentChange(WmDisplayChange));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x007Du)]
    [InlineData(0xC124u)]
    public void IsShellEnvironmentChange_ZeroOrUnrelatedId_ReturnsFalse(uint messageId)
    {
        var classifier = new ShellMessageClassifier(FixedTaskbarCreatedId);

        Assert.False(classifier.IsShellEnvironmentChange(messageId));
    }

    [Fact]
    public void Constructor_RejectsZeroRegisteredMessageId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShellMessageClassifier(0));
    }

    [Theory]
    [InlineData(0xBFFFu)]
    [InlineData(0x10000u)]
    public void Constructor_RejectsRegisteredMessageIdOutsideRegisterWindowRange(
        uint messageId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShellMessageClassifier(messageId));
    }

    [Fact]
    public void Constructor_RejectsRegisteredMessageIdThatConflictsWithDisplayChange()
    {
        Assert.Throws<ArgumentException>(
            () => new ShellMessageClassifier(WmDisplayChange));
    }
}
