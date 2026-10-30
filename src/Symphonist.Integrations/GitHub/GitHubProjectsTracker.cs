using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.GitHub;

/// <summary>
/// A GitHub Projects (v2) board as a tracker, over GraphQL. A ticket is a board item whose content is an open issue
/// assigned to the token's user, carrying <see cref="GitHubProjectsTrackerConfig.Label"/>, in a pickup status and
/// (when a list is given) in one of the repositories. Key = <c>{repository name}-{issue number}</c>.
/// </summary>
/// <param name="pickupStatuses">Status values to pick up (compared ignoring case).</param>
/// <param name="requiredStatuses">Status values that must exist as options of the status field.</param>
/// <param name="repositories">"owner/repo" names to pick up (ignoring case), or null for any repository.</param>
public sealed class GitHubProjectsTracker(HttpClient http, GitHubProjectsTrackerConfig config, string token,
    IReadOnlyList<string> pickupStatuses, IReadOnlyList<string> requiredStatuses, IReadOnlyList<string>? repositories,
    ILogger? log = null) : ITracker
{
    private readonly GitHubApi _api = new(http, token);
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly SemaphoreSlim _viewerLock = new(1, 1);
    private string? _viewer;
    private volatile Snapshot? _snapshot;

    /// <summary>The board's ids from the latest successful query.</summary>
    /// <param name="OptionIds">Status option name to option id, keyed ignoring case.</param>
    internal sealed record Board(string ProjectId, string FieldId, IReadOnlyDictionary<string, string> OptionIds);

    /// <summary>The project item and issue behind a ticket key.</summary>
    internal sealed record ItemRef(string ItemId, string IssueId);

    internal sealed record Snapshot(Board Board, IReadOnlyDictionary<string, ItemRef> Items);

    /// <summary>Board ids and key map from the latest successful <see cref="QueryAsync"/>, or null before one.</summary>
    internal Snapshot? Latest => _snapshot;

    private string Name => $"{config.Owner}/{config.Project}";

    public async Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct)
    {
        var viewer = await GetViewerAsync(ct);
        var board = await GetBoardAsync(ct);

        var kept = new List<(string Key, string ItemId, string Status, JsonElement Issue)>();
        string? after = null;
        do
        {
            using var doc = await _api.GraphQlAsync(GitHubProjectsQueries.Items,
                Vars(("project", board.ProjectId), ("field", config.StatusField), ("after", after)), ct);
            var items = doc.RootElement.GetProperty("data").GetProperty("node").GetProperty("items");
            foreach (var item in items.GetProperty("nodes").EnumerateArray())
                if (Keep(item, viewer) is { } k)
                    kept.Add((k.Key, item.GetProperty("id").GetString()!, k.Status, item.GetProperty("content").Clone()));
            after = NextCursor(items);
        } while (after is not null);

        var refs = new Dictionary<string, ItemRef>(StringComparer.OrdinalIgnoreCase);
        var tickets = new List<SourceTicket>();
        foreach (var group in kept.GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                _log.LogWarning("GitHub project {Board}: items {Issues} all map to key {Key}; leaving them out",
                    Name, string.Join(", ", group.Select(g => g.Issue.GetProperty("url").GetString())), group.Key);
                continue;
            }

            var (key, itemId, status, issue) = group.Single();
            var issueId = issue.GetProperty("id").GetString()!;
            refs[key] = new ItemRef(itemId, issueId);
            tickets.Add(new SourceTicket(
                key,
                Text(issue, "title"),
                Text(issue, "body"),
                status,
                await GetCommentsAsync(issueId, viewer, ct),
                new Uri(issue.GetProperty("url").GetString()!)));
        }

        _snapshot = new Snapshot(board, refs);
        return tickets;
    }

    public async Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct)
    {
        var (board, item) = await ResolveAsync(key, ct);
        if (!board.OptionIds.TryGetValue(targetStatus, out var optionId)) return false;

        using var _ = await _api.GraphQlAsync(GitHubProjectsQueries.SetStatus,
            Vars(("project", board.ProjectId), ("item", item.ItemId), ("field", board.FieldId), ("option", optionId)), ct);
        return true;
    }

    public async Task CommentAsync(string key, string markdown, CancellationToken ct)
    {
        var (_, item) = await ResolveAsync(key, ct);
        using var __ = await _api.GraphQlAsync(GitHubProjectsQueries.AddComment,
            Vars(("subject", item.IssueId), ("body", markdown)), ct);
    }

    public Task DownloadAttachmentAsync(string key, SourceAttachment attachment, Stream destination, CancellationToken ct) =>
        throw new NotSupportedException("GitHub Projects tickets have no attachments.");

    /// <summary>The board and item behind a key; queries once when the key is not known yet.</summary>
    private async Task<(Board Board, ItemRef Item)> ResolveAsync(string key, CancellationToken ct)
    {
        if (_snapshot is { } known && known.Items.TryGetValue(key, out var item)) return (known.Board, item);
        await QueryAsync(ct);
        if (_snapshot is { } fresh && fresh.Items.TryGetValue(key, out item)) return (fresh.Board, item);
        throw new InvalidOperationException($"ticket {key} is not on GitHub project {Name}");
    }

    /// <summary>The item's key and status when it passes every filter, else null.</summary>
    private (string Key, string Status)? Keep(JsonElement item, string viewer) =>
        Match(item, viewer, config.Label, repositories) is { } m && m.Status.Length > 0
        && pickupStatuses.Contains(m.Status, StringComparer.OrdinalIgnoreCase)
            ? m : null;

    /// <summary>
    /// Every filter but the status: the item's key and status ("" when unset) when its content is an open issue
    /// assigned to <paramref name="viewer"/>, carrying <paramref name="label"/> and (when a list is given) in one of
    /// <paramref name="repositories"/>, else null. Shared by <see cref="QueryAsync"/> and discovery's ticket count.
    /// </summary>
    internal static (string Key, string Status)? Match(JsonElement item, string viewer, string label,
        IReadOnlyList<string>? repositories)
    {
        if (!item.TryGetProperty("content", out var issue) || issue.ValueKind != JsonValueKind.Object
            || Text(issue, "__typename") != "Issue" || Text(issue, "state") != "OPEN")
            return null;

        if (!Names(issue, "assignees", "login").Contains(viewer, StringComparer.OrdinalIgnoreCase)) return null;
        if (!Names(issue, "labels", "name").Contains(label, StringComparer.OrdinalIgnoreCase)) return null;

        var repository = issue.GetProperty("repository");
        if (repositories is not null
            && !repositories.Contains(Text(repository, "nameWithOwner"), StringComparer.OrdinalIgnoreCase))
            return null;

        var status = item.TryGetProperty("fieldValueByName", out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, "name") : "";
        return ($"{Text(repository, "name")}-{issue.GetProperty("number").GetInt32()}", status);
    }

    /// <summary>
    /// Read-only, for the setup screen: the viewer, the status field's options in board order (no category: GitHub
    /// has none) and the number of items <see cref="QueryAsync"/> would consider with the status filter left out.
    /// Never calls <see cref="QueryAsync"/>, which fails while the draft names statuses the board lacks.
    /// </summary>
    internal async Task<TrackerDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var viewer = await GetViewerAsync(ct);
        var board = $"{config.Owner} #{config.Project}";
        JsonDocument doc;
        try
        {
            doc = await _api.GraphQlAsync(GitHubProjectsQueries.Discover,
                Vars(("owner", config.Owner), ("number", config.Project), ("field", config.StatusField)), ct);
        }
        catch (GitHubGraphQlException ex) when (ex.ErrorTypes.Count > 0 && ex.ErrorTypes.All(t => t == "NOT_FOUND"))
        {
            throw new InvalidOperationException($"GitHub board {board} not found or not accessible", ex);
        }

        string projectId;
        var statuses = new List<SourceStatus>();
        using (doc)
        {
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("repositoryOwner", out var owner) || owner.ValueKind != JsonValueKind.Object
                || !owner.TryGetProperty("projectV2", out var project) || project.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"GitHub board {board} not found or not accessible");

            if (!project.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.Object
                || !field.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"board {board} has no single-select field '{config.StatusField}'");

            foreach (var option in options.EnumerateArray())
                if (Text(option, "name") is { Length: > 0 } name)
                    statuses.Add(new SourceStatus(name, StatusCategory.Unknown, Text(option, "description")));
            projectId = Text(project, "id");
        }

        return new TrackerDiscovery(viewer, $"GitHub board {board} ({config.StatusField})", statuses,
            await CountAsync(projectId, viewer, ct), ["GitHub columns have no category; the proposal infers it from names"]);
    }

    /// <summary>Items passing <see cref="Match"/>, or null when paging fails.</summary>
    private async Task<int?> CountAsync(string projectId, string viewer, CancellationToken ct)
    {
        try
        {
            var count = 0;
            string? after = null;
            do
            {
                using var doc = await _api.GraphQlAsync(GitHubProjectsQueries.Items,
                    Vars(("project", projectId), ("field", config.StatusField), ("after", after)), ct);
                var items = doc.RootElement.GetProperty("data").GetProperty("node").GetProperty("items");
                count += items.GetProperty("nodes").EnumerateArray()
                    .Count(item => Match(item, viewer, config.Label, repositories) is not null);
                after = NextCursor(items);
            } while (after is not null);
            return count;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<Board> GetBoardAsync(CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await _api.GraphQlAsync(GitHubProjectsQueries.Project,
                Vars(("owner", config.Owner), ("number", config.Project), ("field", config.StatusField)), ct);
        }
        catch (GitHubGraphQlException ex) when (ex.ErrorTypes.Count > 0 && ex.ErrorTypes.All(t => t == "NOT_FOUND"))
        {
            throw NotFound(ex);
        }

        using (doc)
        {
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("repositoryOwner", out var owner) || owner.ValueKind != JsonValueKind.Object
                || !owner.TryGetProperty("projectV2", out var project) || project.ValueKind != JsonValueKind.Object)
                throw NotFound(null);

            if (!project.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.Object
                || !field.TryGetProperty("id", out var fieldId) || !field.TryGetProperty("options", out var options))
                throw new InvalidOperationException(
                    $"GitHub project {Name} has no single-select field '{config.StatusField}'");

            var optionIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in options.EnumerateArray())
                optionIds.TryAdd(Text(option, "name"), Text(option, "id"));

            var missing = requiredStatuses.Where(s => !optionIds.ContainsKey(s)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"GitHub project {Name} field '{config.StatusField}' has no option(s): {string.Join(", ", missing)}");

            return new Board(project.GetProperty("id").GetString()!, fieldId.GetString()!, optionIds);
        }
    }

    private InvalidOperationException NotFound(Exception? inner) =>
        new($"GitHub project {Name} not found or not accessible", inner);

    private async Task<IReadOnlyList<SourceComment>> GetCommentsAsync(string issueId, string viewer, CancellationToken ct)
    {
        var comments = new List<SourceComment>();
        string? after = null;
        do
        {
            using var doc = await _api.GraphQlAsync(GitHubProjectsQueries.Comments,
                Vars(("issue", issueId), ("after", after)), ct);
            var page = doc.RootElement.GetProperty("data").GetProperty("node").GetProperty("comments");
            foreach (var c in page.GetProperty("nodes").EnumerateArray())
            {
                // A deleted account leaves a null author; GitHub shows it as "ghost".
                var login = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                    ? Text(a, "login") : "";
                if (login.Length == 0) login = "ghost";
                comments.Add(new SourceComment(
                    login,
                    string.Equals(login, viewer, StringComparison.OrdinalIgnoreCase),
                    DateTimeOffset.Parse(Text(c, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    Text(c, "body")));
            }

            after = NextCursor(page);
        } while (after is not null);

        return comments.OrderBy(c => c.Created).ToList();
    }

    private async Task<string> GetViewerAsync(CancellationToken ct)
    {
        if (_viewer is not null) return _viewer;
        await _viewerLock.WaitAsync(ct);
        try
        {
            if (_viewer is not null) return _viewer;
            using var doc = await _api.GraphQlAsync(GitHubProjectsQueries.Viewer, Vars(), ct);
            _viewer = doc.RootElement.GetProperty("data").GetProperty("viewer").GetProperty("login").GetString()!;
            return _viewer;
        }
        finally
        {
            _viewerLock.Release();
        }
    }

    private static string? NextCursor(JsonElement connection)
    {
        var info = connection.GetProperty("pageInfo");
        return info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
    }

    private static IEnumerable<string> Names(JsonElement issue, string connection, string field) =>
        issue.TryGetProperty(connection, out var c) && c.ValueKind == JsonValueKind.Object
        && c.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object).Select(n => Text(n, field))
            : [];

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static Dictionary<string, object?> Vars(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}
