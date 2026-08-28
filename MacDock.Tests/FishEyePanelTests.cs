using System.Windows;
using System.Windows.Controls;
using System.Runtime.ExceptionServices;
using MacDock.UI.Controls;
using Xunit;

namespace MacDock.Tests;

public sealed class FishEyePanelTests
{
    [Fact]
    public void Layout_UsesVariableItemExtentsAndRunningGroupBreak()
    {
        RunInSta(() =>
        {
            var panel = new FishEyePanel
            {
                IconSize = 56,
                MaxScale = 1.6,
                Spacing = 8,
                GroupBreakIndex = 2,
            };
            AddChild(panel, 56);
            AddChild(panel, 18);
            AddChild(panel, 56);

            panel.Measure(new Size(1000, 200));
            panel.Arrange(new Rect(panel.DesiredSize));

            Assert.Equal(164, panel.StaticContentWidth, precision: 6);
            Assert.True(panel.GetInsertionX(0) < panel.GetInsertionX(1));
            Assert.True(panel.GetInsertionX(1) < panel.GetInsertionX(2));
            Assert.True(panel.GetInsertionX(2) < panel.GetInsertionX(3));
            Assert.Equal(0, panel.GetInsertionIndex(0, maximumIndex: 3));
            Assert.Equal(1, panel.GetInsertionIndex(80, maximumIndex: 3));
            Assert.Equal(2, panel.GetInsertionIndex(110, maximumIndex: 3));
            Assert.Equal(3, panel.GetInsertionIndex(500, maximumIndex: 3));
        });
    }

    [Fact]
    public void Layout_HidesRunningGroupBreakAtCollectionEdges()
    {
        RunInSta(() =>
        {
            var panel = new FishEyePanel
            {
                IconSize = 56,
                Spacing = 8,
                GroupBreakIndex = 2,
            };
            AddChild(panel, 56);
            AddChild(panel, 56);

            Assert.Equal(120, panel.StaticContentWidth, precision: 6);

            panel.GroupBreakIndex = 0;

            Assert.Equal(120, panel.StaticContentWidth, precision: 6);
        });
    }

    private static void AddChild(FishEyePanel panel, double extent)
    {
        var child = new Border();
        FishEyePanel.SetItemExtent(child, extent);
        panel.Children.Add(child);
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "STA layout test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
