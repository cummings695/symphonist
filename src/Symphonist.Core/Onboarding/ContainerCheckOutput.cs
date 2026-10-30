using System.Text.Json;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Onboarding;

public enum CheckStatus { NotRun, Running, Passed, Failed, Skipped }

/// <summary>One step of the container check as the setup screen shows it; <see cref="Detail"/> is plain text.</summary>
public sealed record CheckStep(string Name, CheckStatus Status, string Detail);

/// <summary>What the container check agent reports. <see cref="TestCommand"/> and <see cref="TestsPassed"/> are null when no tests ran.</summary>
public sealed record ContainerCheckOutput(string BuildCommand, bool BuildPassed, string? TestCommand, bool? TestsPassed,
    string Summary);

public static class ContainerCheckSchema
{
    public const string Json = """{"type":"object","properties":{"build_command":{"type":"string"},"build_passed":{"type":"boolean"},"test_command":{"type":"string"},"tests_passed":{"type":"boolean"},"summary":{"type":"string"}},"required":["build_command","build_passed","summary"]}""";
}

/// <summary>Reads the container check agent's structured output; all text is made one-line and bounded.</summary>
public static class ContainerCheckParser
{
    /// <summary>Null when <paramref name="structured"/> is missing or does not match <see cref="ContainerCheckSchema"/>.</summary>
    public static ContainerCheckOutput? Parse(JsonElement? structured)
    {
        if (structured is not { ValueKind: JsonValueKind.Object } root) return null;
        if (!root.TryGetProperty("build_command", out var build) || build.ValueKind != JsonValueKind.String) return null;
        if (!root.TryGetProperty("build_passed", out var buildPassed) || !IsBool(buildPassed)) return null;
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String) return null;

        string? testCommand = null;
        if (root.TryGetProperty("test_command", out var test) && test.ValueKind != JsonValueKind.Null)
        {
            if (test.ValueKind != JsonValueKind.String) return null;
            testCommand = ReviewerText.Line(test.GetString(), ReviewerText.MaxItemChars);
            if (testCommand.Length == 0) testCommand = null;
        }
        bool? testsPassed = null;
        if (root.TryGetProperty("tests_passed", out var passed) && passed.ValueKind != JsonValueKind.Null)
        {
            if (!IsBool(passed)) return null;
            testsPassed = passed.GetBoolean();
        }

        return new ContainerCheckOutput(ReviewerText.Line(build.GetString(), ReviewerText.MaxItemChars),
            buildPassed.GetBoolean(), testCommand, testsPassed,
            ReviewerText.Line(summary.GetString(), ReviewerText.MaxSummaryChars));
    }

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
