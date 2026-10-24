using System.Globalization;
using System.Text.RegularExpressions;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Prompts;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Orchestration;

/// <param name="ReviewedHead">
/// The commit whose change was reviewed (the pushed ticket branch for implement, main for release); null when no
/// diff was reviewed or it could not be resolved. A verdict only covers this commit.
/// </param>
/// <param name="ReviewedBranch">The branch <paramref name="ReviewedHead"/> is the head of; null when there is none.</param>
/// <param name="ReviewedBase">The commit the change was diffed against (main for implement, production for release).</param>
public sealed record GateReviewOutcome(GateVerdict Verdict, string? ReviewedHead, string? ReviewedBranch = null,
    string? ReviewedBase = null);

/// <summary>
/// Runs the read-only reviewer at a gate and returns its verdict. Fails closed: every error, and every risk-area path in
/// the change, gives a high verdict with the reason recorded.
/// </summary>
public sealed partial class GateReviewer(IGitWorkspace git, IAgentRunners agents, ITicketStore store, TimeProvider time)
{
    /// <summary>Patches longer than this are cut in the prompt, and the verdict is forced high.</summary>
    public const int MaxDiffChars = 200_000;

    private const int FilesListedPerArea = 5;
    private const int MaxPathLength = 300;

    /// <summary>Binary types a patch may show as "Binary files ... differ"; any other binary line hides content.</summary>
    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "ico", "webp", "woff", "woff2", "ttf", "eot",
    };

    /// <summary>Never throws for agent or git failures (returns <see cref="GateVerdict.Failed"/>); honours <paramref name="ct"/>.</summary>
    public async Task<GateReviewOutcome> ReviewAsync(ProjectConfig p, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        try
        {
            return await RunAsync(p, t, source, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new GateReviewOutcome(GateVerdict.Failed(e.Message), null);
        }
    }

    public static string RiskAreasText(IReadOnlyList<RiskArea> areas) =>
        areas.Count == 0
            ? "(none configured)"
            : string.Join("\n", areas.Select(a =>
                a.Paths.Count == 0 ? $"- {a.Name}: {a.Description}" : $"- {a.Name}: {a.Description} (paths: {string.Join(", ", a.Paths)})"));

    private async Task<GateReviewOutcome> RunAsync(ProjectConfig p, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        var stage = p.Stage(t.StageId);
        var runner = agents.For(p.Review.Agent.Type);
        var branch = t.Branch ?? $"symphonist/{t.Key}";
        foreach (var name in new[] { branch, p.Repo.MainBranch, p.Repo.ProductionBranch })
            if (!IsValidBranchName(name)) return new(GateVerdict.Failed($"invalid branch name '{name}'"), null);
        var workspace = await git.PrepareAsync(p, t.Key, branch, ct);
        var forced = new List<string>();

        // Pin the review to commits: what is pushed (implement) or about to ship (release), never the local checkout.
        // Full ref names: a tag or local branch called "origin/main" must not shadow the remote-tracking branch.
        var range = "";
        string? baseSha = null, head = null, headBranch = null;
        IReadOnlyList<string> changed = [];
        var diff = "";
        if (DiffRefs(p, stage.Kind, branch) is var (baseRef, headRef))
        {
            headBranch = headRef["origin/".Length..];
            baseSha = await git.ResolveCommitAsync(workspace, $"refs/remotes/{baseRef}", ct);
            if (baseSha is null) return new(GateVerdict.Failed($"cannot resolve {baseRef}"), null);
            head = await git.ResolveCommitAsync(workspace, $"refs/remotes/{headRef}", ct);
            if (head is null)
                return new(GateVerdict.Failed(stage.Kind == StageKind.Implement
                    ? $"branch not pushed: {headRef}"
                    : $"cannot resolve {headRef}"), null);
            range = $"{baseSha}...{head}";
            changed = await git.ChangedFilesAsync(workspace, range, ct);
            diff = await git.DiffAsync(workspace, range, ct);
            if (stage.Kind == StageKind.Implement && changed.Count == 0) forced.Add("no changes to review");
        }
        var truncated = diff.Length > MaxDiffChars;

        // A random fence per run: text inside cannot guess it, and forged markers inside are neutralised.
        var nonce = Guid.NewGuid().ToString("N");
        string Fence(string text) => $"<untrusted-{nonce}>\n{ForgedMarker().Replace(text, "&lt;$1")}\n</untrusted-{nonce}>";

        var values = PromptValues.For(p, t, source, branch, feedback: "");
        foreach (var key in new[] { "ticket.title", "ticket.description", "ticket.comments", "ticket.url", "branch" })
            values[key] = Fence(values[key] ?? "");
        foreach (var key in new[] { "spec_path", "plan_path" })
        {
            var path = values[key] ?? "";
            if (path.Length == 0) continue;
            if (IsSafeRepoPath(path)) values[key] = Fence(path);
            else
            {
                values[key] = "(invalid path)";
                forced.Add($"invalid {key}");
            }
        }
        values["review.fence_id"] = nonce;
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
        verdict = Guard(p, changed, diff, truncated, verdict);
        foreach (var reason in forced) verdict = verdict.WithForcedHigh(reason);

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", p.Id, t.Key, $"{t.StageId}-review-{stamp}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, result.RawLog, ct);
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, t.StageId, startedAt, result.Duration, result.Succeeded,
            result.CostUsd, result.Error, logPath, $"{Level(verdict.Risk)} risk: {verdict.Summary}", IsReview: true), ct);
        return new GateReviewOutcome(verdict, head, head is null ? null : headBranch, baseSha);
    }

    [GeneratedRegex("<(/?untrusted-)", RegexOptions.IgnoreCase)]
    private static partial Regex ForgedMarker();

    [GeneratedRegex("^(new file mode|deleted file mode|old mode|new mode|index [0-9a-f]+[.][.][0-9a-f]+) 160000$")]
    private static partial Regex GitlinkHeader();

    [GeneratedRegex("^Binary files (.+) and (.+) differ$")]
    private static partial Regex BinaryLine();

    private static (string Base, string Head)? DiffRefs(ProjectConfig p, StageKind kind, string branch) => kind switch
    {
        StageKind.Implement => ($"origin/{p.Repo.MainBranch}", $"origin/{branch}"),
        StageKind.Release => ($"origin/{p.Repo.ProductionBranch}", $"origin/{p.Repo.MainBranch}"),
        _ => null, // Triage, Plan: the reviewer reads the ticket and the spec/plan files
    };

    /// <summary>The rules of <c>git check-ref-format --branch</c> that matter here; anything doubtful is rejected.</summary>
    internal static bool IsValidBranchName(string name) =>
        name.Length is > 0 and <= 255
        && !name.StartsWith('-') && !name.StartsWith('/') && !name.EndsWith('/') && !name.EndsWith('.')
        && !name.EndsWith(".lock", StringComparison.Ordinal)
        && !name.Contains("..") && !name.Contains("@{") && !name.Contains("//") && name != "@"
        && !name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
        && !name.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal));

    /// <summary>Single-line, repo-relative, no "..", at most <see cref="MaxPathLength"/> characters.</summary>
    private static bool IsSafeRepoPath(string path) =>
        path.Length <= MaxPathLength
        && !path.Any(char.IsControl)
        && !path.StartsWith('/') && !path.StartsWith('\\') && !path.Contains(':') && !Path.IsPathRooted(path)
        && !path.Split('/', '\\').Contains("..");

    /// <summary>
    /// What the reviewer's word alone cannot clear: risk-area paths, names we could not read, a cut diff, and changes
    /// whose content the patch does not show (unknown binary files, submodule pointers, git attributes).
    /// </summary>
    private static GateVerdict Guard(ProjectConfig p, IReadOnlyList<string> changed, string diff, bool truncated,
        GateVerdict verdict)
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
        foreach (var file in changed.Where(IsGitMetadata))
            verdict = verdict.WithForcedHigh($"changes git attributes or submodules: {file}");
        foreach (var file in changed.Where(IsAgentConfiguration))
            verdict = verdict.WithForcedHigh($"changes agent configuration: {file}");

        var lines = diff.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        foreach (var line in lines)
        {
            if (BinaryLine().Match(line) is not { Success: true } m) continue;
            var sides = new[] { m.Groups[1].Value, m.Groups[2].Value }.Where(s => s != "/dev/null").Select(Unquote).ToList();
            foreach (var side in sides.Where(s => !KnownBinaryExtensions.Contains(Path.GetExtension(s).TrimStart('.'))).Take(1))
                verdict = verdict.WithForcedHigh($"patch hides the content of {side} (shown as binary)");
        }
        // Whatever diff.submodule says, a gitlink shows as a 160000 mode header, a "Submodule " line or "Subproject commit".
        if (lines.Any(l => l.StartsWith("+Subproject commit ", StringComparison.Ordinal)
                           || l.StartsWith("-Subproject commit ", StringComparison.Ordinal)
                           || l.StartsWith("Submodule ", StringComparison.Ordinal)
                           || GitlinkHeader().IsMatch(l)))
            verdict = verdict.WithForcedHigh("changes a submodule pointer");

        if (truncated) verdict = verdict.WithForcedHigh("diff too large to review fully");
        return verdict;
    }

    private static bool IsGitMetadata(string file) =>
        file.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)
        || file.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith("/.gitattributes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files that instruct or configure Claude Code (and other agents) in this repository: they steer every later agent
    /// run here, so the reviewer's word alone cannot clear them.
    /// </summary>
    private static bool IsAgentConfiguration(string file)
    {
        var parts = file.Split('/');
        var name = parts[^1];
        return name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)
               || name.Equals("CLAUDE.local.md", StringComparison.OrdinalIgnoreCase)
               || name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
               || name.Equals(".mcp.json", StringComparison.OrdinalIgnoreCase)
               || parts[..^1].Any(dir => dir.Equals(".claude", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drops the C-quotes and the a/ or b/ prefix of a patch path (enough to read its extension).</summary>
    private static string Unquote(string side)
    {
        if (side.Length >= 2 && side[0] == '"' && side[^1] == '"') side = side[1..^1];
        return side.StartsWith("a/", StringComparison.Ordinal) || side.StartsWith("b/", StringComparison.Ordinal) ? side[2..] : side;
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
