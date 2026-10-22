using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;

namespace Symphonist.Core.Tests.Pipeline;

public class PipelineEngineTests
{
    private static readonly ProjectConfig P =
        ProjectConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "Config", "orders.yaml"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T10:00Z");

    private static Ticket At(string stageId, StageStatus status = StageStatus.Pending) =>
        new() { Id = Guid.NewGuid(), ProjectId = "orders", Key = "ORD-1", StageId = stageId, Status = status };

    private static Ticket Awaiting(string stageId) => At(stageId, StageStatus.AwaitingApproval);

    [Fact] public void Create_starts_at_stage_matching_source_status()
    {
        Assert.Equal("plan", PipelineEngine.Create(P, "ORD-1", "t", "planned", Now).StageId);
        Assert.Equal("triage", PipelineEngine.Create(P, "ORD-2", "t", "Selected", Now).StageId);
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
}
