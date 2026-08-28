using MacDock.Core.Models;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

public sealed class DockItemViewModelTests
{
    [Fact]
    public void Separator_DisablesLaunchButKeepsManagementCommands()
    {
        var launched = 0;
        var removed = 0;
        var separator = new DockItemViewModel(
            new DockItem
            {
                Kind = DockItemKind.Separator,
                Name = "分隔线",
            },
            icon: null,
            isPinned: true,
            _ => launched++,
            _ => removed++,
            _ => { });

        Assert.True(separator.IsSeparator);
        Assert.False(separator.LaunchCommand.CanExecute(null));

        separator.LaunchCommand.Execute(null);
        separator.RemoveCommand.Execute(null);

        Assert.Equal(0, launched);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Application_AddSeparatorCommandUsesCurrentItem()
    {
        DockItemViewModel? requestedAfter = null;
        var application = new DockItemViewModel(
            new DockItem
            {
                Name = "应用",
                Path = @"C:\Apps\Sample.exe",
            },
            icon: null,
            isPinned: true,
            _ => { },
            _ => { },
            _ => { },
            item => requestedAfter = item);

        application.AddSeparatorAfterCommand.Execute(null);

        Assert.Same(application, requestedAfter);
    }
}
