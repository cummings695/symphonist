namespace Symphonist.Host.Tests;

/// <summary>A temporary SYMPHONIST_HOME (use only in <see cref="SymphonistHomeCollection"/> tests).</summary>
public sealed class TestHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "symphonist-tests-" + Guid.NewGuid().ToString("N"));

    public TestHome()
    {
        Directory.CreateDirectory(Projects);
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", Path);
    }

    public string Projects => System.IO.Path.Combine(Path, "projects");

    public void WriteProject(string fileName, string yaml) => File.WriteAllText(System.IO.Path.Combine(Projects, fileName), yaml);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }

    /// <summary>A valid project file; <paramref name="agent"/> is the YAML body of the <c>agent:</c> mapping.</summary>
    public static string ProjectYaml(string id, string agent = "type: claude-code", string? image = null) => $$"""
        id: {{id}}
        name: Project {{id}}
        tracker:
          type: jira
          site: https://acme.atlassian.net
          email: dev@example.com
          tokenSecret: jira-acme
          jql: assignee = currentUser() AND labels = Claude
        repo:
          type: bitbucket
          workspace: acme
          slug: {{id}}
          localPath: ../acme-{{id}}
          email: dev@example.com
          tokenSecret: bitbucket-acme
        build:
          type: bitbucket-pipelines
        agent:
          {{agent}}{{(image is null ? "" : $"\n  image: {image}")}}
        pipeline:
          - { id: triage,    kind: triage,    gate: human, entryStatuses: [Backlog], approvedWhenStatus: Planned }
          - { id: plan,      kind: plan,      gate: human, entryStatuses: [Planned], onEnterStatus: Planned, approvedWhenStatus: In Progress }
          - { id: implement, kind: implement, gate: human, onEnterStatus: In Progress, onGateStatus: In Review }
          - { id: done,      kind: done,      onEnterStatus: Done }
        """;

    public const string HostAgent = "type: claude-code\n  isolation: host\n  allowedTools: [Read]";
}
