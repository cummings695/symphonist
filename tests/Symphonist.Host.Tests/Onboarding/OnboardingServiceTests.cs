using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Host.Onboarding;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Tests.Onboarding;

[Collection(SymphonistHomeCollection.Name)]
public sealed class OnboardingServiceTests : IDisposable
{
    private static readonly ProviderRegistry Providers = BuiltInProviders.Registry;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z");

    private readonly TestHome home = new();
    private readonly FakeTicketStore tickets = new();
    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeProjectDiscovery discovery = new();
    private readonly FakeSecretStore secrets = new();
    private readonly FakeGitWorkspace git = new() { OriginUrl = "git@example.org:acme/orders.git" };
    private readonly FakeAgentRunner agent = new();
    private readonly FakeHostLifetime lifetime = new();
    private readonly FakeIntegrationFactory factory =
        new(new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
    private readonly ProjectRegistry registry;
    private readonly Orchestrator orchestrator;
    private readonly OnboardingService service;

    public OnboardingServiceTests()
    {
        registry = new ProjectRegistry(NullLogger<ProjectRegistry>.Instance);
        orchestrator = new Orchestrator(registry, tickets, factory,
            new StageExecutor(new FakeGitWorkspace(), new FakeAgentRunner(), tickets, time),
            new GateReviewer(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), tickets, time),
            new EvidenceRunner(new FakeGitWorkspace(), new AgentRunners(new Dictionary<string, IAgentRunner>()), tickets, time), time,
            new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false), NullLogger<Orchestrator>.Instance);
        var agents = new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = agent });
        var docker = new DockerLauncher(new FakeProcessRunner(), Path.Combine(home.Path, "agent-context"), _ => null);
        service = new OnboardingService(registry, Providers, new SetupChecks(discovery, Providers, secrets, git),
            new OnboardingProposer(git, agents, time),
            new ContainerCheck(docker, new IsolationLauncher(new HostLauncher(), docker), git, agents, time),
            new ProjectFileStore(registry, tickets, orchestrator, Providers), secrets, lifetime, time);
        discovery.Repository = new RepositoryDiscovery("acme/orders", "main", true);
    }

    public void Dispose()
    {
        service.Dispose();
        orchestrator.WhenIdleAsync().GetAwaiter().GetResult(); // the tick a save requests
        lifetime.Dispose();
        home.Dispose();
    }

    private string PathOf(string fileName) => Path.Combine(home.Projects, fileName);

    /// <summary>Writes project <paramref name="id"/> (repository acme/{id}) with a header comment and reloads; returns the text.</summary>
    private string WriteProject(string id, string tracker = "jira", string repo = "bitbucket")
    {
        var form = ProjectFileStoreTests.Form(id, tracker, repo);
        var keys = Providers.Repo(repo).FullNameKeys;
        form.Repo.Values[keys[0]] = "acme";
        form.Repo.Values[keys[1]] = id;
        var text = "# hand-written header\n" + ProjectYamlWriter.NewFile(form, Providers);
        home.WriteProject($"{id}.yaml", text);
        registry.Reload();
        return text;
    }

    private void SetSecretsOf(OnboardingSession session)
    {
        foreach (var name in service.SecretNamesInUse(session)) secrets.Set(name, "s3cret-value");
        Assert.NotEmpty(session.Form.Tracker.Values["tokenSecret"]);
    }

    private static AgentRunResult Proposal(object structured) =>
        new(true, null, 0.4m, TimeSpan.FromSeconds(5), null, """{"raw":"onboarding"}""",
            JsonSerializer.SerializeToElement(structured));

    /// <summary>Makes the next agent run wait until cancelled; the returned task completes when the run has started.</summary>
    private Task BlockAgent(Action? onCancelled = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunCancellableAsync = async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                onCancelled?.Invoke();
                throw;
            }
            return FakeAgentRunner.Fail("unreachable");
        };
        return started.Task;
    }

    [Theory]
    [InlineData("jira", "bitbucket")]
    [InlineData("github-projects", "github")]
    public void Open_existing_project_loads_form_and_text(string tracker, string repo)
    {
        var text = WriteProject("orders", tracker, repo);

        var session = service.Open("orders");

        Assert.Equal("orders", session.ProjectId);
        Assert.Equal(PathOf("orders.yaml"), session.ExistingFile);
        Assert.Equal(text, session.LoadedText);
        Assert.NotNull(session.Original);
        Assert.NotSame(session.Original, session.Form);
        Assert.Equal(ProjectYamlWriter.Sections(session.Original, Providers), ProjectYamlWriter.Sections(session.Form, Providers));
        Assert.Equal(tracker, session.Form.Tracker.Type);
        Assert.Null(session.RunningStep);
        Assert.Same(session, service.Current);
    }

    [Fact]
    public void Open_returns_the_same_session_until_another_project_is_opened()
    {
        WriteProject("orders");
        WriteProject("billing");

        var orders = service.Open("orders");
        orders.Form.Name = "edited, not saved";

        Assert.Same(orders, service.Open("orders"));
        var fresh = service.Open(null);
        Assert.Null(fresh.ProjectId);
        Assert.Null(fresh.Original);
        Assert.Null(fresh.ExistingFile);
        Assert.Equal(7, fresh.Form.Pipeline.Count);
        Assert.Same(fresh, service.Open(null));
        Assert.NotSame(orders, service.Open("orders"));
        Assert.Throws<KeyNotFoundException>(() => service.Open("nope"));
    }

    [Fact]
    public async Task Open_after_the_file_changed_on_disk_starts_a_fresh_session()
    {
        WriteProject("orders");
        var old = service.Open("orders");
        var changed = File.ReadAllText(PathOf("orders.yaml")) + "# hand edit\n";
        File.WriteAllText(PathOf("orders.yaml"), changed);

        var fresh = service.Open("orders");

        Assert.NotSame(old, fresh);
        Assert.Equal(changed, fresh.LoadedText);
        Assert.Same(fresh, service.Open("orders"));
        var e = await Assert.ThrowsAsync<OnboardingSessionEndedException>(() => service.SaveAsync(old));
        Assert.Equal("the file changed on disk; this setup was reloaded", e.Message);
    }

    [Fact]
    public void Idle_session_expires()
    {
        WriteProject("orders");
        var first = service.Open("orders");

        time.Advance(OnboardingService.IdleTimeout - TimeSpan.FromMinutes(1));
        Assert.Same(first, service.Open("orders"));
        time.Advance(OnboardingService.IdleTimeout - TimeSpan.FromMinutes(1));
        service.Touch(first); // editing the form counts as use
        time.Advance(OnboardingService.IdleTimeout - TimeSpan.FromMinutes(1));
        Assert.Same(first, service.Current);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(service.Current);
        var e = Assert.Throws<OnboardingSessionEndedException>(() => service.Touch(first));
        Assert.Equal("this setup expired after 8 hours without use; reload the page", e.Message);
        Assert.NotSame(first, service.Open("orders"));
    }

    [Theory]
    [InlineData("jira", "bitbucket", "workspace", "slug")]
    [InlineData("github-projects", "github", "owner", "repo")]
    public async Task Adopted_full_name_updates_provider_keys(string tracker, string repo, string ownerKey, string nameKey)
    {
        WriteProject("orders", tracker, repo);
        var session = service.Open("orders");
        SetSecretsOf(session);
        discovery.Repository = new RepositoryDiscovery("Acme/Orders", "main", true);

        await service.CheckConnectionsAsync(session);

        Assert.Null(session.LastError);
        Assert.NotNull(session.Connections);
        Assert.Equal("Acme/Orders", session.PendingFullName);
        Assert.Equal("acme", session.Form.Repo.Values[ownerKey]); // not touched off the page's thread

        Assert.True(service.ApplyPendingAdoption(session));

        Assert.Equal("Acme", session.Form.Repo.Values[ownerKey]);
        Assert.Equal("Orders", session.Form.Repo.Values[nameKey]);
        Assert.Null(session.PendingFullName);
        Assert.Equal("acme", session.Original!.Repo.Values[ownerKey]); // the loaded form is not touched
        Assert.False(service.ApplyPendingAdoption(session));
    }

    [Fact]
    public async Task Adoption_is_skipped_when_the_name_was_edited_since_the_check()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        SetSecretsOf(session);
        discovery.Repository = new RepositoryDiscovery("Acme/Orders", "main", true);
        await service.CheckConnectionsAsync(session);

        session.Form.Repo.Values["slug"] = "billing";

        Assert.False(service.ApplyPendingAdoption(session));
        Assert.Equal("acme", session.Form.Repo.Values["workspace"]);
        Assert.Equal("billing", session.Form.Repo.Values["slug"]);
    }

    [Fact]
    public async Task Proposal_is_refused_when_the_tracker_changed_since_the_check()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        SetSecretsOf(session);
        await service.CheckConnectionsAsync(session);

        session.Form.Tracker.Values["projectKey"] = "BILL";
        await service.ProposeAsync(session);

        Assert.Equal("the tracker changed since the connection check; check connections again", session.LastError);
        Assert.Empty(agent.Requests);

        session.Form.Tracker.Values.Remove("projectKey");
        session.Form.Name = "renamed"; // other sections do not matter
        agent.Results.Enqueue(Proposal(new { stages = Array.Empty<object>(), risk_areas = Array.Empty<object>() }));
        await service.ProposeAsync(session);

        Assert.Null(session.LastError);
        Assert.Single(agent.Requests);
    }

    [Fact]
    public async Task Replaced_session_cannot_act()
    {
        WriteProject("orders");
        WriteProject("billing");
        var orders = service.Open("orders");
        orders.Form.Name = "edited in the first tab";
        var billing = service.Open("billing");
        var billingText = File.ReadAllText(PathOf("billing.yaml"));
        var ordersText = File.ReadAllText(PathOf("orders.yaml"));

        var save = await Assert.ThrowsAsync<OnboardingSessionEndedException>(() => service.SaveAsync(orders));
        var check = await Assert.ThrowsAsync<OnboardingSessionEndedException>(() => service.CheckConnectionsAsync(orders));
        await Assert.ThrowsAsync<OnboardingSessionEndedException>(() => service.ProposeAsync(orders));
        await Assert.ThrowsAsync<OnboardingSessionEndedException>(() => service.CheckContainerAsync(orders));
        Assert.Throws<OnboardingSessionEndedException>(() => service.Touch(orders));

        Assert.Equal("this setup was replaced by another one; reload the page", save.Message);
        Assert.Equal(save.Message, check.Message);
        Assert.Equal(billingText, File.ReadAllText(PathOf("billing.yaml")));
        Assert.Equal(ordersText, File.ReadAllText(PathOf("orders.yaml")));
        Assert.Same(billing, service.Current);
        Assert.Equal(0, discovery.Calls);
    }

    [Fact]
    public async Task Saved_session_cannot_act()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        Assert.True((await service.SaveAsync(session)).Saved);

        var e = await Assert.ThrowsAsync<OnboardingSessionEndedException>(() => service.SaveAsync(session));
        Assert.Equal("this setup was saved; reload the page to edit the project again", e.Message);
    }

    [Fact]
    public async Task Steps_run_for_a_file_with_a_folded_jql()
    {
        var text = WriteProject("orders");
        Assert.Contains("  jql: acme\n", text);
        home.WriteProject("orders.yaml", text.Replace("  jql: acme\n", "  jql: >\n    project = ACME\n    AND labels = Claude\n"));
        registry.Reload();
        var session = service.Open("orders");
        SetSecretsOf(session);

        await service.CheckConnectionsAsync(session);

        Assert.Null(session.LastError);
        Assert.NotNull(session.Connections);
        Assert.Equal("project = ACME AND labels = Claude\n", session.Form.Tracker.Values["jql"]);
    }

    [Fact]
    public async Task Draft_error_is_shown_and_nothing_runs()
    {
        var session = service.Open(null);

        await service.CheckConnectionsAsync(session);

        Assert.StartsWith("setup draft: ", session.LastError);
        Assert.Null(session.Connections);
        Assert.Null(session.RunningStep);
        Assert.Equal(0, discovery.Calls);
    }

    [Fact]
    public async Task Second_step_while_one_runs_is_refused()
    {
        WriteProject("orders");
        WriteProject("billing");
        var session = service.Open("orders");
        SetSecretsOf(session);
        await service.CheckConnectionsAsync(session);
        var started = BlockAgent();
        var changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);

        var proposal = service.ProposeAsync(session);
        await started;
        Assert.Equal("proposal", session.RunningStep);

        await service.CheckContainerAsync(session);
        Assert.Equal("another setup step is running", session.LastError);
        Assert.Equal("proposal", session.RunningStep);
        Assert.Empty(session.ContainerSteps);
        await service.CheckConnectionsAsync(session);
        Assert.Equal("proposal", session.RunningStep);
        var opened = Assert.Throws<InvalidOperationException>(() => service.Open("billing"));
        Assert.Equal("another setup step is running", opened.Message);
        Assert.Same(session, service.Open("orders"));
        var save = await service.SaveAsync(session);
        Assert.False(save.Saved);
        Assert.Equal(["another setup step is running; cancel it or wait for it to finish"], save.Errors);
        Assert.Single(agent.Requests);

        service.Cancel(session);
        await proposal;
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task Propose_requires_connections_first()
    {
        WriteProject("orders");
        var session = service.Open("orders");

        await service.ProposeAsync(session);

        Assert.Equal("check connections first", session.LastError);
        Assert.Null(session.Proposal);
        Assert.Empty(agent.Requests);
    }

    [Fact]
    public async Task Proposal_uses_the_checked_tracker_and_the_form_as_it_was()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        SetSecretsOf(session);
        await service.CheckConnectionsAsync(session);
        agent.Results.Enqueue(Proposal(new
        {
            stages = new[] { new { id = "plan", approved_when_status = "In Progress", reason = "next column" } },
            risk_areas = Array.Empty<object>(),
        }));

        await service.ProposeAsync(session);

        Assert.Null(session.LastError);
        Assert.Null(session.Proposal!.Error);
        Assert.Equal("In Progress", Assert.Single(session.Proposal.Stages).ApprovedWhenStatus);
        Assert.Contains("Jira project ACME", Assert.Single(agent.Requests).Prompt);
    }

    [Fact]
    public async Task Cancel_stops_running_step()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        SetSecretsOf(session);
        await service.CheckConnectionsAsync(session);
        var cancelled = false;
        var started = BlockAgent(() => cancelled = true);

        var proposal = service.ProposeAsync(session);
        await started;
        service.Cancel(session);
        await proposal;

        Assert.True(cancelled);
        Assert.Null(session.RunningStep);
        Assert.Equal("cancelled", session.LastError);
        Assert.Null(session.Proposal);
    }

    [Fact]
    public async Task Cancelled_step_reports_cancelled_whatever_the_dependency_throws()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        SetSecretsOf(session);
        await service.CheckConnectionsAsync(session);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnRunCancellableAsync = async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
            throw new InvalidOperationException("docker: container killed");
        };

        var proposal = service.ProposeAsync(session);
        await started.Task;
        service.Cancel(session);
        await proposal;

        Assert.Equal("cancelled", session.LastError);
        Assert.Null(session.RunningStep);
    }

    [Fact]
    public async Task Host_stopping_cancels_running_step()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        SetSecretsOf(session);
        await service.CheckConnectionsAsync(session);
        var started = BlockAgent();

        var proposal = service.ProposeAsync(session);
        await started;
        lifetime.StopApplication();
        await proposal;

        Assert.Null(session.RunningStep);
    }

    [Fact]
    public void Set_secret_validates_name_and_value()
    {
        Assert.Throws<ArgumentException>(() => service.SetSecret("Bad Name", "value"));
        // A token pasted into the name field is not echoed back.
        var pasted = Assert.Throws<ArgumentException>(() => service.SetSecret("ghp_PastedToken123", "value"));
        Assert.DoesNotContain("ghp_PastedToken123", pasted.Message);
        Assert.StartsWith("invalid secret name; use lowercase letters, digits and '-'", pasted.Message);
        Assert.Throws<ArgumentException>(() => service.SetSecret("jira-orders", ""));
        Assert.Throws<ArgumentException>(() => service.SetSecret("jira-orders", "   "));
        Assert.Empty(secrets.Values);

        service.SetSecret("jira-orders", " s3cret\n");

        Assert.Equal("s3cret", secrets.Values["jira-orders"]);
    }

    [Fact]
    public void Secret_state_reports_environment_override()
    {
        secrets.Set("bitbucket-orders", "x");
        secrets.Overridden.Add("jira-orders");

        Assert.Equal("Set by environment variable", service.SecretState("jira-orders"));
        Assert.Equal("Set", service.SecretState("bitbucket-orders"));
        Assert.Equal("Not set", service.SecretState("github-orders"));
    }

    [Fact]
    public void Secret_names_in_use_are_distinct_and_include_agent_token_in_container_mode()
    {
        WriteProject("orders", "github-projects", "github");
        var session = service.Open("orders");

        Assert.Equal(["github-orders", DockerLauncher.TokenSecretName], service.SecretNamesInUse(session));

        session.Form.Agent.Isolation = Isolation.Host;
        Assert.Equal(["github-orders"], service.SecretNamesInUse(session));
    }

    [Fact]
    public async Task Nothing_is_written_until_save()
    {
        var text = WriteProject("orders");
        var projectsBefore = Directory.GetFiles(home.Projects).Order().ToList();
        var loadedBefore = registry.Projects;
        var session = service.Open("orders");
        SetSecretsOf(session);
        agent.Results.Enqueue(Proposal(new
        {
            stages = new[] { new { id = "plan", approved_when_status = "In Progress", reason = "next column" } },
            risk_areas = new[] { new { name = "payments", description = "Money", paths = new[] { "src/Payments/**" }, reason = "r" } },
        }));

        await service.CheckConnectionsAsync(session);
        await service.ProposeAsync(session);
        session.Proposal!.ApplyTo(session.Form, new HashSet<string>());

        Assert.Equal(projectsBefore, Directory.GetFiles(home.Projects).Order().ToList());
        Assert.Equal(text, File.ReadAllText(PathOf("orders.yaml")));
        Assert.Same(loadedBefore, registry.Projects);
        Assert.Equal(0, factory.Created);

        var saved = await service.SaveAsync(session);

        Assert.True(saved.Saved, string.Join("; ", saved.Errors));
        Assert.Null(service.Current);
        Assert.Equal("payments", Assert.Single(registry.Get("orders").RiskAreas).Name);
        Assert.StartsWith("# hand-written header\n", File.ReadAllText(PathOf("orders.yaml")));
        Assert.DoesNotContain("s3cret-value", File.ReadAllText(PathOf("orders.yaml")));
    }

    [Fact]
    public async Task Refused_save_keeps_the_session()
    {
        WriteProject("orders");
        var session = service.Open("orders");
        session.Form.Pipeline.Add(new StageForm { Id = "plan", Kind = StageKind.Plan });

        var result = await service.SaveAsync(session);

        Assert.False(result.Saved);
        Assert.Same(session, service.Current);
    }
}
