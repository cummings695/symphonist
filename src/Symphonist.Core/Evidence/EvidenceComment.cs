using System.Text;
using System.Text.RegularExpressions;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Evidence;

public static partial class EvidenceComment
{
    [GeneratedRegex(@"^(\s*(?:[-*+>]\s+)*)@claude", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RequestLine();

    /// <summary>
    /// PR comment (unsigned; the caller signs it): a guessed warning first, the headline, then Regressions, Tests and
    /// coverage and Walkthrough, failures first in each. All agent text is bounded, and no line can begin with
    /// "@claude" (it would read as a request).
    /// </summary>
    public static string Format(EvidenceReport report, bool hasScreenshots)
    {
        var sb = new StringBuilder();
        if (report.Guessed)
            sb.AppendLine("Guessed: the agent guessed how to run the app or which features to check, rather than using the project's configuration. Read the walkthrough with that in mind.").AppendLine();
        foreach (var h in report.Headline) sb.AppendLine(Bound(h));
        if (report.Headline.Count > 0) sb.AppendLine();

        sb.AppendLine("### Regressions");
        if (report.Regressions.Count == 0) sb.AppendLine("None reported.");
        foreach (var r in report.Regressions.OrderBy(r => !r.Intended && r.Risk == RiskLevel.High ? 0 : 1))
        {
            var tag = !r.Intended && r.Risk == RiskLevel.High ? "Unintended, high risk"
                : r.Intended ? "Intended" : "Unintended, low risk";
            sb.AppendLine($"- {tag}: {Bound(r.Area)}: {Bound(r.Reason)}");
        }

        sb.AppendLine().AppendLine("### Tests and coverage");
        if (report.Tests is { } t)
            sb.AppendLine($"- Tests {(t.Passed ? "passed" : "failed")}: {Bound(t.Command)}: {Bound(t.Summary)}");
        else sb.AppendLine("- Tests: not reported");
        foreach (var c in report.Coverage.OrderBy(c => c.Gap.Length > 0 ? 0 : 1))
        {
            var tests = c.Tests.Count > 0 ? string.Join(", ", c.Tests.Select(Bound)) : "no tests named";
            sb.AppendLine(c.Gap.Length > 0
                ? $"- Gap in {Bound(c.Area)}: {Bound(c.Gap)} (tests: {tests})"
                : $"- Covered {Bound(c.Area)}: {tests}");
        }

        sb.AppendLine().AppendLine("### Walkthrough");
        var w = report.Walkthrough;
        if (!w.Ran) sb.AppendLine($"Not run: {Bound(w.WhyNot ?? "(no reason given)")}");
        foreach (var f in w.Features.OrderBy(f => f.Steps.Any(s => !s.Passed) ? 0 : 1))
        {
            sb.AppendLine($"{Bound(f.Name)} ({f.Source.ToString().ToLowerInvariant()})");
            foreach (var s in f.Steps.OrderBy(s => s.Passed ? 1 : 0))
                sb.AppendLine($"- {(s.Passed ? "Passed" : "Failed")}: {Bound(s.Action)} -> {Bound(s.Result)}");
        }
        if (hasScreenshots) sb.AppendLine().AppendLine("Screenshots: on the Symphonist dashboard");

        return Neutralise(sb.ToString().TrimEnd());
    }

    public static string Failed(string reason) =>
        Neutralise($"Evidence could not be gathered: {ReviewerText.Line(reason, ReviewerText.MaxSummaryChars)}");

    public static string TrackerLine(EvidenceReport report) =>
        Neutralise("Evidence posted on the PR: "
            + (report.Headline.Count > 0 ? ReviewerText.Line(report.Headline[0], ReviewerText.MaxSummaryChars) : "(no headline)"));

    private static string Bound(string text) => ReviewerText.Line(text, ReviewerText.MaxItemChars);

    private static string Neutralise(string text) => RequestLine().Replace(text, @"$1\@claude");
}
