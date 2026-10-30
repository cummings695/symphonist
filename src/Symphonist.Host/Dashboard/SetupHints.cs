using Symphonist.Core.Onboarding;

namespace Symphonist.Host.Dashboard;

/// <summary>
/// The setup screen's provider-specific hints, by provider type: the only place the setup components name a provider.
/// Everything else on the screen comes from the providers' field lists and discovery results.
/// </summary>
public static class SetupHints
{
    /// <summary>
    /// Jira: fills a blank <c>jql</c> from the project key, as
    /// <c>project = {KEY} AND assignee = currentUser() AND labels = Claude AND statusCategory != Done</c>.
    /// Other trackers are left alone.
    /// </summary>
    public static void PrefillTracker(ProviderSectionForm tracker)
    {
        if (tracker.Type != "jira"
            || tracker.Values.GetValueOrDefault("projectKey") is not { } key || string.IsNullOrWhiteSpace(key)
            || !string.IsNullOrWhiteSpace(tracker.Values.GetValueOrDefault("jql")))
            return;
        tracker.Values["jql"] =
            $"project = {key.Trim()} AND assignee = currentUser() AND labels = {ProjectForm.DefaultLabel} AND statusCategory != Done";
    }

    /// <summary>
    /// What else a pipeline status the tracker lacks breaks; null when nothing beyond the status itself. GitHub Projects
    /// checks every pipeline status against the board's options on each query.
    /// </summary>
    public static string? MissingStatusEffect(string trackerType) =>
        trackerType == "github-projects" ? "the board query fails until it exists" : null;
}
