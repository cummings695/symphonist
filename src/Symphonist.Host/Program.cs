using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Http.Resilience;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Storage;
using Symphonist.Host.Api;
using Symphonist.Host.Dashboard;
using Symphonist.Host.Onboarding;
using Symphonist.Host.Orchestration;
using Symphonist.Integrations;
using Symphonist.Integrations.ClaudeCode;
using Symphonist.Integrations.Codex;
using Symphonist.Integrations.Containers;
using Symphonist.Integrations.Git;
using Symphonist.Integrations.Processes;
using Symphonist.Integrations.Secrets;
using Symphonist.Integrations.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Local API: loopback only. Endpoints from code replace ASPNETCORE_URLS / launchSettings / --urls, and the empty
// configuration drops any "Kestrel:Endpoints" from appsettings; the bound addresses are verified after start.
var port = ApiPort(Environment.GetEnvironmentVariable("SYMPHONIST_PORT"));
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Configure(new ConfigurationBuilder().Build());
    kestrel.Listen(IPAddress.Loopback, port);
});

Directory.CreateDirectory(SymphonistPaths.Home);
var apiToken = ApiToken.LoadOrCreate(SymphonistPaths.Resolve("api-token"));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Tracker and source-control writes are not idempotent (comments, transitions, PRs, merges): replace the default
// resilience handler from ServiceDefaults with one that retries only safe methods. RemoveAllResilienceHandlers is the
// documented way to drop handlers added by ConfigureHttpClientDefaults (still marked experimental).
foreach (var client in BuiltInProviders.Registry.HttpClientNames)
{
#pragma warning disable EXTEXP0001
    builder.Services.AddHttpClient(client)
        .RemoveAllResilienceHandlers()
        .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
#pragma warning restore EXTEXP0001
}

// Room for OrchestratorService to cancel agent runs and wait for their containers to be removed.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = OrchestratorService.RunDrainTimeout + TimeSpan.FromSeconds(15));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new OrchestratorOptions());
// SYMPHONIST_SECRET_STORE: os (default), memory or file.
builder.Services.AddSingleton(SecretStores.FromEnvironment());
builder.Services.AddSingleton(BuiltInProviders.Registry);
builder.Services.AddSingleton<IIntegrationFactory, IntegrationFactory>();
builder.Services.AddSingleton<IProjectDiscovery, ProjectDiscovery>();
builder.Services.AddSingleton<BoardNotifier>();
builder.Services.AddSingleton(_ => new SqliteTicketStore(SymphonistPaths.Resolve("symphonist.db")));
builder.Services.AddSingleton<ITicketStore>(sp =>
    new NotifyingTicketStore(sp.GetRequiredService<SqliteTicketStore>(), sp.GetRequiredService<BoardNotifier>()));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IGitWorkspace>(sp =>
    new GitWorkspace(sp.GetRequiredService<IProcessRunner>(), SymphonistPaths.Resolve("work"),
        sp.GetRequiredService<ILogger<GitWorkspace>>()));
builder.Services.AddSingleton(sp => new DockerLauncher(sp.GetRequiredService<IProcessRunner>(),
    Path.Combine(AppContext.BaseDirectory, "Containers", "agent"),
    name => sp.GetRequiredService<ISecretStore>().Get(name)));
builder.Services.AddSingleton(sp => new IsolationLauncher(new HostLauncher(), sp.GetRequiredService<DockerLauncher>()));
builder.Services.AddSingleton<IAgentRunner>(sp =>
    new ClaudeCodeRunner(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IsolationLauncher>()));
builder.Services.AddSingleton<IAgentRunners>(sp => new AgentRunners(new Dictionary<string, IAgentRunner>
{
    [AgentTypes.ClaudeCode] = sp.GetRequiredService<IAgentRunner>(),
    // Run files hold per-run copies of the Codex auth, so they stay under the Symphonist home, not a shared temp dir.
    [AgentTypes.Codex] = new CodexRunner(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IsolationLauncher>(),
        CodexRunner.DefaultCodexHome(), SymphonistPaths.Resolve("run")),
}));
builder.Services.AddSingleton<StageExecutor>();
builder.Services.AddSingleton<GateReviewer>();
builder.Services.AddSingleton<EvidenceRunner>();
builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddSingleton<ProjectFileStore>();
builder.Services.AddSingleton<OnboardingProposer>();
builder.Services.AddSingleton<ContainerCheck>();
builder.Services.AddSingleton<SetupChecks>();
builder.Services.AddSingleton<OnboardingService>();
// Order matters: Docker is prepared (or marked unavailable) before the orchestrator's first tick.
builder.Services.AddHostedService<DockerStartup>();
builder.Services.AddHostedService<OrchestratorService>();
builder.Services.AddDashboard(port);

var app = builder.Build();

var notifier = app.Services.GetRequiredService<BoardNotifier>();
app.Services.GetRequiredService<Orchestrator>().ProblemsChanged += notifier.Notify;
var registry = app.Services.GetRequiredService<ProjectRegistry>();
registry.PausedChanged += notifier.Notify;
registry.ProjectsChanged += notifier.Notify;

app.UseMiddleware<ApiTokenMiddleware>(apiToken);
app.MapDefaultEndpoints();
app.MapTicketsApi();
app.MapDashboard();

app.Lifetime.ApplicationStarted.Register(() => VerifyLoopbackOnly(app, port));

app.Run();

static int ApiPort(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return 5199;
    return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
        ? p
        : throw new InvalidOperationException($"SYMPHONIST_PORT '{value}' is not a valid port");
}

// Defense in depth: the API must never be reachable from another machine.
static void VerifyLoopbackOnly(WebApplication app, int port)
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
    if (addresses is null || addresses.Count == 0) return; // in-memory test server

    var expected = $"http://127.0.0.1:{port}";
    if (addresses.All(a => a.TrimEnd('/') == expected))
    {
        app.Logger.LogInformation("Symphonist API listening on {Url}", expected);
        return;
    }
    app.Logger.LogCritical("Refusing to serve: bound to {Addresses}, expected only {Expected}",
        string.Join(", ", addresses), expected);
    app.Lifetime.StopApplication();
}

/// <summary>Entry point (public for <c>WebApplicationFactory&lt;Program&gt;</c> in tests).</summary>
public partial class Program;
