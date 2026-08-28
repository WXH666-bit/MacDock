using MacDock.UI.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class DockItemReorderTests
{
    [Theory]
    [InlineData(0, 2, 3, 1)]
    [InlineData(2, 0, 3, 0)]
    [InlineData(1, 1, 3, 1)]
    [InlineData(1, 2, 3, 1)]
    [InlineData(1, 3, 3, 2)]
    [InlineData(0, -10, 3, 0)]
    [InlineData(0, 99, 3, 2)]
    public void GetDestinationIndex_ConvertsPreRemovalBoundary(
        int sourceIndex,
        int insertionIndex,
        int pinnedItemCount,
        int expected)
    {
        var actual = DockItemReorder.GetDestinationIndex(
            sourceIndex,
            insertionIndex,
            pinnedItemCount);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(3, 3)]
    [InlineData(0, 0)]
    public void GetDestinationIndex_InvalidSourceOrCountThrows(
        int sourceIndex,
        int pinnedItemCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DockItemReorder.GetDestinationIndex(
                sourceIndex,
                insertionIndex: 0,
                pinnedItemCount));
    }
}
