using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Risk;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Onboarding;

/// <param name="Steps">The eight check steps in order.</param>
/// <param name="Tracker">Null when the tracker was not reached.</param>
/// <param name="Repository">Null when the repository was not reached.</param>
/// <param name="Build">Null when the build host was not reached.</param>
public sealed record ConnectionReport(IReadOnlyList<CheckStep> Steps, TrackerDiscovery? Tracker, RepositoryDiscovery? Repository,
    BuildDiscovery? Build);

/// <summary>
/// The setup screen's connection checks for a draft project: secrets, then read-only discovery of the tracker, the
/// repository and the build host (<see cref="IProjectDiscovery"/>), then the local checkout's origin. Details are one
/// bounded line each and never hold secret values.
/// </summary>
/// <param name="codexHome">The developer's Codex home, where <c>codex login</c> leaves <c>auth.json</c>.</param>
public sealed class SetupChecks(IProjectDiscovery discovery, ProviderRegistry providers, ISecretStore secrets, IGitWorkspace git,
    string codexHome)
{
    private static string NeedsSecret(string name) => $"needs secret {name}";

    /// <summary>
    /// Steps in order: "Secrets", "Tracker sign-in", "Tracker statuses", "Tracker query", "Repository",
    /// "Production branch", "Build", "Local checkout". Never throws for failed checks (failed steps instead); honours ct.
    /// </summary>
    /// <param name="draft">A config that passed <see cref="ProjectConfigLoader.Parse"/>.</param>
    /// <param name="adoptFullName">Called when the host's repository name differs from the draft's only by case.</param>
    public async Task<ConnectionReport> CheckAsync(ProjectConfig draft, Action<string>? adoptFullName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var t = providers.Tracker(draft.Tracker.Type).DisplayName;
        var r = providers.Repo(draft.Repo.Type).DisplayName;
        var b = providers.Build(draft.Build.Type).DisplayName;
        var steps = new List<CheckStep>();
        TrackerDiscovery? tracker = null;
        RepositoryDiscovery? repository = null;
        BuildDiscovery? build = null;

        // A missing secret skips only the checks that need it (claude-code-token is needed by none of them).
        var missing = MissingSecrets(draft);
        steps.Add(missing.Count > 0
            ? Failed("Secrets", $"not set: {string.Join(", ", missing)}; enter them above")
            : Passed("Secrets", "all set"));

        if (UsesChatGptSignIn(draft)) steps.Add(CodexSignInStep());

        if (missing.Contains(draft.Tracker.TokenSecret))
        {
            foreach (var name in (string[])["Tracker sign-in", "Tracker statuses", "Tracker query"])
                steps.Add(new CheckStep(name, CheckStatus.Skipped, NeedsSecret(draft.Tracker.TokenSecret)));
        }
        else
        {
            try
            {
                tracker = await discovery.DiscoverTrackerAsync(draft, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // HTTP and GraphQL errors, and board/field problems the provider reports as InvalidOperationException.
                steps.Add(Failed("Tracker sign-in", $"{t}: {e.Message}"));
            }
            if (tracker is null)
            {
                steps.Add(new CheckStep("Tracker statuses", CheckStatus.Skipped, "needs tracker sign-in"));
                steps.Add(new CheckStep("Tracker query", CheckStatus.Skipped, "needs tracker sign-in"));
            }
            else
            {
                steps.Add(Passed("Tracker sign-in", $"{t}: signed in as {tracker.AccountName}"));
                steps.Add(tracker.StatusSource is { } source
                    ? Passed("Tracker statuses", $"{tracker.Statuses.Count} statuses from {source}")
                    : new CheckStep("Tracker statuses", CheckStatus.Skipped,
                        Bounded(tracker.Notes.FirstOrDefault() ?? "no status source")));
                steps.Add(Passed("Tracker query",
                    tracker.MatchingTickets is { } count ? $"{count} tickets match" : "count unavailable"));
            }
        }

        if (missing.Contains(draft.Repo.TokenSecret))
        {
            foreach (var name in (string[])["Repository", "Production branch", "Build"])
                steps.Add(new CheckStep(name, CheckStatus.Skipped, NeedsSecret(draft.Repo.TokenSecret)));
        }
        else
        {
            try
            {
                repository = await discovery.DiscoverRepositoryAsync(draft, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                steps.Add(Failed("Repository", $"{r}: {e.Message}"));
                steps.Add(new CheckStep("Production branch", CheckStatus.Skipped, "needs the repository"));
            }
            if (repository is not null)
            {
                steps.Add(RepositoryStep(draft, repository, r, adoptFullName));
                var production = draft.Repo.ProductionBranch;
                steps.Add(repository.ProductionBranchExists
                    ? Passed("Production branch", $"branch '{production}' found on {r}")
                    : Failed("Production branch", $"branch '{production}' not found on {r}"));
            }

            try
            {
                build = await discovery.DiscoverBuildAsync(draft, ct);
                var notes = build.Notes.Count == 0 ? "" : "; " + string.Join("; ", build.Notes);
                steps.Add(Passed("Build", $"{b}: {build.Summary}{notes}"));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                steps.Add(Failed("Build", $"{b}: {e.Message}"));
            }
        }

        steps.Add(await LocalCheckoutStepAsync(draft, ct));
        return new ConnectionReport(steps, tracker, repository, build);
    }

    /// <summary>The distinct secrets the draft needs that are not set, in the order the form names them.</summary>
    private List<string> MissingSecrets(ProjectConfig draft)
    {
        var used = draft.AgentTypesInUse;
        List<string> names = [draft.Tracker.TokenSecret, draft.Repo.TokenSecret];
        if (used.Contains(AgentTypes.ClaudeCode) && draft.Agent.Isolation == Isolation.Container)
            names.Add(DockerLauncher.TokenSecretName);
        if (used.Contains(AgentTypes.Codex) && draft.Agent.Codex.Auth == CodexAuth.ApiKey)
            names.Add(draft.Agent.Codex.ApiKeySecret);
        return names.Distinct(StringComparer.Ordinal).Where(name => !IsSet(name)).ToList();
    }

    private static bool UsesChatGptSignIn(ProjectConfig draft) =>
        draft.AgentTypesInUse.Contains(AgentTypes.Codex) && draft.Agent.Codex.Auth == CodexAuth.ChatGpt;

    /// <summary>Only that <c>auth.json</c> is a readable regular file; its contents are never read.</summary>
    private CheckStep CodexSignInStep()
    {
        const string name = "Codex sign-in";
        try
        {
            var path = Path.Combine(codexHome, "auth.json");
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory))
                return Failed(name, "not signed in to ChatGPT; run `codex login`");
            using (info.OpenRead()) { }
            return Passed(name, "signed in to ChatGPT");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(name, "cannot read the Codex sign-in; run `codex login`");
        }
    }

    private bool IsSet(string name)
    {
        try
        {
            return secrets.Get(name) is not null;
        }
        catch (Exception)
        {
            // An unreadable credential store counts as not set: the remote checks could not use the secret either.
            return false;
        }
    }

    private static CheckStep RepositoryStep(ProjectConfig draft, RepositoryDiscovery found, string r,
        Action<string>? adoptFullName)
    {
        var expected = draft.Repo.FullName;
        if (!string.Equals(found.FullName, expected, StringComparison.OrdinalIgnoreCase))
            return Failed("Repository", $"{r} names it {found.FullName}, expected {expected}");

        var detail = $"{r}: {found.FullName}, main branch {draft.Repo.MainBranch}";
        if (!string.Equals(found.FullName, expected, StringComparison.Ordinal))
        {
            adoptFullName?.Invoke(found.FullName);
            detail += $"; the form now uses {r}'s spelling";
        }
        if (!string.Equals(found.MainBranch, draft.Repo.MainBranch, StringComparison.Ordinal))
            detail += $"; {r}'s main branch is {found.MainBranch}";
        return Passed("Repository", detail);
    }

    private async Task<CheckStep> LocalCheckoutStepAsync(ProjectConfig draft, CancellationToken ct)
    {
        const string name = "Local checkout";
        var path = draft.Repo.LocalPath;
        string? origin;
        try
        {
            origin = await git.OriginUrlAsync(path, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Failed(name, $"cannot read the origin of {path}: {e.Message}");
        }
        if (origin is null) return Failed(name, $"not a git checkout with an origin: {path}");

        var expected = draft.Repo.FullName;
        return NamesRepository(origin, expected)
            ? Passed(name, $"origin is {origin}")
            : Failed(name, $"origin is {origin}, expected {expected}");
    }

    /// <summary>
    /// True when <paramref name="origin"/> ends with the repository (ignoring case, a trailing '/' and ".git"), after a
    /// '/' or ':' so that <c>acme/orders-old</c> does not count as <c>acme/orders</c>.
    /// </summary>
    private static bool NamesRepository(string origin, string fullName)
    {
        var url = origin.Trim().Replace('\\', '/').TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        if (!url.EndsWith(fullName, StringComparison.OrdinalIgnoreCase) || url.Length == fullName.Length) return false;
        return url[^(fullName.Length + 1)] is '/' or ':';
    }

    private static CheckStep Passed(string name, string detail) => new(name, CheckStatus.Passed, Bounded(detail));

    private static CheckStep Failed(string name, string detail) => new(name, CheckStatus.Failed, Bounded(detail));

    /// <summary>Discovery and exception text as one bounded line for the screen.</summary>
    private static string Bounded(string text) => ReviewerText.Line(text, ReviewerText.MaxSummaryChars);
}
