using Symphonist.Integrations.ClaudeCode;

namespace Symphonist.Integrations.Tests.ClaudeCode;

public class ClaudeUsageLimitTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    // 2026-10-28 10:00 in London (GMT: summer time ended on the 25th).
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset? ResetsAt(string text) => ClaudeUsageLimit.Detect(text, Now, London)!.ResetsAt;

    [Fact]
    public void Epoch_suffix_gives_reset()
    {
        // Claude Code: "Claude AI usage limit reached|<epoch seconds>"
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793210400), ResetsAt("Claude AI usage limit reached|1793210400"));
    }

    [Fact]
    public void Resets_time_today_or_tomorrow()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero), ResetsAt("Claude usage limit reached ∙ resets 3pm"));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), ResetsAt("Claude usage limit reached ∙ resets 9am"));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 30, 0, TimeSpan.Zero), ResetsAt("usage limit reached, resets 3:30 PM"));
    }

    [Fact]
    public void Resets_with_named_zone()
    {
        // 15:00 in New York on the 28th is 19:00 UTC (EDT).
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 19, 0, 0, TimeSpan.Zero),
            ResetsAt("Claude usage limit reached ∙ resets 3pm (America/New_York)"));
    }

    [Fact]
    public void Usage_limit_without_time_has_no_reset()
    {
        Assert.Null(ResetsAt("Claude usage limit reached"));
    }

    [Theory]
    // Claude Code 'weekly' lower-priority copy: "you have reached your weekly usage limit"
    [InlineData("you have reached your weekly usage limit")]
    // Claude Code's non-interactive reply line: "You've hit your limit · resets 3pm (Europe/London)"
    [InlineData("You've hit your limit · resets 3pm (Europe/London)")]
    // Claude Code credit message: "usage credit limit reached"
    [InlineData("usage credit limit reached")]
    // Gateway message: "spend limit reached (daily; resets 2026-08-08 00:00 UTC)"
    [InlineData("spend limit reached (daily; resets 2026-08-08 00:00 UTC) - request an increase")]
    public void Real_phrasings_are_usage_limits(string text)
    {
        Assert.NotNull(ClaudeUsageLimit.Detect(text, Now, London));
    }

    [Fact]
    public void Hit_your_limit_with_zone_gives_reset()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero),
            ResetsAt("You've hit your limit · resets 3pm (Europe/London)"));
    }

    [Fact]
    public void Rate_limit_resets_in_five_minutes()
    {
        Assert.Equal(Now.AddMinutes(5),
            ResetsAt("""API Error: 429 {"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""));
    }

    [Theory]
    [InlineData("""API Error: 529 {"type":"error","error":{"type":"overloaded_error"}}""")]
    [InlineData("API Error: overloaded")]
    public void Overloaded_is_not_a_limit(string text) => Assert.Null(ClaudeUsageLimit.Detect(text, Now, London));

    [Theory]
    [InlineData("API Error: 429 Too Many Requests")]
    [InlineData("429 Too Many Requests")]
    public void Real_429_wordings_are_rate_limits(string text) =>
        Assert.Equal(Now.AddMinutes(5), ResetsAt(text));

    [Fact]
    public void Bare_429_in_other_text_is_not_a_limit() =>
        Assert.Null(ClaudeUsageLimit.Detect("fetched 429 rows; added a rate limit to the endpoint", Now, London));

    [Fact]
    public void Unrelated_error_is_not_a_limit() =>
        Assert.Null(ClaudeUsageLimit.Detect("agent exited with code 1", Now, London));
}
