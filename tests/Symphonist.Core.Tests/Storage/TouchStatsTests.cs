using Symphonist.Core.Config;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Core.Tests.Storage;

public class TouchStatsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");
    // Wednesday 2026-10-28 12:00 local; its week started Monday 2026-10-26.
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly Guid Ticket1 = Guid.NewGuid();

    private static DateTimeOffset Local(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, TimeSpan.FromHours(2));

    private static GateReview Review(DateTimeOffset since, RiskLevel risk, Guid? ticket = null, string stage = "implement") =>
        new(ticket ?? Ticket1, "p", stage, since, since, ReviewMode.Shadow, GatePolicy.Human,
            new GateVerdict(risk, "s", [], [], []));

    private static GateDecisionRecord Decision(DateTimeOffset since, GateDecision d, DateTimeOffset at, bool auto = false,
        Guid? ticket = null, string stage = "implement") =>
        new(ticket ?? Ticket1, "p", stage, since, d, auto, at);

    private static TouchRecord Touch(TouchKind kind, DateTimeOffset at, string stage = "implement") =>
        new(Ticket1, "p", stage, kind, at);

    [Fact]
    public void Week_starts_monday_local()
    {
        var sundayLate = new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.FromHours(2));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)), TouchStats.WeekStart(sundayLate, Zone));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(2)), TouchStats.WeekStart(Now, Zone));
        // 22:30 UTC on Sunday is already Monday 00:30 in this zone.
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(2)),
            TouchStats.WeekStart(new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.Zero), Zone));
    }

    [Fact]
    public void Each_round_counts_by_its_latest_decision()
    {
        var since = Local(10, 27, 9);
        var stage = Assert.Single(TouchStats.Compute([],
            [Decision(since, GateDecision.Approved, Local(10, 27, 10)), Decision(since, GateDecision.SentBack, Local(10, 27, 11))],
            [Review(since, RiskLevel.High)], Now, Zone));

        Assert.Equal(0, stage.ApprovedByYou);
        Assert.Equal(0, stage.Overrules); // sent back a high: agreed with the reviewer
        Assert.Equal(0, stage.WeeklyTouches.Sum()); // the send-back touch itself is a TouchRecord
    }

    [Fact]
    public void Approving_a_high_is_a_touch_and_an_overrule()
    {
        var since = Local(10, 27, 9);
        var stage = Assert.Single(TouchStats.Compute([], [Decision(since, GateDecision.Approved, Local(10, 27, 10))],
            [Review(since, RiskLevel.High)], Now, Zone));

        Assert.Equal((1, 1, 0, 0), (stage.Overrules, stage.ApprovedByYou, stage.SendBacks, stage.AutoApproved));
        Assert.Equal(1, stage.WeeklyTouches[^1]);
    }

    [Fact]
    public void Sending_back_a_low_is_one_touch_and_an_overrule()
    {
        var since = Local(10, 27, 9);
        var at = Local(10, 27, 10);
        var stage = Assert.Single(TouchStats.Compute([Touch(TouchKind.SendBack, at)],
            [Decision(since, GateDecision.SentBack, at)], [Review(since, RiskLevel.Low)], Now, Zone));

        Assert.Equal((1, 1), (stage.SendBacks, stage.Overrules));
        Assert.Equal(1, stage.WeeklyTouches[^1]); // not 2
    }

    [Fact]
    public void Auto_approvals_are_not_touches()
    {
        var since = Local(10, 27, 9);
        var stage = Assert.Single(TouchStats.Compute([],
            [Decision(since, GateDecision.Approved, Local(10, 27, 10), auto: true)], [Review(since, RiskLevel.Low)], Now, Zone));

        Assert.Equal((1, 0, 0), (stage.AutoApproved, stage.ApprovedByYou, stage.Overrules));
        Assert.Equal(0, stage.WeeklyTouches.Sum());
    }

    [Fact]
    public void Weekly_buckets_cover_eight_weeks_oldest_first()
    {
        var touches = new[]
        {
            Touch(TouchKind.Unblock, Local(10, 28)),             // this week
            Touch(TouchKind.SendBack, Local(10, 26, 0)),         // Monday 00:00: this week
            Touch(TouchKind.SendBack, Local(10, 25, 23)),        // last week (Sunday)
            Touch(TouchKind.SendBack, Local(9, 7, 8)),           // Monday of the oldest week (7 weeks before 10-26)
            Touch(TouchKind.SendBack, Local(9, 6, 23)),          // before the window
        };
        var stage = Assert.Single(TouchStats.Compute(touches, [], [], Now, Zone));

        Assert.Equal([1, 0, 0, 0, 0, 0, 1, 2], stage.WeeklyTouches);
        Assert.Equal((3, 1), (stage.SendBacks, stage.Unblocks));
    }

    [Fact]
    public void Summary_compares_this_week_with_last()
    {
        var since = Local(10, 27, 9);
        var other = Guid.NewGuid();
        var touches = new[]
        {
            Touch(TouchKind.SendBack, Local(10, 27)), Touch(TouchKind.Unblock, Local(10, 27)),
            Touch(TouchKind.SendBack, Local(10, 20)), Touch(TouchKind.SendBack, Local(10, 21)),
        };
        var decisions = new[]
        {
            Decision(since, GateDecision.Approved, Local(10, 27, 10)),                            // high approved: overrule
            Decision(since, GateDecision.Approved, Local(10, 27, 10), auto: true, ticket: other), // auto
            Decision(Local(10, 20, 9), GateDecision.Approved, Local(10, 20, 10), auto: true),     // last week
        };
        var reviews = new[] { Review(since, RiskLevel.High), Review(since, RiskLevel.Low, other) };

        var stages = TouchStats.Compute(touches, decisions, reviews, Now, Zone);
        var summary = TouchStats.Summarize(stages, touches, decisions, reviews, Now, Zone);

        Assert.Equal(new TouchSummary(ThisWeek: 3, LastWeek: 2, SendBacks: 1, Unblocks: 1, Overrules: 1, AutoApproved: 1, Decided: 2), summary);
    }
}
