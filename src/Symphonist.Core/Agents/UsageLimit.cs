using Symphonist.Core.Config;

namespace Symphonist.Core.Agents;

/// <summary>A provider's usage or rate limit that stopped a run.</summary>
/// <param name="ResetsAt">When the provider says it lets runs through again; null when it did not say.</param>
public sealed record UsageLimit(DateTimeOffset? ResetsAt)
{
    /// <summary>How long a rate limit (HTTP 429) is assumed to last: those carry no reset time worth parsing.</summary>
    public static readonly TimeSpan RateLimitPause = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The first moment after <paramref name="now"/> at which the wall clock in <paramref name="zone"/> reads
    /// <paramref name="hour"/>:<paramref name="minute"/> (24-hour), today or tomorrow there.
    /// </summary>
    public static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeZoneInfo zone, int hour, int minute)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTimeOffset candidate = default;
        for (var day = 0; day <= 2; day++)
        {
            var wall = DateTime.SpecifyKind(today.AddDays(day).AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (candidate > now) break;
        }
        return candidate;
    }
}

/// <summary>Which usage limit a job draws on: jobs with the same key stop and resume together.</summary>
public static class UsageLimits
{
    /// <summary>"claude-code" for every Claude job, "codex:chatgpt" or "codex:api-key:{secret}" for Codex, by sign-in.</summary>
    public static string KeyFor(AgentSettings settings) =>
        settings.Type != AgentTypes.Codex ? AgentTypes.ClaudeCode
        : settings.Codex.Auth == CodexAuth.ChatGpt ? "codex:chatgpt"
        : $"codex:api-key:{settings.Codex.ApiKeySecret}";

    /// <summary>The provider's display name for a <see cref="KeyFor"/> key.</summary>
    public static string ProviderName(string key) =>
        key.StartsWith(AgentTypes.Codex, StringComparison.Ordinal) ? "Codex" : "Claude";

    /// <summary>Every key some job of the project may run under: the project agent, stage overrides, reviewer and evidence agent.</summary>
    public static IReadOnlySet<string> KeysInUse(ProjectConfig project)
    {
        var keys = new HashSet<string>
        {
            KeyFor(project.Agent), KeyFor(project.Review.Agent), KeyFor(project.Evidence.Agent),
        };
        foreach (var stage in project.Pipeline)
            if (stage.Agent is not null) keys.Add(KeyFor(stage.Agent));
        return keys;
    }
}
