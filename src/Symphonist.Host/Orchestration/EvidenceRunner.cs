using System.Globalization;
using System.Text;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Evidence;
using Symphonist.Core.Integrations;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Prompts;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Host.Orchestration;

public enum EvidenceStatus { NotNeeded, AlreadyGathered, Gathered, Failed }

/// <param name="Head">The pushed branch head the evidence is about; null when it could not be resolved.</param>
/// <param name="Screenshots">Stored paths relative to evidence/{project}/{key}/, e.g. "20261025T101500Z/home.png".</param>
/// <param name="Error">Why the run failed; set only for <see cref="EvidenceStatus.Failed"/>.</param>
public sealed record EvidenceOutcome(EvidenceStatus Status, string? Head, EvidenceReport? Report,
    IReadOnlyList<string> Screenshots, string? Error, string Message);

/// <summary>
/// Gathers evidence (regressions, test coverage, an app walkthrough) for an implement round: a fresh, writable agent
/// in a throwaway clone of the pushed ticket branch, removed before and after the run. Symphonist never commits or
/// pushes from it. Only screenshots are copied out, and only plain, bounded image files. Evidence never approves or
/// blocks anything; the caller records and posts the outcome.
/// </summary>
public sealed class EvidenceRunner(IGitWorkspace git, IAgentRunners agents, ITicketStore store, TimeProvider time,
    ILogger<EvidenceRunner>? log = null)
{
    /// <summary>The folder in the workspace the agent writes to; screenshots go to its <c>screenshots</c> folder.</summary>
    public const string OutputFolder = ".symphonist-evidence";
    public const int MaxScreenshots = 20;
    public const long MaxScreenshotBytes = 5 * 1024 * 1024;

    private const string ScreenshotFolder = "screenshots";
    private const string OutputDir = OutputFolder + "/" + ScreenshotFolder;

    private const string RunNotConfigured =
        "(not configured: work out how to start the app from the repository and report run_source \"guessed\")";
    private const string FeaturesNotConfigured =
        "(not configured: work out which features are major from the repository and the diff, and report their source \"guessed\")";

    /// <summary>The evidence workspace key; no tracker key starts with "_".</summary>
    public static string WorkspaceKey(string ticketKey) => "_evidence-" + ticketKey;

    /// <summary>Never throws except <see cref="OperationCanceledException"/> for <paramref name="ct"/>; removes its workspace before and after.</summary>
    public async Task<EvidenceOutcome> GatherAsync(ProjectConfig p, Ticket t, SourceTicket? source, CancellationToken ct)
    {
        var branch = t.Branch ?? $"symphonist/{t.Key}";
        foreach (var name in new[] { branch, p.Repo.MainBranch })
            if (!GateReviewer.IsValidBranchName(name)) return Failed(null, $"invalid branch name '{name}'");

        var key = WorkspaceKey(t.Key);
        try
        {
            return await RunAsync(p, t, source, branch, key, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(e, "Evidence for {Project}/{Ticket} failed", p.Id, t.Key);
            return Failed(null, e.Message);
        }
        finally
        {
            // Not kept between runs; a leftover is removed again before the next one.
            try
            {
                await git.RemoveAsync(p, key, CancellationToken.None);
            }
            catch (Exception e)
            {
                log?.LogWarning(e, "Could not remove the evidence workspace of {Project}/{Ticket}", p.Id, t.Key);
            }
        }
    }

    private async Task<EvidenceOutcome> RunAsync(ProjectConfig p, Ticket t, SourceTicket? source, string branch,
        string key, CancellationToken ct)
    {
        // Always a fresh clone at the pushed head: whatever an earlier run left behind is gone.
        await git.RemoveAsync(p, key, ct);
        var workspace = await git.PrepareAsync(p, key, branch, ct);
        // The branch was written by the implement agent: anything it committed in the output folder must not pass for
        // the evidence agent's own screenshots.
        ClearOutputFolder(workspace);

        // Full ref names: a tag or local branch called "origin/main" must not shadow the remote-tracking branch.
        var baseSha = await git.ResolveCommitAsync(workspace, $"refs/remotes/origin/{p.Repo.MainBranch}", ct);
        if (baseSha is null) return Failed(null, $"cannot resolve origin/{p.Repo.MainBranch}");
        var head = await git.ResolveCommitAsync(workspace, $"refs/remotes/origin/{branch}", ct);
        if (head is null) return Failed(null, $"branch not pushed: origin/{branch}");
        var range = $"{baseSha}...{head}";
        var changed = await git.ChangedFilesAsync(workspace, range, ct);

        var needed = p.Evidence.Mode switch
        {
            EvidenceMode.Always => true,
            EvidenceMode.HighRisk => t.Risk == RiskLevel.High || RiskAreaMatcher.Touched(p.RiskAreas, changed).Count > 0,
            _ => false,
        };
        if (!needed)
            return new(EvidenceStatus.NotNeeded, head, null, [], null, $"no evidence needed for {Short(head)}: not high risk");

        // One evidence run per head: a stored report for this commit is reused.
        if ((await store.EvidenceAsync(t.Id, ct)).FirstOrDefault(r => r.Head == head && r.ReportJson is not null) is { } done)
            return new(EvidenceStatus.AlreadyGathered, head, done.Report, done.Screenshots, null,
                $"evidence for {Short(head)} already gathered");

        var diff = await git.DiffAsync(workspace, range, ct);
        var prompt = Prompt(p, t, source, branch, range, changed, diff);

        var startedAt = time.GetUtcNow();
        AgentRunResult result;
        try
        {
            // Writable: the agent builds, runs tests and starts the app. The clone is discarded afterwards.
            result = await agents.For(p.Evidence.Agent.Type).RunAsync(new AgentRunRequest(workspace, prompt,
                p.Evidence.Agent, EvidenceSchema.Json, ReadOnlyWorkspace: false), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The runner itself failed (e.g. missing container image): still record the attempt.
            result = new AgentRunResult(false, null, null, time.GetUtcNow() - startedAt, e.Message, "");
        }

        // Structured output may be present on a failed run: only a successful run's output is read.
        var report = result.Succeeded ? EvidenceParser.Parse(result.Structured) : null;
        var error = !result.Succeeded ? result.Error ?? "evidence run failed"
            : report is null ? "the agent returned no usable evidence report"
            : null;

        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var logPath = SymphonistPaths.Resolve("logs", p.Id, t.Key, $"{t.StageId}-evidence-{stamp}.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            // Not cancellable: a cancel right after the run must not lose its log.
            await File.WriteAllTextAsync(logPath, result.RawLog, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning(e, "Could not write the evidence log {Path}", logPath);
        }
        var summary = error ?? report!.Headline.FirstOrDefault() ?? "evidence gathered";
        await store.AddAgentRunAsync(new AgentRunRecord(t.Id, t.StageId, startedAt, result.Duration, result.Succeeded,
            result.CostUsd, result.Error, logPath, summary, IsEvidence: true), CancellationToken.None);

        if (report is null) return Failed(head, error!);

        var copied = CopyScreenshots(p, t, workspace, stamp);
        var names = copied.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        report = report with
        {
            Walkthrough = report.Walkthrough with
            {
                Features = [.. report.Walkthrough.Features.Select(f => f with
                {
                    Steps = [.. f.Steps.Select(s => s.Screenshot is null || names.Contains(s.Screenshot) ? s : s with { Screenshot = null })],
                })],
            },
        };
        return new(EvidenceStatus.Gathered, head, report, copied, null, $"evidence for {Short(head)} gathered");
    }

    private static string Prompt(ProjectConfig p, Ticket t, SourceTicket? source, string branch, string range,
        IReadOnlyList<string> changed, string diff)
    {
        // A random fence per run: text inside cannot guess it, and forged markers inside are neutralised.
        var nonce = UntrustedFence.NewNonce();
        string Fence(string text) => UntrustedFence.Wrap(nonce, text);

        var values = PromptValues.For(p, t, source, branch, feedback: "");
        foreach (var name in new[] { "ticket.title", "ticket.description", "ticket.comments", "ticket.url", "branch" })
            values[name] = Fence(values[name] ?? "");
        foreach (var name in new[] { "spec_path", "plan_path" })
            if (values[name] is { Length: > 0 } path) values[name] = Fence(path);
        values["evidence.fence_id"] = nonce;
        values["evidence.diff_range"] = range;
        values["evidence.changed_files"] = changed.Count == 0 ? "(no changed files)" : Fence(string.Join("\n", changed));
        values["evidence.diff"] = diff.Length > GateReviewer.MaxDiffChars
            ? $"NOTE: the diff is {diff.Length} characters and was truncated to the first {GateReviewer.MaxDiffChars}; inspect the workspace for the rest.\n"
              + Fence(diff[..GateReviewer.MaxDiffChars])
            : Fence(diff);
        values["risk.areas"] = GateReviewer.RiskAreasText(p.RiskAreas);
        // Configured by the developer in the project file: trusted, not fenced.
        values["evidence.run"] = p.Evidence.Run.IsEmpty ? RunNotConfigured : RunText(p.Evidence.Run);
        values["evidence.features"] = p.Evidence.Features.Count == 0 ? FeaturesNotConfigured : FeaturesText(p.Evidence.Features);
        values["evidence.output_dir"] = OutputDir;
        return PromptRenderer.Render(PromptLibrary.GetEvidence(p), values);
    }

    private static string RunText(EvidenceRun run)
    {
        var sb = new StringBuilder();
        if (run.Setup.Count > 0)
        {
            sb.Append("setup:\n");
            foreach (var command in run.Setup) sb.Append("  - ").Append(command).Append('\n');
        }
        void Field(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.Append(name).Append(": ").Append(Indented(value)).Append('\n');
        }
        Field("start", run.Start);
        Field("url", run.Url);
        Field("ready", run.Ready);
        Field("notes", run.Notes);
        return sb.ToString().TrimEnd('\n');
    }

    private static string FeaturesText(IReadOnlyList<EvidenceFeature> features) =>
        string.Join("\n", features.Select(f => $"- name: {f.Name}\n  steps: {Indented(f.Steps)}"));

    /// <summary>Continuation lines of a multi-line value, indented under their key.</summary>
    private static string Indented(string value) =>
        string.Join("\n    ", value.Replace("\r\n", "\n").Split('\n'));

    /// <summary>
    /// Copies plain image files from the workspace's screenshot folder (not links, not subfolders, names the report
    /// may reference, at most <see cref="MaxScreenshotBytes"/> each, the first <see cref="MaxScreenshots"/> by name)
    /// to <c>evidence/{project}/{key}/{stamp}/</c>. Returns the stored paths relative to <c>evidence/{project}/{key}/</c>.
    /// </summary>
    private List<string> CopyScreenshots(ProjectConfig p, Ticket t, string workspace, string stamp)
    {
        var copied = new List<string>();
        try
        {
            var output = new DirectoryInfo(Path.Combine(workspace, OutputFolder));
            var folder = new DirectoryInfo(Path.Combine(output.FullName, ScreenshotFolder));
            // A linked folder could point anywhere on the machine.
            if (!folder.Exists || IsLink(output) || IsLink(folder)) return copied;

            var candidates = folder.EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
                })
                .Where(f => !IsLink(f) && EvidenceParser.IsScreenshotName(f.Name) && f.Length <= MaxScreenshotBytes)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Take(MaxScreenshots)
                .ToList();
            if (candidates.Count == 0) return copied;

            var destination = SymphonistPaths.Resolve("evidence", p.Id, t.Key, stamp);
            Directory.CreateDirectory(destination);
            foreach (var file in candidates)
            {
                var target = Path.Combine(destination, file.Name);
                try
                {
                    if (CopyBounded(file.FullName, target)) copied.Add($"{stamp}/{file.Name}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.LogWarning(e, "Could not copy evidence screenshot {File}", file.FullName);
                    TryDelete(target);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning(e, "Could not read the evidence screenshots of {Project}/{Ticket}", p.Id, t.Key);
        }
        return copied;
    }

    /// <summary>Copies at most <see cref="MaxScreenshotBytes"/>; a file that has grown past it is not kept.</summary>
    private static bool CopyBounded(string source, string target)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var outputStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxScreenshotBytes) break;
                outputStream.Write(buffer, 0, read);
            }
            if (total <= MaxScreenshotBytes) return true;
        }
        TryDelete(target);
        return false;
    }

    /// <summary>
    /// Deletes <see cref="OutputFolder"/> from the checkout. A link (or a file) at that path is deleted itself, never
    /// followed. Throws when it cannot be removed: evidence is not gathered over planted files.
    /// </summary>
    private static void ClearOutputFolder(string workspace)
    {
        var path = Path.Combine(workspace, OutputFolder);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }  // the entry itself, not a link's target
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).LinkTarget is not null)
        {
            // Remove the link only. Directory links need a directory delete on Windows; either way, never recursive.
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Directory.Delete(path); }
        }
        else if (attributes.HasFlag(FileAttributes.Directory))
            Directory.Delete(path, recursive: true);  // does not follow links inside the folder
        else
            File.Delete(path);

        if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException($"could not clear {OutputFolder} in the evidence workspace");
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static EvidenceOutcome Failed(string? head, string reason) =>
        new(EvidenceStatus.Failed, head, null, [], reason,
            head is null ? $"evidence failed: {reason}" : $"evidence for {Short(head)} failed: {reason}");

    private static string Short(string head) => head.Length > 12 ? head[..12] : head;
}
