using Symphonist.Host.Dashboard;

namespace Symphonist.Host.Tests.Dashboard;

public sealed class DashboardChromeTests
{
    private readonly DashboardChrome chrome = new();

    [Fact]
    public void Error_shows_in_place_of_the_command_message()
    {
        chrome.ShowMessage("Poll requested");
        chrome.SetError(StatusSource.Board, "x");

        Assert.Equal("x", chrome.Message);
        Assert.True(chrome.IsError);

        chrome.SetError(StatusSource.Board, null);

        Assert.Equal("Poll requested", chrome.Message);
        Assert.False(chrome.IsError);
    }

    [Fact]
    public void Each_source_clears_only_its_own_error()
    {
        chrome.SetError(StatusSource.Shell, "shell");
        chrome.SetError(StatusSource.Ticket, "ticket");

        chrome.SetError(StatusSource.Ticket, null);

        Assert.Equal("shell", chrome.Message);
        Assert.True(chrome.IsError);
    }

    [Fact]
    public void Navigation_clears_page_state_but_not_shell_errors()
    {
        chrome.ShowMessage("msg");
        chrome.SetError(StatusSource.Board, "board");
        chrome.SetError(StatusSource.Shell, "shell");

        chrome.ClearForNavigation();

        Assert.Equal("shell", chrome.Message);

        chrome.SetError(StatusSource.Shell, null);
        chrome.ShowMessage("again");
        chrome.SetError(StatusSource.Ticket, "t");
        chrome.ClearForNavigation();

        Assert.Null(chrome.Message);
        Assert.False(chrome.IsError);
    }

    [Fact]
    public void Changes_raise_Changed()
    {
        var raised = 0;
        chrome.Changed += () => raised++;

        chrome.SetPage("SYM-1", "proj");
        chrome.ShowMessage("m");
        chrome.SetError(StatusSource.Board, "e");
        chrome.ClearForNavigation();

        Assert.Equal(4, raised);
        Assert.Equal("SYM-1", chrome.PageLabel);
        Assert.Equal("proj", chrome.CurrentProject);
    }
}
