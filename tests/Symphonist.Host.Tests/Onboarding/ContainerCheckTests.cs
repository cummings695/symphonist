using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Onboarding;
using Symphonist.Host.Onboarding;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Onboarding;

[Collection(SymphonistHomeCollection.Name)]
public sealed class ContainerCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z");
    private static readonly string[] StepNames = ["Docker", "Agent image", "Build and tests in container"];

    private readonly TestHome home = new();
    private readonly FakeProcessRunner processes = new();
    private readonly FakeGitWorkspace git = new();
    private readonly FakeAgentRunner agent = new();
    private readonly string context;
    private readonly DockerLauncher docker;
    private readonly IsolationLauncher launcher;
    private readonly ContainerCheck check;
    private readonly ProjectConfig draft;

    public ContainerCheckTests()
    {
        context = Path.Combine(home.Path, "agent-context");
        Directory.CreateDirectory(context);
        File.WriteAllText(Path.Combine(context, "Dockerfile"), "FROM scratch\n");
        docker = new DockerLauncher(processes, context, _ => null);
        launcher = new IsolationLauncher(new HostLauncher(), docker);
        check = new ContainerCheck(docker, launcher, git,
            new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = agent }), new FakeTimeProvider(Now));
        draft = Draft(TestHome.ProjectYaml("orders"));
        // Docker answers and the default image is current, unless a test says otherwise.
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "image inspect" => new ProcessResult(0, hash, "", false),
            _ => new ProcessResult(0, "", "", false),
        };
        agent.OnRun = _ => Output(new
        {
            build_command = "dotnet build", build_passed = true, test_command = "dotnet test", tests_passed = true,
            summary = "all green",
        });
    }

    public void Dispose() => home.Dispose();

    private ProjectConfig Draft(string yaml) =>
        ProjectConfigLoader.Parse(yaml, home.Projects, "draft", BuiltInProviders.Registry);

    private static string Verb(ProcessSpec s) => string.Join(' ', s.Arguments.Take(2));

    private static AgentRunResult Output(object structured) =>
        new(true, null, 0.3m, TimeSpan.FromSeconds(30), null, """{"raw":"check"}""",
            JsonSerializer.SerializeToElement(structured));

    private static AgentRunRequest Request(AgentSettings settings) => new("/w", "p", settings);

    private sealed class RecordingProgress : IProgress<IReadOnlyList<CheckStep>>
    {
        public List<IReadOnlyList<CheckStep>> Reports { get; } = [];
        public void Report(IReadOnlyList<CheckStep> value) => Reports.Add(value);
    }

    [Fact]
    public async Task Host_isolation_skips_every_step()
    {
        var steps = await check.RunAsync(Draft(TestHome.ProjectYaml("orders", TestHome.HostAgent)), null,
            CancellationToken.None);

        Assert.Equal(StepNames, steps.Select(s => s.Name));
        Assert.All(steps, s =>
        {
            Assert.Equal(CheckStatus.Skipped, s.Status);
            Assert.Equal("host isolation: no container to check", s.Detail);
        });
        Assert.Empty(processes.Calls);
        Assert.Empty(agent.Requests);
        Assert.Empty(git.Events);
    }

    [Fact]
    public async Task Docker_unavailable_fails_first_step_and_skips_rest()
    {
        processes.OnRun = _ => new ProcessResult(1, "", "cannot connect", false);

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(new CheckStep("Docker", CheckStatus.Failed, "Docker is not running or not installed"), steps[0]);
        Assert.Equal(CheckStatus.Skipped, steps[1].Status);
        Assert.Equal(CheckStatus.Skipped, steps[2].Status);
        Assert.Equal("version --format", Verb(Assert.Single(processes.Calls)));
        Assert.Empty(agent.Requests);
        Assert.Empty(git.Events);
    }

    [Fact]
    public async Task Image_failure_disables_image_and_skips_build()
    {
        processes.OnRun = s => Verb(s) switch
        {
            "image inspect" => new ProcessResult(1, "", "No such image", false),
            "build -t" => new ProcessResult(1, "", "network down", false),
            _ => new ProcessResult(0, "", "", false),
        };

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Passed, steps[0].Status);
        Assert.Equal(CheckStatus.Failed, steps[1].Status);
        Assert.Contains("network down", steps[1].Detail);
        Assert.Equal(CheckStatus.Skipped, steps[2].Status);
        Assert.Empty(agent.Requests);
        Assert.Empty(git.Events);
        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request(draft.Agent)));
        Assert.Equal(steps[1].Detail, ex.Message);
    }

    [Fact]
    public async Task Packages_image_is_built_and_enabled()
    {
        var packaged = Draft(TestHome.ProjectYaml("orders", "type: claude-code\n  packages:\n    apt: [ripgrep]"));
        var image = DockerLauncher.ImageFor(packaged.Agent);
        Assert.StartsWith(DockerLauncher.PackagesRepository, image);
        launcher.DisableImage(image, "built at startup and failed");
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "image inspect" when s.Arguments.Contains("{{.Id}}") => new ProcessResult(0, "sha256:base", "", false),
            "image inspect" when s.Arguments.Contains(image) => new ProcessResult(1, "", "No such image", false),
            "image inspect" => new ProcessResult(0, hash, "", false),
            _ => new ProcessResult(0, "", "", false),
        };

        var steps = await check.RunAsync(packaged, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Passed, steps[1].Status);
        var build = Assert.Single(processes.Calls, s => s.Arguments[0] == "build");
        Assert.Contains(image, build.Arguments);
        Assert.Contains("'ripgrep'", build.StandardInput);
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(packaged.Agent)).FileName);
    }

    private bool Probed(string command) => processes.Calls.Any(s =>
        s.Arguments.Contains("--entrypoint") && s.Arguments[(s.Arguments.ToList().IndexOf("--entrypoint") + 1)] == command);

    [Fact]
    public async Task Container_check_verifies_each_cli_in_the_image()
    {
        var both = Draft(TestHome.ProjectYaml("orders") + "\nevidence:\n  agent: { type: codex }\n");
        Assert.Equal(["claude-code", "codex"], AgentTypes.All.Where(both.AgentTypesInUse.Contains));

        var steps = await check.RunAsync(both, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Passed, steps[1].Status);
        Assert.True(Probed("claude"));
        Assert.True(Probed("codex"));
    }

    [Fact]
    public async Task Missing_codex_cli_fails_the_image_step()
    {
        var both = Draft(TestHome.ProjectYaml("orders") + "\nevidence:\n  agent: { type: codex }\n");
        var hash = DockerLauncher.ContextHash(context);
        processes.OnRun = s => Verb(s) switch
        {
            "image inspect" => new ProcessResult(0, hash, "", false),
            "run --rm" when s.Arguments.Contains("codex") => new ProcessResult(127, "", "not found", false),
            _ => new ProcessResult(0, "", "", false),
        };

        var steps = await check.RunAsync(both, null, CancellationToken.None);

        Assert.Equal(new CheckStep("Agent image", CheckStatus.Failed, "image has no codex CLI; rebuild on symphonist/agent:2"),
            steps[1]);
        Assert.Equal(CheckStatus.Skipped, steps[2].Status);
        Assert.Empty(agent.Requests);
    }

    [Fact]
    public async Task Claude_only_project_does_not_probe_codex()
    {
        await check.RunAsync(draft, null, CancellationToken.None);

        Assert.True(Probed("claude"));
        Assert.False(Probed("codex"));
    }

    [Fact]
    public async Task Passing_check_enables_containers_and_image()
    {
        launcher.DisableContainers(IsolationLauncher.DockerNotAvailable);
        launcher.DisableImage(DockerLauncher.DefaultImage, "build failed at startup");

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.All(steps, s => Assert.Equal(CheckStatus.Passed, s.Status));
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(draft.Agent)).FileName);
    }

    [Fact]
    public async Task Build_runs_writable_in_fresh_clone_and_cleans_up()
    {
        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(["remove _container-check", "prepare _container-check", "remove _container-check"], git.Events);
        Assert.All(git.Removed, r => Assert.Equal(("orders", ContainerCheck.WorkspaceKey), r));
        Assert.Equal((ContainerCheck.WorkspaceKey, "main"), Assert.Single(git.Prepared));
        var request = Assert.Single(agent.Requests);
        Assert.False(request.ReadOnlyWorkspace);
        Assert.Equal(ContainerCheckSchema.Json, request.OutputSchema);
        Assert.Equal(draft.Agent, request.Settings);
        Assert.Equal(git.WorkspacePath, request.WorkingDirectory);
        Assert.Contains("Project orders", request.Prompt);
        Assert.Empty(git.Commits);
        Assert.Empty(git.Pushes);
        Assert.Equal(new CheckStep("Build and tests in container", CheckStatus.Passed,
            "build: dotnet build (passed); tests: dotnet test (passed). all green"), steps[2]);
        var log = Path.Combine(home.Path, "logs", "orders", "_onboarding", "container-check-20261025T100000Z.json");
        Assert.Equal("""{"raw":"check"}""", File.ReadAllText(log));
    }

    [Fact]
    public async Task Failing_tests_fail_the_build_step()
    {
        agent.OnRun = _ => Output(new
        {
            build_command = "dotnet build", build_passed = true, test_command = "dotnet test", tests_passed = false,
            summary = "2 failed",
        });

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Failed, steps[2].Status);
        Assert.Contains("tests: dotnet test (failed)", steps[2].Detail);
        Assert.Equal("remove _container-check", git.Events[^1]);
    }

    [Fact]
    public async Task No_tests_found_still_passes()
    {
        agent.OnRun = _ => Output(new { build_command = "make", build_passed = true, summary = "built" });

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(new CheckStep("Build and tests in container", CheckStatus.Passed,
            "build: make (passed); no tests found. built"), steps[2]);
    }

    [Fact]
    public async Task Failed_or_malformed_run_fails_the_build_step()
    {
        agent.OnRun = _ => FakeAgentRunner.Fail("timed out after 30 minutes");
        var failed = await check.RunAsync(draft, null, CancellationToken.None);

        agent.OnRun = _ => Output(new { summary = "no build" });
        var malformed = await check.RunAsync(draft, null, CancellationToken.None);

        agent.OnRun = _ => throw new InvalidOperationException("Docker not available");
        var thrown = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(new CheckStep("Build and tests in container", CheckStatus.Failed, "timed out after 30 minutes"), failed[2]);
        Assert.Equal(CheckStatus.Failed, malformed[2].Status);
        Assert.Contains("no usable structured output", malformed[2].Detail);
        Assert.Equal(new CheckStep("Build and tests in container", CheckStatus.Failed, "Docker not available"), thrown[2]);
        Assert.Equal(9, git.Events.Count);
    }

    [Fact]
    public async Task Stale_workspace_that_cannot_be_removed_fails_without_running()
    {
        git.RemoveError = new IOException("file in use");

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Failed, steps[2].Status);
        Assert.Contains("file in use", steps[2].Detail);
        Assert.Empty(git.Prepared);
        Assert.Empty(agent.Requests);
    }

    [Fact]
    public async Task Cleanup_failure_after_run_keeps_the_result()
    {
        var removals = 0;
        git.OnRemove = () => { if (++removals == 2) throw new IOException("file in use"); };

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Passed, steps[2].Status);
    }

    [Fact]
    public async Task Progress_reports_each_step()
    {
        var progress = new RecordingProgress();

        var steps = await check.RunAsync(draft, progress, CancellationToken.None);

        var running = progress.Reports
            .Select(r => r.FirstOrDefault(s => s.Status == CheckStatus.Running)?.Name)
            .Where(n => n is not null)
            .Distinct();
        Assert.Equal(StepNames, running);
        Assert.Equal(steps, progress.Reports[^1]);
        Assert.All(progress.Reports, r => Assert.Equal(StepNames, r.Select(s => s.Name)));
        Assert.Equal([CheckStatus.Running, CheckStatus.NotRun, CheckStatus.NotRun], progress.Reports[0].Select(s => s.Status));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_and_workspace_is_removed()
    {
        using var cts = new CancellationTokenSource();
        agent.OnRunCancellableAsync = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FakeAgentRunner.Fail("unreachable"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.RunAsync(draft, null, cts.Token));
        Assert.Equal("remove _container-check", git.Events[^1]);
    }

    [Fact]
    public async Task After_docker_down_start_a_check_enables_only_its_own_image()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        home.WriteProject("billing.yaml", TestHome.ProjectYaml("billing", image: "acme/agent:7"));
        var dockerUp = processes.OnRun;
        processes.OnRun = _ => new ProcessResult(1, "", "cannot connect", false);
        await new DockerStartup(new ProjectRegistry(NullLogger<ProjectRegistry>.Instance), docker, launcher,
            NullLogger<DockerStartup>.Instance).StartAsync(CancellationToken.None);
        processes.OnRun = dockerUp;

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.All(steps, s => Assert.Equal(CheckStatus.Passed, s.Status));
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(draft.Agent)).FileName);
        var billing = draft.Agent with { Image = "acme/agent:7" };
        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Wrap(new ProcessSpec("claude", []), Request(billing)));
        Assert.Equal(IsolationLauncher.ImageNotPrepared, ex.Message);
    }

    private Task StartupAsync() =>
        new DockerStartup(new ProjectRegistry(NullLogger<ProjectRegistry>.Instance), docker, launcher,
            NullLogger<DockerStartup>.Instance).StartAsync(CancellationToken.None);

    [Fact]
    public async Task After_docker_down_start_a_project_added_later_stays_refused_until_its_own_check()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var dockerUp = processes.OnRun;
        processes.OnRun = _ => new ProcessResult(1, "", "cannot connect", false);
        await StartupAsync();
        processes.OnRun = dockerUp;
        // billing is added after startup (hand edit + reload); orders' check passes.
        var billing = Draft(TestHome.ProjectYaml("billing", image: "acme/agent:7"));

        await check.RunAsync(draft, null, CancellationToken.None);

        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request(billing.Agent)));
        Assert.Equal(IsolationLauncher.ImageNotPrepared, ex.Message);
        await check.RunAsync(billing, null, CancellationToken.None);
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(billing.Agent)).FileName);
    }

    [Fact]
    public async Task Without_container_projects_at_start_a_project_added_later_is_refused_until_its_check()
    {
        home.WriteProject("hr.yaml", TestHome.ProjectYaml("hr", TestHome.HostAgent));
        await StartupAsync();
        Assert.Empty(processes.Calls);

        var ex = Assert.Throws<InvalidOperationException>(
            () => launcher.Wrap(new ProcessSpec("claude", []), Request(draft.Agent)));
        Assert.Equal(IsolationLauncher.ImageNotPrepared, ex.Message);
        await check.RunAsync(draft, null, CancellationToken.None);
        Assert.Equal("docker", launcher.Wrap(new ProcessSpec("claude", []), Request(draft.Agent)).FileName);
    }

    [Fact]
    public async Task Exception_details_are_one_bounded_line()
    {
        git.PrepareError = new InvalidOperationException("clone failed\n\u001b[31m" + new string('x', 2000));

        var steps = await check.RunAsync(draft, null, CancellationToken.None);

        Assert.Equal(CheckStatus.Failed, steps[2].Status);
        Assert.StartsWith("cannot prepare the repository: clone failed [31m", steps[2].Detail);
        Assert.True(steps[2].Detail.Length <= 500);
        Assert.DoesNotContain('\n', steps[2].Detail);
    }

    [Fact]
    public async Task Log_is_written_when_cancelled_right_after_the_run()
    {
        using var cts = new CancellationTokenSource();
        agent.OnRunCancellableAsync = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(FakeAgentRunner.Fail("cancelled"));
        };

        await check.RunAsync(draft, null, cts.Token);

        var log = Path.Combine(home.Path, "logs", "orders", "_onboarding", "container-check-20261025T100000Z.json");
        Assert.Equal("""{"raw":false}""", File.ReadAllText(log));
    }
}
