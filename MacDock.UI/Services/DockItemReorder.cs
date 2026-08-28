namespace MacDock.UI.Services;

/// <summary>Dock 固定区重排的索引换算；插入边界按移除源项之前的集合计算。</summary>
internal static class DockItemReorder
{
    /// <summary>
    /// 把拖放插入边界换算为源项移除后的目标索引。
    /// </summary>
    public static int GetDestinationIndex(
        int sourceIndex,
        int insertionIndex,
        int pinnedItemCount)
    {
        if (pinnedItemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(pinnedItemCount));
        if (sourceIndex < 0 || sourceIndex >= pinnedItemCount)
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));

        var destination = Math.Clamp(insertionIndex, 0, pinnedItemCount);
        if (sourceIndex < destination)
            destination--;

        return Math.Clamp(destination, 0, pinnedItemCount - 1);
    }
}
