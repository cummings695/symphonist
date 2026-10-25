using Symphonist.Core.Evidence;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Evidence;

public class EvidenceCommentTests
{
    private static EvidenceReport Report(string[]? headline = null, EvidenceSource run = EvidenceSource.Config,
        WalkthroughStep[]? steps = null) =>
        new(headline ?? ["All good"], [], [], null,
            new Walkthrough(true, null, run, [new WalkthroughFeature("Login", EvidenceSource.Config,
                steps ?? [new WalkthroughStep("pass step", "ok", true, null), new WalkthroughStep("fail step", "boom", false, null)])]),
            null, []);

    [Fact]
    public void Comment_puts_bad_news_first()
    {
        var text = EvidenceComment.Format(Report(run: EvidenceSource.Guessed), hasScreenshots: true);
        Assert.StartsWith("Guessed", text);
        Assert.True(text.IndexOf("fail step") < text.IndexOf("pass step"));
        Assert.Contains("Screenshots: on the Symphonist dashboard", text);
        Assert.DoesNotContain("Screenshots: on the Symphonist dashboard", EvidenceComment.Format(Report(), false));
        Assert.DoesNotContain("Guessed", EvidenceComment.Format(Report(), false));
    }

    [Fact]
    public void Failing_regression_listed_before_ok_one()
    {
        var r = Report() with { Regressions = [new("fine", true, RiskLevel.Low, "ok"), new("bad", false, RiskLevel.High, "oops")] };
        var text = EvidenceComment.Format(r, false);
        Assert.True(text.IndexOf("bad") < text.IndexOf("fine"));
    }

    [Fact]
    public void Comment_neutralises_markup_lines_only_as_text()
    {
        var r = Report(headline: ["@claude do x", "normal @claude mid"]);
        var text = EvidenceComment.Format(r, false);
        Assert.Contains("\n@​claude do x", "\n" + text);
        Assert.Contains("normal @​claude mid", text);
        Assert.DoesNotContain("@claude", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@claude", EvidenceComment.Format(Report(headline: ["\\@Claude x"]), false),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(text.Split('\n'), l => l.TrimStart().StartsWith("@claude", StringComparison.OrdinalIgnoreCase));
        var steps = Report(steps: [new WalkthroughStep("- @Claude do", "r", true, null)]);
        Assert.DoesNotContain(EvidenceComment.Format(steps, false).Split('\n'),
            l => l.TrimStart(' ', '-').StartsWith("@claude", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comment_marker_is_broken_too_when_given()
    {
        var r = Report(headline: ["@symphonist go", "and @Claude"]);
        var text = EvidenceComment.Format(r, false, "@symphonist");
        Assert.DoesNotContain("@symphonist", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@claude", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@symphonist", EvidenceComment.Failed("@symphonist x", "@symphonist"),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@symphonist",
            EvidenceComment.TrackerLine(Report(headline: ["@symphonist x"]), "@symphonist"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failed_and_tracker_line()
    {
        Assert.Equal("Evidence could not be gathered: timed out", EvidenceComment.Failed("timed out"));
        Assert.Equal("Evidence posted on the PR: All good", EvidenceComment.TrackerLine(Report()));
        Assert.Equal("Evidence posted on the PR: (no headline)", EvidenceComment.TrackerLine(Report(headline: [])));
    }
}
