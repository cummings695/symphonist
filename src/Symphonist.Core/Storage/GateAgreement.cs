using Symphonist.Core.Risk;

namespace Symphonist.Core.Storage;

/// <summary>Joins gate reviews to gate decisions by round (ticket, stage, WaitingSince) and feeds <see cref="ReviewAgreement"/>.</summary>
public static class GateAgreement
{
    /// <summary>The latest decision of the round (by time; a later write wins a tie), or null while undecided.</summary>
    public static GateDecisionRecord? Latest(IEnumerable<GateDecisionRecord> decisions, Guid ticketId, string stageId, DateTimeOffset waitingSince)
    {
        GateDecisionRecord? latest = null;
        foreach (var d in decisions)
            if (d.TicketId == ticketId && d.StageId == stageId && d.WaitingSince == waitingSince && (latest is null || d.At >= latest.At))
                latest = d;
        return latest;
    }

    /// <summary>
    /// Both lists in write order. Each decided round counts once, by its latest decision: an auto decision is
    /// "auto-approved", a human one counts as decided only when its round has a review (the newest one).
    /// </summary>
    public static IReadOnlyList<StageAgreement> Compute(IEnumerable<GateReview> reviews, IEnumerable<GateDecisionRecord> decisions)
    {
        var allDecisions = decisions.ToList();
        var reviewList = reviews.ToList();
        var human = new List<DecidedReview>();
        var auto = new List<(string, string)>();
        foreach (var round in allDecisions.GroupBy(d => (d.TicketId, d.StageId, d.WaitingSince)))
        {
            var latest = Latest(round, round.Key.TicketId, round.Key.StageId, round.Key.WaitingSince)!;
            if (latest.Auto)
            {
                auto.Add((latest.ProjectId, latest.StageId));
                continue;
            }
            var review = reviewList.LastOrDefault(r => r.TicketId == latest.TicketId && r.StageId == latest.StageId
                && r.WaitingSince == latest.WaitingSince);
            if (review is not null)
                human.Add(new DecidedReview(latest.ProjectId, latest.StageId, review.Verdict.Risk, latest.Decision));
        }
        return ReviewAgreement.Compute(human, auto);
    }
}
