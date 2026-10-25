using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.Jira;

public sealed record JiraTrackerConfig(string Type, string TokenSecret, Uri Site, string Email, string Jql,
    string? ProjectKey = null) : TrackerConfig(Type, TokenSecret);

/// <summary>Jira Cloud: <c>tracker.type: jira</c> with <c>site</c>, <c>email</c> and <c>jql</c>.</summary>
public sealed partial class JiraProvider : ITrackerProvider
{
    public string Type => "jira";

    public string DisplayName => "Jira";

    public string HttpClientName => "jira";

    public TrackerConfig Parse(TrackerSection section)
    {
        var dto = section.Bind<Dto>();
        if (dto.ProjectKey is not null && !ProjectKeyPattern().IsMatch(dto.ProjectKey))
            throw new ProjectConfigException($"invalid tracker.projectKey '{dto.ProjectKey}'");
        return new JiraTrackerConfig(section.Type, section.TokenSecret, new Uri(section.Required(dto.Site, "site")),
            section.Required(dto.Email, "email"), section.Required(dto.Jql, "jql"), dto.ProjectKey);
    }

    public ITracker Create(ProjectConfig project, HttpClient http, string token, ILoggerFactory loggers) =>
        new JiraTracker(http, Config(project), token);

    public Uri? IssueUrl(ProjectConfig project, string key) =>
        new(Config(project).Site, "browse/" + Uri.EscapeDataString(key));

    private static JiraTrackerConfig Config(ProjectConfig project) =>
        project.Tracker as JiraTrackerConfig
        ?? throw new InvalidOperationException($"project '{project.Id}' tracker is not a Jira tracker");

    private sealed class Dto
    {
        public string? Site { get; set; }
        public string? Email { get; set; }
        public string? Jql { get; set; }
        public string? ProjectKey { get; set; }
    }

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{1,9}\z")]
    private static partial Regex ProjectKeyPattern();
}
