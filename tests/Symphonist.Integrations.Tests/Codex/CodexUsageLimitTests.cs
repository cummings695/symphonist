using Symphonist.Integrations.Codex;

namespace Symphonist.Integrations.Tests.Codex;

public class CodexUsageLimitTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    // 2026-10-28 10:00 in London (GMT: summer time ended on the 25th).
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset? ResetsAt(string text) => CodexUsageLimit.Detect(text, Now, London)!.ResetsAt;

    [Fact]
    public void Try_again_in_duration()
    {
        // Codex binary: "You've hit your usage limit. ... Try again in <duration>."
        Assert.Equal(Now.AddHours(2).AddMinutes(5),
            ResetsAt("You've hit your usage limit. Upgrade to Plus to continue using Codex (https://chatgpt.com/explore/plus). Try again in 2 hours 5 minutes."));
        Assert.Equal(Now.AddDays(5).AddHours(2), ResetsAt("You've hit your usage limit. Try again in 5 days 2 hours."));
    }

    [Fact]
    public void Try_again_at_time()
    {
        // Codex binary: "You've hit your usage limit. ... or try again at <time>."
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 4, 0, TimeSpan.Zero),
            ResetsAt("You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), or try again at 3:04 PM."));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.Zero),
            ResetsAt("You've hit your usage limit. Try again at 9:30 AM."));
    }

    [Fact]
    public void Try_again_at_dated_time()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 15, 4, 0, TimeSpan.Zero),
            ResetsAt("You've hit your usage limit. Try again at Nov 3rd, 2026 3:04 PM."));
    }

    [Theory]
    // Codex binary: "You've hit your usage limit." / "Usage limit reached. You've reached your usage limit. ..."
    [InlineData("You've hit your usage limit.")]
    [InlineData("You've hit your usage limit. To get more access now, send a request to your admin")]
    [InlineData("You've hit your usage limit. Visit https://chatgpt.com/settings/usage to purchase more credits")]
    [InlineData("Usage limit reached. You've reached your usage limit. Increase your limits to continue using codex.")]
    public void Usage_limit_without_time_has_no_reset(string text)
    {
        Assert.Null(ResetsAt(text));
    }

    [Fact]
    public void Too_many_requests_is_rate_limit()
    {
        // Codex binary: "exceeded retry limit, last status: " + the HTTP status
        Assert.Equal(Now.AddMinutes(5), ResetsAt("exceeded retry limit, last status: 429 Too Many Requests"));
    }

    [Fact]
    public void Stdout_trusts_only_strict_phrasings()
    {
        Assert.Null(CodexUsageLimit.DetectInOutput("tests cover the rate limit and the 429 path", Now, London));
        Assert.Equal(Now.AddMinutes(5),
            CodexUsageLimit.DetectInOutput("exceeded retry limit, last status: 429 Too Many Requests", Now, London)!.ResetsAt);
        Assert.Equal(Now.AddHours(1), CodexUsageLimit.DetectInOutput("You've hit your usage limit. Try again in 1 hour.", Now, London)!.ResetsAt);
    }

    [Theory]
    [InlineData("exceeded retry limit, last status: 503 Service Unavailable")]
    [InlineData("server overloaded")]
    [InlineData("agent exited with code 1")]
    public void Other_errors_are_not_limits(string text) => Assert.Null(CodexUsageLimit.Detect(text, Now, London));
}
