using System.Globalization;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Prompts;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Orchestration;

/// <summary>
/// Runs the read-only reviewer at a gate and returns its verdict. Fails closed: every error, and every risk-area path in
/// the change, gives a high verdict with the reason recorded.
/// </summary>
public sealed class GateReviewer(IGitWorkspace git, IAgentRunners agents, ITicketStore store, TimeProvider time)
{
    /// <summary>Patches longer than this are cut in the prompt, and the verdict is forced high.</summary>
    public const int MaxDiffChars = 200_000;

    private const int FilesListedPerArea = 5;

    /// <summary>Never throws for agent or git failures (returns <see cref="GateVerdict.Failed"/>); honours <paramref name="ct"/>.</summary>
    public async Task<GateVerdict> ReviewAsync(ProjectConfig p, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        try
        {
            return await RunAsync(p, t, source, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return GateVerdict.Failed(e.Message);
        }
    }

    public static string RiskAreasText(IReadOnlyList<RiskArea> areas) =>
        areas.Count == 0
            ? "(none configured)"
            : string.Join("\n", areas.Select(a =>
                a.Paths.Count == 0 ? $"- {a.Name}: {a.Description}" : $"- {a.Name}: {a.Description} (paths: {string.Join(", ", a.Paths)})"));

    private async Task<GateVerdict> RunAsync(ProjectConfig p, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        var stage = p.Stage(t.StageId);
        var runner = agents.For(p.Review.Agent.Type);
        var branch = t.Branch ?? $"symphonist/{t.Key}";
        var workspace = await git.PrepareAsync(p, t.Key, branch, ct);

        var range = DiffRange(p, stage.Kind);
        IReadOnlyList<string> changed = [];
        var diff = "";
        if (range.Length > 0)
        {
            changed = await git.ChangedFilesAsync(workspace, range, ct);
            diff = await git.DiffAsync(workspace, range, ct);
        }
        var truncated = diff.Length > MaxDiffChars;

        // A random fence per run: text inside cannot guess it to close the fence early.
        var nonce = Guid.NewGuid().ToString("N");
        string Fence(string text) => $"<untrusted-{nonce}>\n{text}\n</untrusted-{nonce}>";

        var values = PromptValues.For(p, t, source, branch, feedback: "");
        foreach (var key in new[] { "ticket.title", "ticket.description", "ticket.comments" })
            values[key] = Fence(values[key] ?? "");
        values["gate.stage"] = stage.Id;
        values["gate.kind"] = stage.Kind.ToString().ToLowerInvariant();
        values["review.diff_range"] = range;
        values["review.changed_files"] = range.Length == 0 ? "(no diff for this stage)"
            : changed.Count == 0 ? "(no changed files)"
            : Fence(string.Join("\n", changed));
        values["review.diff"] = range.Length == 0 ? "(no diff for this stage)"
            : truncated
                ? $"NOTE: the diff is {diff.Length} characters and was truncated to the first {MaxDiffChars}; you cannot review it fully, so the verdict will be high.\n"
                  + Fence(diff[..MaxDiffChars])
            : Fence(diff);
        values["review.history"] = await HistoryAsync(p, t, stage, ct);
        values["review.stage_summary"] = await StageSummaryAsync(t, ct) is { Length: > 0 } summary ? Fence(summary) : "";
        var prompt = PromptRenderer.Render(PromptLibrary.GetReview(p), values);

        var startedAt = time.GetUtcNow();
        AgentRunResult result;
        try
        {
            result = await runner.RunAsync(new AgentRunRequest(workspace, prompt, p.Review.Agent, GateReviewSchema.Json,
                ReadOnlyWorkspace: true), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The runner itself failed (e.g. missing container image): still record the attempt.
            result = new AgentRunResult(false, null, null, time.GetUtcNow() - startedAt, e.Message, "");
        }

        // Structured output may be present on a failed run: only a successful run's output is judged.
        var verdict = result.Succeeded
            ? GateVerdictParser.Parse(result.Structured)
            : GateVerdict.Failed(result.Error ?? "reviewer run failed");
        verdict = Guard(p, changed, truncated, verdict);

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", p.Id, t.Key, $"{t.StageId}-review-{stamp}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, result.RawLog, ct);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, t.StageId, startedAt, result.Duration, result.Succeeded,
            result.CostUsd, result.Error, logPath, $"{Level(verdict.Risk)} risk: {verdict.Summary}", IsReview: true), ct);
        return verdict;
    }

    private static string DiffRange(ProjectConfig p, StageKind kind) => kind switch
    {
        StageKind.Implement => $"origin/{p.Repo.MainBranch}...HEAD",
        StageKind.Release => $"origin/{p.Repo.ProductionBranch}...origin/{p.Repo.MainBranch}",
        _ => "", // Triage, Plan: the reviewer reads the ticket and the spec/plan files
    };

    /// <summary>What the reviewer's word alone cannot clear: risk-area paths, names we could not read, a cut diff.</summary>
    private static GateVerdict Guard(ProjectConfig p, IReadOnlyList<string> changed, bool truncated, GateVerdict verdict)
    {
        foreach (var area in RiskAreaMatcher.Touched(p.RiskAreas, changed).GroupBy(x => x.Area))
        {
            var files = area.Select(x => x.File).ToList();
            foreach (var file in files.Take(FilesListedPerArea))
                verdict = verdict.WithForcedHigh($"touches risk area '{area.Key}': {file}");
            if (files.Count > FilesListedPerArea)
                verdict = verdict.WithForcedHigh($"touches risk area '{area.Key}': (+{files.Count - FilesListedPerArea} more)");
        }
        // git paths are bytes; one that is not valid UTF-8 decodes with U+FFFD and could dodge a pattern.
        foreach (var file in changed.Where(f => f.Contains('\uFFFD')))
            verdict = verdict.WithForcedHigh($"changed file name could not be decoded: {file}");
        if (truncated) verdict = verdict.WithForcedHigh("diff too large to review fully");
        return verdict;
    }

    private async Task<string> HistoryAsync(ProjectConfig p, Ticket t, StageDefinition stage, CancellationToken ct)
    {
        var lines = (await store.GateReviewsAsync(t.Id, ct))
            .Where(r => !(r.StageId == t.StageId && r.WaitingSince == t.WaitingSince)) // the round under review
            .OrderBy(r => r.At)
            .Select(r => $"{r.StageId}: {Level(r.Verdict.Risk)} risk ({Decision(r)})")
            .ToList();
        if (stage.Kind == StageKind.Release)
        {
            lines.Add(t.MergeCommit is { } merge ? $"build: succeeded at {merge}" : "build: no merge commit recorded");
            var earlier = p.Pipeline.TakeWhile(s => s.Id != stage.Id);
            lines.AddRange(earlier.Where(s => s.Kind == StageKind.Uat).Select(s => $"passed: {s.Id}"));
        }
        return lines.Count == 0 ? "(none)" : string.Join("\n", lines);
    }

    private static string Decision(GateReview r) => r switch
    {
        { Decision: GateDecision.Approved, AutoDecided: true } => "auto-approved",
        { Decision: GateDecision.Approved } => "approved",
        { Decision: GateDecision.SentBack } => "sent back",
        _ => "undecided",
    };

    private async Task<string?> StageSummaryAsync(Ticket t, CancellationToken ct) =>
        (await store.AgentRunsAsync(t.Id, ct))
        .Where(r => r.StageId == t.StageId && !r.IsReview)
        .MaxBy(r => r.StartedAt)?.Summary;

    private static string Level(RiskLevel risk) => risk == RiskLevel.Low ? "low" : "high";
}
