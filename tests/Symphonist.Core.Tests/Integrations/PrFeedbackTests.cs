using Symphonist.Core.Integrations;

namespace Symphonist.Core.Tests.Integrations;

public class PrFeedbackTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-25T10:00:00Z");

    private static PullRequestComment C(string id, int minutes, string body, bool mine = true, string author = "me") =>
        new(id, author, mine, T0.AddMinutes(minutes), body);

    [Fact] public void Select_takes_tagged_unsigned_newer_mine()
    {
        var selected = PrFeedback.Select([
            C("1", 5, "@claude rename it"),
            C("2", -1, "@claude too old"),
            C("3", 6, "looks good"),
            C("4", 7, TrackerComments.Sign("@claude quoted by Symphonist")),
            C("5", 8, "@claude from Ann", mine: false, author: "ann"),
            C("6", 4, "@CLAUDE also tests"),
            C("7", 9, "@claudette no"),
        ], T0, fromOthers: false);

        Assert.Equal(["6", "1"], selected.Select(c => c.Id)); // oldest first
        Assert.Equal(["2", "1"], PrFeedback.Select([C("1", 5, "@claude a"), C("2", -1, "@claude b")], null, false)
            .Select(c => c.Id)); // no since: every one
    }

    [Fact] public void Select_with_from_others()
    {
        var selected = PrFeedback.Select([
            C("1", 5, "@claude from Ann", mine: false, author: "ann"),
            C("2", 6, "@claude mine"),
            C("3", 7, TrackerComments.Sign("@claude signed"), mine: false, author: "bot"),
        ], T0, fromOthers: true);

        Assert.Equal(["1", "2"], selected.Select(c => c.Id));
    }

    [Fact] public void Select_keeps_newest_twenty_oldest_first()
    {
        var comments = Enumerable.Range(1, 25).Reverse().Select(i => C($"{i}", i, $"@claude fix {i}")).ToList();

        var selected = PrFeedback.Select(comments, T0, false);

        Assert.Equal(PrFeedback.MaxComments, selected.Count);
        Assert.Equal(Enumerable.Range(6, 20).Select(i => $"{i}"), selected.Select(c => c.Id));
    }

    [Fact] public void From_strips_marker_and_bounds()
    {
        var inline = new PullRequestComment("review:9", "me", true, T0, "  @claude  use a const here ", "src/A.cs", 12, "review:8");

        var f = PrFeedback.From(inline);

        Assert.Equal(new PrFeedbackComment("review:9", "me", true, "use a const here", "src/A.cs", 12, "review:8"), f);
        Assert.Equal("", PrFeedback.From(C("1", 0, "@claude")).Request);
        Assert.Equal(PrFeedback.MaxRequestChars,
            PrFeedback.From(C("2", 0, "@claude " + new string('x', 10_000))).Request.Length);
    }

    [Fact] public void Json_round_trip_and_invalid_is_empty()
    {
        IReadOnlyList<PrFeedbackComment> comments =
        [
            new("issue:1", "me", true, "rename it", null, null, null),
            new("review:2", "ann", false, "", "src/B.cs", 3, "review:1"),
        ];

        Assert.Equal(comments, PrFeedback.FromJson(PrFeedback.ToJson(comments)));
        Assert.Empty(PrFeedback.FromJson(null));
        Assert.Empty(PrFeedback.FromJson(""));
        Assert.Empty(PrFeedback.FromJson("not json"));
        Assert.Empty(PrFeedback.FromJson("{\"a\":1}"));
        Assert.Empty(PrFeedback.FromJson("null"));
    }
}
