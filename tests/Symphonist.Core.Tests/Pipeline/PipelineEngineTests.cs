using Symphonist.Integrations;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Core.Tests.Pipeline;

public class PipelineEngineTests
{
    private static readonly ProjectConfig P =
        ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.yaml"), BuiltInProviders.Registry);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T10:00Z");

    private static Ticket At(string stageId, StageStatus status = StageStatus.Pending) =>
        new() { Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", StageId = stageId, Status = status };

    private static Ticket Awaiting(string stageId) => At(stageId, StageStatus.AwaitingApproval);

    [Fact] public void Create_starts_at_stage_matching_source_status()
    {
        Assert.Equal("plan", PipelineEngine.Create(P, "ORD-1", "t", "planned", Now).StageId);
        Assert.Equal("triage", PipelineEngine.Create(P, "ORD-2", "t", "Selected", Now).StageId);
    }

    [Theory]
    [InlineData(StageStatus.AwaitingApproval)]
    [InlineData(StageStatus.Blocked)]
    [InlineData(StageStatus.Approved)]
    public void Ticket_at_a_removed_stage_names_the_way_out(StageStatus status)
    {
        const string message = "stage 'qa' is no longer in the pipeline; send it back to an existing stage (--to)";
        var t = At("qa", status);

        var approve = PipelineEngine.Approve(P, t, Now);
        var sendBack = PipelineEngine.SendBack(P, t, "fix", null, Now);

        Assert.False(approve.Changed);
        Assert.Equal(message, approve.Message);
        Assert.False(sendBack.Changed);
        Assert.Equal(message, sendBack.Message);
        if (status == StageStatus.Approved)
            Assert.Equal(message, PipelineEngine.PostApprovalCompleted(P, t, Now).Message);
        Assert.True(PipelineEngine.SendBack(P, t, "fix", "plan", Now).Changed);
        Assert.Equal("plan", t.StageId);
    }

    [Fact] public void Create_sets_initial_fields()
    {
        var t = PipelineEngine.Create(P, "ORD-1", "t", "Backlog", Now);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Equal("Backlog", t.LastSourceStatus);
        Assert.Equal("symphonist/ORD-1", t.Branch);
        Assert.Equal("orders", t.ProjectId);
    }

    [Fact] public void Human_gate_waits_and_sets_gate_status()
    {
        var t = At("implement"); var o = PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal(StageStatus.AwaitingApproval, t.Status); Assert.Equal("In Review", o.SourceStatusToSet); Assert.Equal(Now, t.WaitingSince);
    }

    [Fact] public void Gate_round_key_is_truncated_to_whole_milliseconds()
    {
        var t = At("implement");
        PipelineEngine.StageSucceeded(P, t, Now.AddTicks(1234567 % TimeSpan.TicksPerMillisecond + 5));
        Assert.Equal(0, t.WaitingSince!.Value.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(Now, t.WaitingSince);
    }

    [Fact] public void No_gate_advances_and_sets_enter_status()
    {
        var t = At("deploy"); var o = PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal("done", t.StageId); Assert.Equal(StageStatus.Done, t.Status); Assert.Equal("Done", o.SourceStatusToSet);
    }

    [Fact] public void StageSucceeded_clears_feedback_and_sets_updated_at()
    {
        var t = At("plan"); t.Feedback = "old";
        var o = PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Null(t.Feedback); Assert.True(o.Changed); Assert.NotEmpty(o.Message); Assert.Equal(Now, t.UpdatedAt);
    }

    [Fact] public void Entering_stage_resets_attempts_and_stamps_start()
    {
        var t = At("build"); t.Attempts = 2;
        PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal("release", t.StageId); Assert.Equal(0, t.Attempts); Assert.Equal(Now, t.StageStartedAt);
    }

    [Fact] public void Failure_blocks_after_max_attempts()
    {
        var t = At("plan"); PipelineEngine.StageFailed(P, t, "boom", Now); PipelineEngine.StageFailed(P, t, "boom", Now);
        Assert.Equal(StageStatus.Pending, t.Status);
        PipelineEngine.StageFailed(P, t, "boom", Now);
        Assert.Equal(StageStatus.Blocked, t.Status); Assert.Equal("plan failed 3 times: boom", t.BlockedReason);
    }

    [Fact] public void Usage_limit_returns_to_pending_without_an_attempt()
    {
        var t = At("plan", StageStatus.Running); t.Attempts = 2;
        var o = PipelineEngine.StageLimited(t, "plan paused: Claude usage limit", Now);
        Assert.Equal((StageStatus.Pending, 2, Now), (t.Status, t.Attempts, t.UpdatedAt));
        Assert.Equal(new TransitionOutcome(true, null, "plan paused: Claude usage limit"), o);
    }

    [Fact] public void Approve_implement_goes_to_Approved_then_build()
    {
        var t = Awaiting("implement"); PipelineEngine.Approve(P, t, Now); Assert.Equal(StageStatus.Approved, t.Status);
        PipelineEngine.PostApprovalCompleted(P, t, Now); Assert.Equal("build", t.StageId); Assert.Equal(StageStatus.Pending, t.Status);
    }

    [Fact] public void Approve_into_Approved_resets_attempts_for_the_merge()
    {
        var t = Awaiting("implement"); t.Attempts = 2;
        PipelineEngine.Approve(P, t, Now);
        Assert.Equal((StageStatus.Approved, 0), (t.Status, t.Attempts));
    }

    [Fact] public void Approve_plan_advances_directly()
    {
        var t = Awaiting("plan"); var o = PipelineEngine.Approve(P, t, Now);
        Assert.Equal("implement", t.StageId); Assert.Equal("In Progress", o.SourceStatusToSet);
    }

    [Fact] public void Approve_ignored_unless_awaiting() { var t = At("plan"); Assert.False(PipelineEngine.Approve(P, t, Now).Changed); }

    [Fact] public void PostApprovalCompleted_ignored_unless_approved()
    {
        var t = Awaiting("implement"); Assert.False(PipelineEngine.PostApprovalCompleted(P, t, Now).Changed); Assert.Equal("implement", t.StageId);
    }

    [Fact] public void HasPostApprovalAction_only_implement_and_release()
    {
        foreach (var kind in Enum.GetValues<StageKind>())
            Assert.Equal(kind is StageKind.Implement or StageKind.Release, PipelineEngine.HasPostApprovalAction(kind));
    }

    [Fact] public void Source_status_approves_gate()
    {
        var t = Awaiting("plan"); PipelineEngine.SourceStatusChanged(P, t, "In Progress", null, Now); Assert.Equal("implement", t.StageId);
    }

    [Fact] public void Source_status_compare_is_case_insensitive()
    {
        var t = Awaiting("plan"); PipelineEngine.SourceStatusChanged(P, t, "in progress", null, Now); Assert.Equal("implement", t.StageId);
    }

    [Fact] public void Source_status_moving_back_sends_back_with_feedback()
    {
        var t = Awaiting("plan"); PipelineEngine.SourceStatusChanged(P, t, "Backlog", "needs caching", Now);
        Assert.Equal("triage", t.StageId); Assert.Equal("needs caching", t.Feedback); Assert.Equal(StageStatus.Pending, t.Status);
    }

    [Fact] public void Source_status_matching_enter_status_of_earlier_stage_sends_back_to_latest_match()
    {
        // "Planned" is both plan's entry/enter status and triage's approvedWhen; plan is earlier than implement.
        var t = Awaiting("implement"); t.LastSourceStatus = "In Review";
        var o = PipelineEngine.SourceStatusChanged(P, t, "Planned", "redo", Now);
        Assert.True(o.Changed); Assert.Equal("plan", t.StageId); Assert.Equal("redo", t.Feedback);
    }

    [Fact] public void Unrelated_source_status_is_recorded_but_no_change()
    {
        var t = Awaiting("plan"); var o = PipelineEngine.SourceStatusChanged(P, t, "Code Review", null, Now);
        Assert.False(o.Changed); Assert.Equal("Code Review", t.LastSourceStatus);
    }

    [Fact] public void Repeated_source_status_is_no_change()
    {
        var t = Awaiting("plan"); t.LastSourceStatus = "In Progress";
        Assert.False(PipelineEngine.SourceStatusChanged(P, t, "in progress", null, Now).Changed);
        Assert.Equal("plan", t.StageId);
    }

    [Theory]
    [InlineData(StageStatus.AwaitingApproval)]
    [InlineData(StageStatus.Approved)]
    [InlineData(StageStatus.Blocked)]
    public void SendBack_allowed_from_gate_approved_and_blocked(StageStatus from)
    {
        var t = At("implement", from); t.Attempts = 2; t.BlockedReason = "x";
        var o = PipelineEngine.SendBack(P, t, "fix it", null, Now);
        Assert.True(o.Changed); Assert.NotEmpty(o.Message);
        Assert.Equal("implement", t.StageId); Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Equal(0, t.Attempts); Assert.Equal("fix it", t.Feedback); Assert.Equal("In Progress", o.SourceStatusToSet);
        Assert.Equal(Now, t.UpdatedAt);
    }

    [Theory]
    [InlineData(StageStatus.Pending)]
    [InlineData(StageStatus.Running)]
    [InlineData(StageStatus.Done)]
    public void SendBack_rejected_from_other_statuses(StageStatus from)
    {
        var t = At("implement", from);
        Assert.False(PipelineEngine.SendBack(P, t, "fix", null, Now).Changed);
        Assert.Equal(from, t.Status);
    }

    [Fact] public void SendBack_to_explicit_stage()
    {
        var t = Awaiting("implement");
        var o = PipelineEngine.SendBack(P, t, "replan", "plan", Now);
        Assert.Equal("plan", t.StageId); Assert.Equal("Planned", o.SourceStatusToSet);
    }

    [Fact] public void Block_sets_status_and_reason()
    {
        var t = At("build"); var o = PipelineEngine.Block(t, "no longer matches", Now);
        Assert.True(o.Changed); Assert.Equal(StageStatus.Blocked, t.Status); Assert.Equal("no longer matches", t.BlockedReason);
        Assert.Equal(Now, t.UpdatedAt);
    }

    [Fact] public void Unblock_resets_to_pending()
    {
        var t = At("build", StageStatus.Blocked); t.Attempts = 3; t.BlockedReason = "x";
        var o = PipelineEngine.Unblock(t, Now);
        Assert.True(o.Changed); Assert.Equal(StageStatus.Pending, t.Status); Assert.Equal(0, t.Attempts); Assert.Null(t.BlockedReason);
        Assert.Equal(Now, t.UpdatedAt);
    }

    [Fact] public void Unblock_restarts_the_stage_clock()
    {
        var t = At("build", StageStatus.Blocked); t.StageStartedAt = Now.AddDays(-1);
        PipelineEngine.Unblock(t, Now);
        Assert.Equal(Now, t.StageStartedAt);
    }

    [Fact] public void Changed_transitions_set_UpdatedAt()
    {
        var later = Now.AddHours(1);
        var t = At("plan"); PipelineEngine.StageFailed(P, t, "e", later); Assert.Equal(later, t.UpdatedAt);
        var a = Awaiting("plan"); PipelineEngine.Approve(P, a, later); Assert.Equal(later, a.UpdatedAt);
    }
    [Fact] public void Moving_back_to_current_stage_enter_status_at_gate_sends_back_to_current_stage()
    {
        var t = Awaiting("implement"); t.LastSourceStatus = "In Review";
        var o = PipelineEngine.SourceStatusChanged(P, t, "In Progress", "fix the tests", Now);
        Assert.True(o.Changed); Assert.Equal(("implement", StageStatus.Pending), (t.StageId, t.Status));
        Assert.Equal("fix the tests", t.Feedback); Assert.Equal("sent back to implement", o.Message);
    }

    [Theory]
    [InlineData(StageStatus.Pending)]
    [InlineData(StageStatus.Running)]
    public void Approval_move_while_stage_runs_is_deferred_until_it_succeeds(StageStatus status)
    {
        var t = At("plan", status); t.LastSourceStatus = "Planned";
        var o = PipelineEngine.SourceStatusChanged(P, t, "In Progress", null, Now);
        Assert.True(o.Changed); Assert.Null(o.SourceStatusToSet);
        Assert.Equal($"source moved to 'In Progress' while plan is {status}; will apply when the stage finishes", o.Message);
        Assert.Equal(("plan", status, "In Progress"), (t.StageId, t.Status, t.PendingSourceStatus));

        var done = PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal(("implement", StageStatus.Pending), (t.StageId, t.Status));
        Assert.Null(t.PendingSourceStatus);
        Assert.Equal("applying source move to 'In Progress': advanced to implement", done.Message);
        Assert.Equal("In Progress", done.SourceStatusToSet);
    }

    [Fact] public void Backward_move_while_stage_runs_sends_back_when_it_succeeds_with_latest_feedback()
    {
        var t = At("implement", StageStatus.Running); t.LastSourceStatus = "In Progress";
        PipelineEngine.SourceStatusChanged(P, t, "Planned", "too early", Now);
        Assert.Equal(("implement", StageStatus.Running, "Planned"), (t.StageId, t.Status, t.PendingSourceStatus));

        var o = PipelineEngine.StageSucceeded(P, t, Now, "rethink the plan");
        Assert.Equal(("plan", StageStatus.Pending, "rethink the plan"), (t.StageId, t.Status, t.Feedback));
        Assert.Null(t.PendingSourceStatus);
        Assert.Equal("applying source move to 'Planned': sent back to plan", o.Message);
    }

    [Fact] public void Backward_move_while_gateless_stage_runs_sends_back_when_it_succeeds()
    {
        var t = At("build", StageStatus.Running); t.LastSourceStatus = "In Review";
        PipelineEngine.SourceStatusChanged(P, t, "In Progress", null, Now);
        PipelineEngine.StageSucceeded(P, t, Now, "fix it");
        Assert.Equal(("implement", StageStatus.Pending, "fix it"), (t.StageId, t.Status, t.Feedback));
    }

    [Fact] public void Unrelated_move_while_stage_runs_drops_the_deferred_decision()
    {
        var t = At("plan", StageStatus.Running); t.LastSourceStatus = "Planned";
        PipelineEngine.SourceStatusChanged(P, t, "In Progress", null, Now);
        var o = PipelineEngine.SourceStatusChanged(P, t, "Code Review", null, Now);
        Assert.Null(t.PendingSourceStatus);
        PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
    }

    [Fact] public void Status_at_pickup_is_never_an_approval()
    {
        var t = PipelineEngine.Create(P, "ORD-1", "t", "Planned", Now);
        Assert.Null(t.PendingSourceStatus);
        PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
    }
    [Fact] public void Stale_approval_never_survives_block_and_unblock()
    {
        var t = At("plan", StageStatus.Running); t.LastSourceStatus = "Planned";
        PipelineEngine.SourceStatusChanged(P, t, "In Progress", null, Now); // approval while running: stored
        for (var i = 0; i < P.MaxAttemptsPerStage; i++) PipelineEngine.StageFailed(P, t, "boom", Now);
        Assert.Equal(StageStatus.Blocked, t.Status);
        Assert.Null(t.PendingSourceStatus);

        PipelineEngine.SourceStatusChanged(P, t, "Planned", null, Now); // developer moves the card back
        PipelineEngine.Unblock(t, Now);
        PipelineEngine.StageSucceeded(P, t, Now);

        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
    }

    [Fact] public void Block_clears_a_deferred_decision()
    {
        var t = At("plan", StageStatus.Running); t.PendingSourceStatus = "In Progress";
        PipelineEngine.Block(t, "no longer matches tracker query", Now);
        Assert.Null(t.PendingSourceStatus);
    }

    [Fact] public void Unblock_clears_a_deferred_decision()
    {
        var t = At("plan", StageStatus.Blocked); t.PendingSourceStatus = "In Progress";
        PipelineEngine.Unblock(t, Now);
        Assert.Null(t.PendingSourceStatus);
    }

    [Fact] public void Deferred_decision_no_longer_matching_the_card_is_discarded()
    {
        var t = At("plan", StageStatus.Running); t.LastSourceStatus = "Planned"; t.PendingSourceStatus = "In Progress";

        var o = PipelineEngine.StageSucceeded(P, t, Now);

        Assert.Equal(("plan", StageStatus.AwaitingApproval), (t.StageId, t.Status));
        Assert.Null(t.PendingSourceStatus);
        Assert.Equal("discarded source move to 'In Progress' (card is now 'Planned'); plan awaiting approval", o.Message);
    }

    [Fact] public void Failure_that_does_not_block_keeps_the_deferred_decision()
    {
        var t = At("plan", StageStatus.Running); t.LastSourceStatus = "In Progress"; t.PendingSourceStatus = "In Progress";
        PipelineEngine.StageFailed(P, t, "boom", Now);
        Assert.Equal((StageStatus.Pending, "In Progress"), (t.Status, t.PendingSourceStatus));
        PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal("implement", t.StageId);
    }

    private static readonly DateTimeOffset Gate = DateTimeOffset.Parse("2026-10-21T09:00Z");

    /// <summary><see cref="P"/> with <paramref name="stageId"/>'s gate set and review in <paramref name="mode"/>.</summary>
    private static ProjectConfig With(string stageId, GatePolicy gate, ReviewMode mode) =>
        P with
        {
            Pipeline = P.Pipeline.Select(s => s.Id == stageId ? s with { Gate = gate } : s).ToList(),
            Review = new ReviewConfig(mode, P.Agent, null),
        };

    private static Ticket Reviewing(string stageId)
    {
        var t = Awaiting(stageId); t.WaitingSince = Gate; t.ReviewDue = true;
        return t;
    }

    private static GateVerdict Low => new(RiskLevel.Low, "small change", [], ["tests pass"], []);
    private static GateVerdict High => new(RiskLevel.High, "risky change", ["touches pricing"], ["diff"], []);

    [Fact] public void Auto_gate_waits_and_owes_a_review()
    {
        var p = With("implement", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = At("implement");
        var o = PipelineEngine.StageSucceeded(p, t, Now);
        Assert.Equal((StageStatus.AwaitingApproval, true, "In Review"), (t.Status, t.ReviewDue, o.SourceStatusToSet));
    }

    [Fact] public void Auto_gate_with_review_off_waits_for_approval()
    {
        var p = With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Off);
        var t = At("plan");
        PipelineEngine.StageSucceeded(p, t, Now);
        Assert.Equal(("plan", StageStatus.AwaitingApproval, false), (t.StageId, t.Status, t.ReviewDue));
        Assert.False(PipelineEngine.WaitsOnReviewer(p, t));
    }

    [Fact] public void Human_gate_owes_review_only_when_review_is_on()
    {
        var shadow = At("implement");
        PipelineEngine.StageSucceeded(With("implement", GatePolicy.Human, ReviewMode.Shadow), shadow, Now);
        Assert.True(shadow.ReviewDue);

        var off = At("implement");
        PipelineEngine.StageSucceeded(With("implement", GatePolicy.Human, ReviewMode.Off), off, Now);
        Assert.False(off.ReviewDue);
    }

    [Fact] public void Deferred_approval_owes_no_review()
    {
        var t = At("plan", StageStatus.Running); t.LastSourceStatus = "In Progress"; t.PendingSourceStatus = "In Progress";
        PipelineEngine.StageSucceeded(With("plan", GatePolicy.Human, ReviewMode.Active), t, Now);
        Assert.Equal(("implement", false), (t.StageId, t.ReviewDue));
    }

    [Fact] public void Low_verdict_auto_approves_auto_gate_in_active_mode()
    {
        var p = With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("plan");
        Assert.True(PipelineEngine.WouldAutoApprove(p, t, Low, Gate));

        var o = PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);

        // The plan's verdict is not implement's: the badge waits for implement's own review.
        Assert.Equal(("implement", StageStatus.Pending, (RiskLevel?)null, false), (t.StageId, t.Status, t.Risk, t.ReviewDue));
        Assert.StartsWith("auto-approved plan", o.Message);
        Assert.Equal("In Progress", o.SourceStatusToSet);
        Assert.Equal(new GateDecisionInfo("plan", Gate, GateDecision.Approved, Auto: true), o.Decision);
    }

    [Fact] public void Low_verdict_in_shadow_mode_only_records()
    {
        var p = With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Shadow);
        var t = Reviewing("plan");
        Assert.False(PipelineEngine.WouldAutoApprove(p, t, Low, Gate));

        var o = PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);

        Assert.Equal((StageStatus.AwaitingApproval, false, RiskLevel.Low), (t.Status, t.ReviewDue, t.Risk));
        Assert.True(o.Changed); Assert.Null(o.Decision);
        Assert.StartsWith("reviewer (shadow): low risk", o.Message);
        Assert.Equal("reviewer (shadow): low risk — small change", o.Message);
    }

    [Fact] public void High_verdict_waits_on_you()
    {
        var p = With("implement", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("implement");

        var o = PipelineEngine.ReviewCompleted(p, t, High, Gate, allowAutoApprove: true, Now);

        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.High, false), (t.Status, t.Risk, t.ReviewDue));
        Assert.Equal("implement needs you: high risk — touches pricing", o.Message);
        Assert.Null(o.SourceStatusToSet); Assert.Null(o.Decision);
    }

    [Fact] public void High_verdict_without_reasons_quotes_the_summary()
    {
        var p = With("implement", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var o = PipelineEngine.ReviewCompleted(p, Reviewing("implement"), High with { Reasons = [] }, Gate, true, Now);
        Assert.Equal("implement needs you: high risk — risky change", o.Message);
    }

    [Fact] public void Human_gate_in_active_mode_records_the_verdict()
    {
        var p = With("implement", GatePolicy.Human, ReviewMode.Active);
        var t = Reviewing("implement");
        Assert.False(PipelineEngine.WouldAutoApprove(p, t, Low, Gate));

        var o = PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);

        Assert.Equal(StageStatus.AwaitingApproval, t.Status);
        Assert.Equal("reviewer: low risk — small change", o.Message);
    }

    [Fact] public void Auto_approval_not_allowed_keeps_waiting()
    {
        var p = With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("plan");

        var o = PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: false, Now);

        Assert.Equal(("plan", StageStatus.AwaitingApproval, RiskLevel.Low, false), (t.StageId, t.Status, t.Risk, t.ReviewDue));
        Assert.True(o.Changed); Assert.Null(o.Decision);
    }

    [Fact] public void Stale_review_of_earlier_round_is_not_applied()
    {
        var p = With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("plan"); t.WaitingSince = Now; t.Risk = RiskLevel.High;
        Assert.False(PipelineEngine.WouldAutoApprove(p, t, Low, Gate));

        var o = PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);

        Assert.False(o.Changed);
        Assert.Equal("review of plan finished after the gate was decided; not applied", o.Message);
        Assert.Equal((StageStatus.AwaitingApproval, RiskLevel.High, true), (t.Status, t.Risk, t.ReviewDue));
    }

    [Fact] public void Review_after_the_gate_was_decided_is_not_applied()
    {
        var p = With("implement", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("implement"); t.Status = StageStatus.Approved;
        var o = PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);
        Assert.False(o.Changed); Assert.Null(t.Risk); Assert.Equal(StageStatus.Approved, t.Status);
    }

    [Fact] public void Approve_and_send_back_report_gate_decision()
    {
        var approved = Reviewing("implement");
        var a = PipelineEngine.Approve(P, approved, Now);
        Assert.Equal(new GateDecisionInfo("implement", Gate, GateDecision.Approved, Auto: false), a.Decision);
        Assert.False(approved.ReviewDue);

        var sent = Reviewing("implement");
        var s = PipelineEngine.SendBack(P, sent, "fix", null, Now);
        Assert.Equal(new GateDecisionInfo("implement", Gate, GateDecision.SentBack, Auto: false), s.Decision);
        Assert.False(sent.ReviewDue);

        Assert.Null(PipelineEngine.SendBack(P, At("implement", StageStatus.Blocked), "fix", null, Now).Decision);
        Assert.Null(PipelineEngine.SendBack(P, At("implement", StageStatus.Approved), "fix", null, Now).Decision);
        Assert.Null(PipelineEngine.PostApprovalCompleted(P, approved, Now).Decision);
    }

    [Theory]
    [InlineData(StageStatus.AwaitingApproval)]
    [InlineData(StageStatus.Approved)]
    [InlineData(StageStatus.Blocked)]
    public void Send_back_from_gate_approved_or_blocked_is_a_touch(StageStatus status)
    {
        var o = PipelineEngine.SendBack(P, At("implement", status), "fix", null, Now);
        Assert.True(o.Changed);
        Assert.Equal(TouchKind.SendBack, o.Touch);
    }

    [Fact] public void Unblock_is_a_touch()
    {
        var o = PipelineEngine.Unblock(At("implement", StageStatus.Blocked), Now);
        Assert.Equal(TouchKind.Unblock, o.Touch);
        Assert.Null(PipelineEngine.Unblock(At("implement"), Now).Touch);
    }

    [Fact] public void Deferred_card_move_back_is_a_touch()
    {
        var t = At("implement", StageStatus.Running);
        var o = PipelineEngine.SourceStatusChanged(P, t, "Planned", null, Now);
        Assert.NotNull(t.PendingSourceStatus);
        var done = PipelineEngine.StageSucceeded(P, t, Now);
        Assert.Equal(TouchKind.SendBack, done.Touch);
    }

    [Fact] public void Stage_failure_and_merge_hold_are_not_touches()
    {
        Assert.Null(PipelineEngine.StageFailed(P, At("implement", StageStatus.Running), "boom", Now).Touch);
        Assert.Null(PipelineEngine.StageSucceeded(P, At("implement", StageStatus.Running), Now).Touch);
        Assert.Null(PipelineEngine.MergeHeld(P, At("implement", StageStatus.Approved), "dirty", Now).Touch);
    }

    [Fact] public void Block_clears_waiting_since_and_review_due()
    {
        var t = Reviewing("implement");
        PipelineEngine.Block(t, "no longer matches", Now);
        Assert.Equal((StageStatus.Blocked, (DateTimeOffset?)null, false), (t.Status, t.WaitingSince, t.ReviewDue));
    }

    [Fact] public void Merge_held_waits_again_as_a_new_round()
    {
        var p = With("release", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("release");
        var round = t.WaitingSince!.Value;
        PipelineEngine.ReviewCompleted(p, t, Low, round, allowAutoApprove: true, Now);
        Assert.Equal(StageStatus.Approved, t.Status);
        (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead, t.Attempts, t.MergeAcceptedAt) = ("main", "m1", "p1", 2, Now);

        var o = PipelineEngine.MergeHeld(p, t, "main moved since auto-approval (m1 → m2)", round.AddTicks(1)); // same ms

        Assert.True(o.Changed);
        Assert.Equal("merge held: main moved since auto-approval (m1 → m2)", o.Message);
        Assert.Equal((StageStatus.AwaitingApproval, true, 0), (t.Status, t.ReviewDue, t.Attempts));
        Assert.Equal(round.AddMilliseconds(1), t.WaitingSince); // a different round key even on the same clock
        Assert.Equal((null, null, null, null), (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead, t.MergeAcceptedAt));
        Assert.False(PipelineEngine.MergeHeld(p, t, "again", Now).Changed);
    }

    [Fact] public void Risk_badge_is_cleared_when_a_new_round_or_stage_starts()
    {
        var p = With("implement", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("implement");
        PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);
        Assert.Equal((StageStatus.Approved, RiskLevel.Low), (t.Status, t.Risk));

        PipelineEngine.PostApprovalCompleted(p, t, Now); // merged: on to build, which no reviewer judged
        Assert.Equal(("build", (RiskLevel?)null), (t.StageId, t.Risk));

        var release = At("release"); release.Risk = RiskLevel.Low; // e.g. the implement verdict
        PipelineEngine.StageSucceeded(p, release, Now);
        Assert.Equal((StageStatus.AwaitingApproval, true, (RiskLevel?)null), (release.Status, release.ReviewDue, release.Risk));
    }

    [Fact] public void Merge_held_clears_the_risk_badge()
    {
        var p = With("implement", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("implement");
        PipelineEngine.ReviewCompleted(p, t, Low, Gate, allowAutoApprove: true, Now);

        PipelineEngine.MergeHeld(p, t, "moved", Now);

        Assert.Equal((StageStatus.AwaitingApproval, (RiskLevel?)null), (t.Status, t.Risk));
    }

    [Fact] public void Third_merge_hold_owes_no_review_and_waits_on_the_developer()
    {
        var p = With("release", GatePolicy.AutoWhenLowRisk, ReviewMode.Active);
        var t = Reviewing("release");
        var messages = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            PipelineEngine.ReviewCompleted(p, t, Low, t.WaitingSince!.Value, allowAutoApprove: true, Now);
            Assert.Equal(StageStatus.Approved, t.Status);
            (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead) = ("main", $"m{i}", "p1");
            var o = PipelineEngine.MergeHeld(p, t, $"main moved since auto-approval (m{i} → m{i + 1})", Now.AddMinutes(i));
            messages.Add(o.Message);
            Assert.Equal(i, t.MergeHolds);
            Assert.Equal(i < 3, t.ReviewDue);
        }

        Assert.Equal("merge held: main moved since auto-approval (m3 → m4); main keeps moving; waiting on you", messages[^1]);
        Assert.Equal("merge held: main moved since auto-approval (m2 → m3)", messages[1]);
        Assert.Equal(StageStatus.AwaitingApproval, t.Status);
        Assert.False(PipelineEngine.WaitsOnReviewer(p, t));

        PipelineEngine.SendBack(p, t, null, "implement", Now); // a new stage starts the count again
        Assert.Equal(0, t.MergeHolds);
    }

    [Fact] public void Approved_heads_are_cleared_by_a_new_round_a_human_approval_and_a_new_stage()
    {
        static void Pin(Ticket t) =>
            (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead, t.MergeAcceptedAt) = ("b", "h", "p", Now);
        static void AssertCleared(Ticket t) => Assert.Equal((null, null, null, null),
            (t.ApprovedBranch, t.ApprovedHead, t.ApprovedBaseHead, t.MergeAcceptedAt));

        var t = At("implement");
        Pin(t);
        PipelineEngine.StageSucceeded(P, t, Now);
        AssertCleared(t);

        Pin(t);
        PipelineEngine.Approve(P, t, Now);
        AssertCleared(t);

        Pin(t);
        PipelineEngine.PostApprovalCompleted(P, t, Now);
        AssertCleared(t);
    }

    [Fact] public void Waits_on_reviewer_only_for_active_auto_gates()
    {
        var t = Reviewing("plan");
        Assert.False(PipelineEngine.WaitsOnReviewer(With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Shadow), t));
        Assert.False(PipelineEngine.WaitsOnReviewer(With("plan", GatePolicy.Human, ReviewMode.Active), t));
        Assert.True(PipelineEngine.WaitsOnReviewer(With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Active), t));

        t.ReviewDue = false;
        Assert.False(PipelineEngine.WaitsOnReviewer(With("plan", GatePolicy.AutoWhenLowRisk, ReviewMode.Active), t));
    }

    private static ProjectConfig Evidence(EvidenceMode mode, ReviewMode review = ReviewMode.Off) =>
        With("implement", GatePolicy.Human, review) with { Evidence = P.Evidence with { Mode = mode } };

    [Fact] public void Gate_round_owes_evidence_for_gated_implement()
    {
        var on = At("implement");
        PipelineEngine.StageSucceeded(Evidence(EvidenceMode.HighRisk, ReviewMode.Active), on, Now);
        Assert.True(on.EvidenceDue);

        var plan = At("plan");
        PipelineEngine.StageSucceeded(Evidence(EvidenceMode.HighRisk), plan, Now);
        Assert.False(plan.EvidenceDue);

        var off = At("implement");
        PipelineEngine.StageSucceeded(Evidence(EvidenceMode.Off), off, Now);
        Assert.False(off.EvidenceDue);
    }

    private static readonly RiskArea Pricing = new("pricing", "money", ["src/Pricing/**"]);

    [Fact] public void Low_verdict_clears_evidence_due_in_high_risk_mode()
    {
        var p = Evidence(EvidenceMode.HighRisk, ReviewMode.Active);
        var t = At("implement");
        PipelineEngine.StageSucceeded(p, t, Now);
        Assert.Equal((true, true), (t.ReviewDue, t.EvidenceDue));

        PipelineEngine.ReviewCompleted(p, t, Low, t.WaitingSince!.Value, allowAutoApprove: false, Now);

        Assert.Equal((StageStatus.AwaitingApproval, false), (t.Status, t.EvidenceDue));
    }

    [Fact] public void High_verdict_keeps_evidence_due()
    {
        var p = Evidence(EvidenceMode.HighRisk, ReviewMode.Active);
        var t = At("implement");
        PipelineEngine.StageSucceeded(p, t, Now);

        PipelineEngine.ReviewCompleted(p, t, High, t.WaitingSince!.Value, allowAutoApprove: false, Now);

        Assert.True(t.EvidenceDue);
        t.PullRequestId = 7;
        Assert.True(PipelineEngine.WaitsOnEvidence(p, t));
    }

    [Fact] public void Always_mode_keeps_evidence_due_after_a_low_verdict()
    {
        var p = Evidence(EvidenceMode.Always, ReviewMode.Active);
        var t = At("implement");
        PipelineEngine.StageSucceeded(p, t, Now);

        PipelineEngine.ReviewCompleted(p, t, Low, t.WaitingSince!.Value, allowAutoApprove: false, Now);

        Assert.True(t.EvidenceDue);
    }

    [Fact] public void Review_off_owes_evidence_only_with_risk_areas_or_always()
    {
        var none = At("implement");
        PipelineEngine.StageSucceeded(Evidence(EvidenceMode.HighRisk), none, Now);
        Assert.False(none.EvidenceDue);

        var areas = At("implement");
        PipelineEngine.StageSucceeded(Evidence(EvidenceMode.HighRisk) with { RiskAreas = [Pricing] }, areas, Now);
        Assert.True(areas.EvidenceDue); // only a clone of the branch can tell

        var always = At("implement");
        PipelineEngine.StageSucceeded(Evidence(EvidenceMode.Always), always, Now);
        Assert.True(always.EvidenceDue);

        var held = At("implement", StageStatus.Approved); held.WaitingSince = Gate;
        PipelineEngine.MergeHeld(Evidence(EvidenceMode.HighRisk), held, "moved", Now);
        Assert.False(held.EvidenceDue);
    }

    [Fact] public void Evidence_waits_for_a_pull_request()
    {
        var p = Evidence(EvidenceMode.Always);
        var t = Awaiting("implement"); t.WaitingSince = Gate; t.EvidenceDue = true;
        Assert.False(PipelineEngine.WaitsOnEvidence(p, t));
        t.PullRequestId = 7;
        Assert.True(PipelineEngine.WaitsOnEvidence(p, t));
    }

    [Fact] public void Waits_on_evidence_only_after_owed_review()
    {
        var p = Evidence(EvidenceMode.HighRisk, ReviewMode.Active);
        var t = Awaiting("implement"); t.WaitingSince = Gate; t.EvidenceDue = true; t.ReviewDue = true; t.PullRequestId = 7;
        Assert.False(PipelineEngine.WaitsOnEvidence(p, t));
        t.ReviewDue = false;
        Assert.True(PipelineEngine.WaitsOnEvidence(p, t));
        t.WaitingSince = null;
        Assert.False(PipelineEngine.WaitsOnEvidence(p, t));
        t.WaitingSince = Gate; t.EvidenceDue = false;
        Assert.False(PipelineEngine.WaitsOnEvidence(p, t));
    }

    [Fact] public void Decisions_clear_evidence_due()
    {
        var p = Evidence(EvidenceMode.Always);
        var approve = Awaiting("implement"); approve.WaitingSince = Gate; approve.EvidenceDue = true;
        PipelineEngine.Approve(p, approve, Now);
        Assert.False(approve.EvidenceDue);

        var back = Awaiting("implement"); back.WaitingSince = Gate; back.EvidenceDue = true;
        PipelineEngine.SendBack(p, back, "fix", null, Now);
        Assert.False(back.EvidenceDue);

        var block = Awaiting("implement"); block.WaitingSince = Gate; block.EvidenceDue = true;
        PipelineEngine.Block(block, "no", Now);
        Assert.False(block.EvidenceDue);
    }

    [Fact] public void Merge_hold_owes_evidence_again()
    {
        var p = Evidence(EvidenceMode.Always);
        var t = At("implement", StageStatus.Approved); t.WaitingSince = Gate;
        PipelineEngine.MergeHeld(p, t, "moved", Now);
        Assert.True(t.EvidenceDue);

        var off = At("implement", StageStatus.Approved); off.WaitingSince = Gate; off.EvidenceDue = true;
        PipelineEngine.MergeHeld(Evidence(EvidenceMode.Off), off, "moved", Now);
        Assert.False(off.EvidenceDue);
    }

    [Fact] public void Evidence_completed_applies_to_same_round_only()
    {
        var p = Evidence(EvidenceMode.Always);
        var t = Awaiting("implement"); t.WaitingSince = Gate; t.EvidenceDue = true;

        var other = PipelineEngine.EvidenceCompleted(p, t, Gate.AddSeconds(1), "done", Now);
        Assert.False(other.Changed);
        Assert.Equal("evidence for implement finished after the gate was decided; not applied", other.Message);
        Assert.True(t.EvidenceDue);

        var same = PipelineEngine.EvidenceCompleted(p, t, Gate, "evidence gathered", Now);
        Assert.True(same.Changed);
        Assert.False(t.EvidenceDue);
        Assert.Equal("evidence gathered", same.Message);

        var notWaiting = At("implement", StageStatus.Approved); notWaiting.WaitingSince = Gate;
        Assert.False(PipelineEngine.EvidenceCompleted(p, notWaiting, Gate, "x", Now).Changed);
    }

    [Fact] public void Send_back_and_success_clear_pr_feedback()
    {
        var p = Evidence(EvidenceMode.Off);
        var t = At("implement", StageStatus.Running); t.PrFeedbackJson = "[]";
        PipelineEngine.StageSucceeded(p, t, Now);
        Assert.Null(t.PrFeedbackJson);

        var back = Awaiting("implement"); back.WaitingSince = Gate; back.PrFeedbackJson = "[]";
        PipelineEngine.SendBack(p, back, "fix", null, Now);
        Assert.Null(back.PrFeedbackJson);
    }

    // Release batches

    private static Ticket Member(string stageId, StageStatus status = StageStatus.Pending)
    {
        var t = At(stageId, status); t.ReleaseTicketId = Guid.NewGuid();
        return t;
    }

    private static Ticket WaitingRelease(ProjectConfig p)
    {
        var t = PipelineEngine.CreateRelease(p, 1, "Release 1: ORD-1", "symphonist/release-1", "Tickets:", Now);
        PipelineEngine.StageSucceeded(p, t, Now);
        return t;
    }

    [Fact] public void Release_ticket_waits_for_approval_even_with_gate_none()
    {
        var p = With("release", GatePolicy.None, ReviewMode.Off);
        var t = PipelineEngine.CreateRelease(p, 3, "Release 3: ORD-1", "symphonist/release-3", "Tickets:\n- ORD-1: t", Now);

        Assert.Equal(TicketKind.Release, t.Kind);
        Assert.Equal("_release-3", t.Key);
        Assert.Equal("Release 3: ORD-1", t.Title);
        Assert.Equal("release", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Equal("symphonist/release-3", t.Branch);
        Assert.Equal("Tickets:\n- ORD-1: t", t.ReleaseSummary);
        Assert.Equal("orders", t.ProjectId);
        Assert.NotEqual(Guid.Empty, t.Id);

        PipelineEngine.StageSucceeded(p, t, Now);
        Assert.Equal(StageStatus.AwaitingApproval, t.Status);
        Assert.Equal(Now, t.WaitingSince);
    }

    [Fact] public void Ready_to_release_excludes_batched_and_legacy_tickets()
    {
        Assert.True(PipelineEngine.IsReadyToRelease(P, At("release")));
        Assert.Equal(TicketKind.Work, At("release").Kind);
        Assert.False(PipelineEngine.IsReadyToRelease(P, Member("release")));
        var legacy = At("release"); legacy.ReleasePullRequestId = 7;
        Assert.False(PipelineEngine.IsReadyToRelease(P, legacy));
        Assert.False(PipelineEngine.IsReadyToRelease(P, PipelineEngine.CreateRelease(P, 1, "R", "b", "s", Now)));
        Assert.False(PipelineEngine.IsReadyToRelease(P, At("build")));
        Assert.False(PipelineEngine.IsReadyToRelease(P, At("release", StageStatus.Blocked)));
        var noRelease = P with { Pipeline = P.Pipeline.Where(s => s.Kind != StageKind.Release).ToList() };
        Assert.False(PipelineEngine.IsReadyToRelease(noRelease, At("deploy")));
    }

    [Fact] public void Release_completed_moves_member_past_release()
    {
        var t = Member("release");
        var o = PipelineEngine.ReleaseCompleted(P, t, "abc123", Now);
        Assert.True(o.Changed);
        Assert.Equal("deploy", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Equal("abc123", t.ReleaseMergeCommit);
        Assert.Null(t.ReleaseTicketId);
        Assert.Null(o.Touch);
    }

    [Fact] public void Release_completed_without_later_stages_is_done()
    {
        var p = P with { Pipeline = P.Pipeline.TakeWhile(s => s.Kind != StageKind.Deploy).ToList() };
        var t = Member("release");
        var o = PipelineEngine.ReleaseCompleted(p, t, "abc123", Now);
        Assert.True(o.Changed);
        Assert.Equal("release", t.StageId);
        Assert.Equal(StageStatus.Done, t.Status);
        Assert.Equal("abc123", t.ReleaseMergeCommit);
        Assert.Null(t.ReleaseTicketId);
    }

    [Fact] public void Release_completed_applies_a_card_moved_back()
    {
        var t = Member("release"); t.LastSourceStatus = "Planned"; t.PendingSourceStatus = "Planned";
        var o = PipelineEngine.ReleaseCompleted(P, t, "abc123", Now);
        Assert.Equal("plan", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Null(t.PendingSourceStatus);
        Assert.Null(t.ReleaseTicketId);
        Assert.Equal("abc123", t.ReleaseMergeCommit);
        Assert.Equal("released; sent back to plan: card moved to 'Planned'", o.Message);
        Assert.Null(o.Touch);
        Assert.Null(o.Decision);
    }

    [Fact] public void Release_completed_ignores_a_card_moved_to_approved_status()
    {
        var p = P with
        {
            Pipeline = P.Pipeline.Select(s => s.Id == "release" ? s with { ApprovedWhenStatus = "Released" } : s).ToList(),
        };
        var t = Member("release"); t.LastSourceStatus = "Released"; t.PendingSourceStatus = "Released";
        PipelineEngine.ReleaseCompleted(p, t, "abc123", Now);
        Assert.Equal("deploy", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
    }

    [Fact] public void Release_completed_finishes_an_also_shipped_member()
    {
        var t = Member("build", StageStatus.Running);
        PipelineEngine.ReleaseCompleted(P, t, "abc123", Now);
        Assert.Equal("deploy", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Equal("abc123", t.ReleaseMergeCommit);
    }

    [Fact] public void Release_cancelled_returns_member_to_ready_or_its_own_progress()
    {
        var ready = Member("release");
        var o = PipelineEngine.ReleaseCancelled(P, ready, "_release-2", Now);
        Assert.True(o.Changed);
        Assert.Null(ready.ReleaseTicketId);
        Assert.Equal("_release-2 cancelled; ready to release again", o.Message);
        Assert.True(PipelineEngine.IsReadyToRelease(P, ready));

        var shipped = Member("build");
        Assert.Equal("_release-2 cancelled; back to its own progress", PipelineEngine.ReleaseCancelled(P, shipped, "_release-2", Now).Message);
        Assert.Null(shipped.ReleaseTicketId);
        Assert.Equal("build", shipped.StageId);
    }

    [Fact] public void Ready_ticket_card_moved_back_is_sent_back_now()
    {
        var t = At("release"); t.LastSourceStatus = "Done";
        var o = PipelineEngine.SourceStatusChanged(P, t, "Planned", "rethink", Now);
        Assert.True(o.Changed);
        Assert.Equal("plan", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Equal("rethink", t.Feedback);
        Assert.Null(t.PendingSourceStatus);
        Assert.Equal(TouchKind.SendBack, o.Touch);
        Assert.False(PipelineEngine.IsReadyToRelease(P, t));
    }

    [Fact] public void Batch_member_card_moved_back_stays_deferred()
    {
        var t = Member("release"); t.LastSourceStatus = "Done";
        var o = PipelineEngine.SourceStatusChanged(P, t, "Planned", null, Now);
        Assert.Equal("release", t.StageId);
        Assert.Equal("Planned", t.PendingSourceStatus);
        Assert.Null(o.Touch);

        var legacy = At("release"); legacy.ReleasePullRequestId = 7; legacy.LastSourceStatus = "Done";
        PipelineEngine.SourceStatusChanged(P, legacy, "Planned", null, Now);
        Assert.Equal("release", legacy.StageId);
        Assert.Equal("Planned", legacy.PendingSourceStatus);
    }

    [Fact] public void Release_cancelled_applies_a_card_moved_back()
    {
        var t = Member("release"); t.LastSourceStatus = "Planned"; t.PendingSourceStatus = "Planned";
        var o = PipelineEngine.ReleaseCancelled(P, t, "_release-2", Now);
        Assert.True(o.Changed);
        Assert.Equal("plan", t.StageId);
        Assert.Equal(StageStatus.Pending, t.Status);
        Assert.Null(t.PendingSourceStatus);
        Assert.Null(t.ReleaseTicketId);
        Assert.Equal("_release-2 cancelled; sent back to plan: card moved to 'Planned'", o.Message);
        Assert.False(PipelineEngine.IsReadyToRelease(P, t));
    }

    [Fact] public void Send_back_of_release_ticket_cancels_it()
    {
        var t = WaitingRelease(P); t.ReviewDue = true;
        var waiting = t.WaitingSince!.Value;
        var o = PipelineEngine.SendBack(P, t, "not this week", "plan", Now.AddMinutes(1));
        Assert.True(o.Changed);
        Assert.Equal(StageStatus.Done, t.Status);
        Assert.Equal("release", t.StageId);
        Assert.Equal("not this week", t.Feedback);
        Assert.Null(t.WaitingSince);
        Assert.False(t.ReviewDue);
        Assert.Equal("release cancelled", o.Message);
        Assert.Equal(new GateDecisionInfo("release", waiting, GateDecision.SentBack, Auto: false), o.Decision);
        Assert.Equal(TouchKind.SendBack, o.Touch);
        Assert.Null(o.SourceStatusToSet);
    }

    [Fact] public void Send_back_of_a_release_with_nothing_to_merge_forgets_its_production_commit()
    {
        // "Nothing to release" records production's head before the gate; a cancelled release must not look merged.
        var t = WaitingRelease(P);
        t.ReleaseMergeCommit = "prod-head";
        var o = PipelineEngine.SendBack(P, t, "no", null, Now);
        Assert.True(o.Changed);
        Assert.Equal(StageStatus.Done, t.Status);
        Assert.Null(t.ReleaseMergeCommit);
    }

    [Fact] public void Merged_release_ticket_is_done_not_advanced()
    {
        var t = WaitingRelease(P);
        Assert.True(PipelineEngine.Approve(P, t, Now).Changed);
        Assert.Equal(StageStatus.Approved, t.Status);
        var o = PipelineEngine.PostApprovalCompleted(P, t, Now);
        Assert.True(o.Changed);
        Assert.Equal("release merged", o.Message);
        Assert.Equal(StageStatus.Done, t.Status);
        Assert.Equal("release", t.StageId);
    }

    [Fact] public void Card_moves_never_decide_a_release_ticket()
    {
        var p = P with
        {
            Pipeline = P.Pipeline.Select(s => s.Id == "release" ? s with { ApprovedWhenStatus = "Released" } : s).ToList(),
        };
        var t = WaitingRelease(p);
        Assert.False(PipelineEngine.SourceStatusChanged(p, t, "Released", null, Now).Changed);
        Assert.Equal(StageStatus.AwaitingApproval, t.Status);
    }
}
