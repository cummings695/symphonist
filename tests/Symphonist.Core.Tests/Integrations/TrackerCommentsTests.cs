using Symphonist.Core.Integrations;

namespace Symphonist.Core.Tests.Integrations;

public class TrackerCommentsTests
{
    [Fact] public void Sign_appends_footer_and_is_recognized()
    {
        var signed = TrackerComments.Sign("PR ready");
        Assert.Equal("PR ready\n\n— posted by Symphonist", signed);
        Assert.True(TrackerComments.IsSymphonist(signed));
        Assert.True(TrackerComments.IsSymphonist(signed + "\n  ")); // trackers may add trailing whitespace
    }

    [Fact] public void Developer_comments_are_not_symphonist()
    {
        Assert.False(TrackerComments.IsSymphonist("please use X"));
        Assert.False(TrackerComments.IsSymphonist("— posted by Symphonist, quoted, then my reply"));
    }
}
