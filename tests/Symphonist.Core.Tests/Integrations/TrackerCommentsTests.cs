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

    [Theory]
    [InlineData("@claude please use v2", "please use v2")]
    [InlineData("  @Claude\nAnswers: yes", "Answers: yes")]
    [InlineData("@claude", "")]
    public void ClaudeRequest_strips_marker(string body, string expected) =>
        Assert.Equal(expected, TrackerComments.ClaudeRequest(body));

    [Theory]
    [InlineData("hey @claude")]
    [InlineData("@claudette")]
    [InlineData("note to Sam")]
    public void ClaudeRequest_ignores_other_comments(string body) => Assert.Null(TrackerComments.ClaudeRequest(body));

    [Theory]
    [InlineData("@claude fix it", @"\@claude fix it")]
    [InlineData("ask @Claude later\n> @CLAUDE again", "ask \\@Claude later\n> \\@CLAUDE again")]
    [InlineData(@"already \@claude", @"already \@claude")]
    [InlineData("no mention", "no mention")]
    public void EscapeMentions_escapes_every_claude_mention(string text, string expected) =>
        Assert.Equal(expected, TrackerComments.EscapeMentions(text));
}
