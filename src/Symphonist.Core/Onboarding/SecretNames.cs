using System.Text.RegularExpressions;

namespace Symphonist.Core.Onboarding;

/// <summary>Names of secrets in the secret store; project files hold these names, never the values.</summary>
public static partial class SecretNames
{
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,63}\z")]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);

    /// <summary>The name a new project starts with, e.g. <c>jira-orders</c>.</summary>
    public static string Default(string httpClientName, string id) =>
        $"{httpClientName}-{id.ToLowerInvariant().Replace('_', '-')}";
}
