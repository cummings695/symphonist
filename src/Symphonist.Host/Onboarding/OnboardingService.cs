using Symphonist.Core;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Core.Onboarding;
using Symphonist.Core.Risk;
using Symphonist.Host.Orchestration;
using Symphonist.Integrations.Containers;

namespace Symphonist.Host.Onboarding;

/// <summary>
/// The state of one setup screen: the form being edited and the results of its checks. Held by
/// <see cref="OnboardingService"/>, so it survives a page reload. Holds no secret values.
/// </summary>
public sealed class OnboardingSession
{
    internal OnboardingSession(string? projectId, ProjectForm form, ProjectForm? original, string? existingFile,
        string? loadedText, DateTimeOffset now)
    {
        ProjectId = projectId;
        Form = form;
        Original = original;
        ExistingFile = existingFile;
        LoadedText = loadedText;
        LastUsed = now;
    }

    /// <summary>Null for a new project.</summary>
    public string? ProjectId { get; }

    /// <summary>Edited by the page.</summary>
    public ProjectForm Form { get; }

    /// <summary><see cref="ProjectForm.From"/> of the loaded config for an existing project; null for a new one.</summary>
    public ProjectForm? Original { get; }

    public string? ExistingFile { get; }

    public string? LoadedText { get; }

    public ConnectionReport? Connections { get; internal set; }

    public OnboardingProposal? Proposal { get; internal set; }

    public IReadOnlyList<CheckStep> ContainerSteps { get; internal set; } = [];

    /// <summary>"connections", "proposal", "container" or null.</summary>
    public string? RunningStep { get; internal set; }

    /// <summary>Why the last step did not run or did not finish; one bounded line.</summary>
    public string? LastError { get; internal set; }

    internal DateTimeOffset LastUsed { get; set; }

    internal bool Saving { get; set; }

    internal CancellationTokenSource? Cancellation { get; set; }

    internal bool Busy => RunningStep is not null || Saving;
}

/// <summary>
/// The single setup session of this host (one signed-in user): opens a new or existing project's form, runs the
/// connection checks, the proposal agent and the container check one at a time on the thread pool, writes secrets
/// straight to <see cref="ISecretStore"/> and saves through <see cref="ProjectFileStore"/>. Nothing reaches the projects
/// folder or the orchestrator before <see cref="SaveAsync"/>. Only one session is kept, and it is dropped after
/// <see cref="IdleTimeout"/> without use.
/// </summary>
public sealed class OnboardingService(ProjectRegistry registry, ProviderRegistry providers, SetupChecks checks,
    OnboardingProposer proposer, ContainerCheck container, ProjectFileStore files, ISecretStore secrets,
    IHostApplicationLifetime lifetime, TimeProvider? time = null, ILogger<OnboardingService>? log = null) : IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

    private const string Busy = "another setup step is running";
    private const string DraftSource = "setup draft";

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock sync = new();
    private OnboardingSession? current;

    /// <summary>The open session; null when none was opened, it was saved, or it expired.</summary>
    public OnboardingSession? Current
    {
        get
        {
            lock (sync)
            {
                DropIfExpired();
                return current;
            }
        }
    }

    /// <summary>Raised on every state change, from any thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts (or returns, when it is for the same project and unsaved) the session.
    /// </summary>
    /// <exception cref="InvalidOperationException">"another setup step is running" in a session for another project.</exception>
    /// <exception cref="KeyNotFoundException">Unknown project id.</exception>
    /// <exception cref="ProjectConfigException">The project's file no longer loads (edited by hand since the last reload).</exception>
    /// <exception cref="IOException">The project's file cannot be read.</exception>
    public OnboardingSession Open(string? projectId)
    {
        OnboardingSession session;
        lock (sync)
        {
            DropIfExpired();
            if (current is not null && current.ProjectId == projectId)
            {
                current.LastUsed = clock.GetUtcNow();
                return current;
            }
            if (current is { Busy: true }) throw new InvalidOperationException(Busy);
            session = projectId is null ? NewSession() : ExistingSession(projectId);
            current = session;
        }
        Changed?.Invoke();
        return session;
    }

    private OnboardingSession NewSession() =>
        // The developer enters the id; until then the draft does not load.
        new(null, ProjectForm.NewProject("", providers), null, null, null, clock.GetUtcNow());

    private OnboardingSession ExistingSession(string projectId)
    {
        registry.Get(projectId); // KeyNotFoundException for an unknown id
        var file = registry.FileOf(projectId) ?? throw new KeyNotFoundException($"Project '{projectId}' not found");
        // The text as it is now: the save is refused when it changes, so the form must match exactly this text.
        var text = File.ReadAllText(file);
        var config = ProjectConfigLoader.Parse(text, Path.GetDirectoryName(file)!, Path.GetFileName(file), providers);
        return new OnboardingSession(projectId, ProjectForm.From(config, providers), ProjectForm.From(config, providers),
            file, text, clock.GetUtcNow());
    }

    /// <summary>Builds the draft from the form; on a draft error, <see cref="OnboardingSession.LastError"/> is the loader's message.</summary>
    public Task CheckConnectionsAsync() => StartStep("connections", null, async (session, form, draft, ct) =>
        session.Connections = await checks.CheckAsync(draft, fullName => AdoptFullName(session, form, fullName), ct));

    /// <summary>Needs the tracker reached by the connection checks; else LastError "check connections first".</summary>
    public Task ProposeAsync() => StartStep("proposal",
        session => session.Connections?.Tracker is null ? "check connections first" : null,
        async (session, form, draft, ct) =>
            session.Proposal = await proposer.ProposeAsync(draft, form, session.Connections!.Tracker!, ct));

    public Task CheckContainerAsync() => StartStep("container", null, async (session, _, draft, ct) =>
    {
        session.ContainerSteps = [];
        session.ContainerSteps = await container.RunAsync(draft, new StepProgress(steps =>
        {
            session.ContainerSteps = steps;
            Changed?.Invoke();
        }), ct);
    });

    /// <summary>Cancels the running step, if any.</summary>
    public void Cancel()
    {
        lock (sync) current?.Cancellation?.Cancel();
    }

    /// <summary>Saves through <see cref="ProjectFileStore"/>; on success <see cref="Current"/> is null. Refused while a step runs.</summary>
    public async Task<ProjectSaveResult> SaveAsync()
    {
        OnboardingSession session;
        ProjectForm form;
        lock (sync)
        {
            session = current ?? throw new InvalidOperationException("no setup session is open");
            if (session.Busy) return new ProjectSaveResult(false, [$"{Busy}; cancel it or wait for it to finish"], null);
            session.Saving = true;
            session.LastUsed = clock.GetUtcNow();
            form = session.Form.Clone();
        }

        var saved = false;
        try
        {
            var result = await files.SaveAsync(new ProjectSaveRequest(form, session.Original, session.ExistingFile,
                session.LoadedText), lifetime.ApplicationStopping);
            saved = result.Saved;
            return result;
        }
        finally
        {
            lock (sync)
            {
                session.Saving = false;
                if (saved && current == session) current = null;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The distinct Secret-kind values of the tracker, repo and build sections, plus the agent token in container mode.
    /// </summary>
    public IReadOnlyList<string> SecretNamesInUse()
    {
        var form = Current?.Form;
        if (form is null) return [];
        var names = new List<string>();
        Add(form.Tracker, providers.AllTrackers.FirstOrDefault(p => p.Type == form.Tracker.Type)?.Fields);
        Add(form.Repo, providers.AllRepos.FirstOrDefault(p => p.Type == form.Repo.Type)?.Fields);
        Add(form.Build, providers.AllBuilds.FirstOrDefault(p => p.Type == form.Build.Type)?.Fields);
        if (form.Agent.Isolation == Isolation.Container) names.Add(DockerLauncher.TokenSecretName);
        return names.Distinct(StringComparer.Ordinal).ToList();

        void Add(ProviderSectionForm section, IReadOnlyList<ProviderField>? fields)
        {
            foreach (var field in fields?.Where(f => f.Kind == ProviderFieldKind.Secret) ?? [])
                if (section.Values.TryGetValue(field.Key, out var name) && !string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
        }
    }

    /// <summary>
    /// Writes the secret straight to <see cref="ISecretStore"/> (trimmed, as <c>symphonist secret set</c> does); the
    /// value is not kept, logged or put in the form.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid name or blank value.</exception>
    /// <exception cref="InvalidOperationException">The credential store refused it.</exception>
    public void SetSecret(string name, string value)
    {
        if (!SecretNames.IsValid(name)) throw new ArgumentException($"invalid secret name '{name}'", nameof(name));
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("the secret value must not be empty", nameof(value));
        try
        {
            secrets.Set(name, trimmed);
        }
        catch (Exception e)
        {
            // The store's message names the entry, never the value.
            throw new InvalidOperationException(Bounded($"could not save secret '{name}': {e.Message}"), e);
        }
        log?.LogInformation("Secret {Name} saved from the setup screen", name);
        Changed?.Invoke();
    }

    /// <summary>"Set", "Not set" or "Set by environment variable".</summary>
    public string SecretState(string name)
    {
        try
        {
            if (secrets.IsOverridden(name)) return "Set by environment variable";
            return secrets.Get(name) is null ? "Not set" : "Set";
        }
        catch (Exception e)
        {
            log?.LogWarning("Cannot read secret {Name} from the credential store: {Error}", name, e.Message);
            return "Not set";
        }
    }

    public void Dispose()
    {
        lock (sync) current?.Cancellation?.Cancel();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool with a draft built from a copy of the form taken now, so the
    /// draft and the form given to the step describe the same config whatever the page edits meanwhile.
    /// </summary>
    private Task StartStep(string name, Func<OnboardingSession, string?>? precondition,
        Func<OnboardingSession, ProjectForm, ProjectConfig, CancellationToken, Task> work)
    {
        OnboardingSession session;
        ProjectForm form = null!;
        ProjectConfig draft = null!;
        CancellationTokenSource? cancellation = null;
        lock (sync)
        {
            DropIfExpired();
            session = current ?? throw new InvalidOperationException("no setup session is open");
            session.LastUsed = clock.GetUtcNow();
            var refusal = session.Busy ? Busy : precondition?.Invoke(session) ?? TryDraft(session, out form, out draft);
            if (refusal is not null)
            {
                session.LastError = refusal;
            }
            else
            {
                session.RunningStep = name;
                session.LastError = null;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
                session.Cancellation = cancellation;
            }
        }
        Changed?.Invoke();
        if (cancellation is null) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await work(session, form, draft, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                session.LastError = "cancelled";
            }
            catch (Exception e)
            {
                log?.LogWarning(e, "Setup step {Step} failed", name);
                session.LastError = Bounded(e.Message);
            }
            finally
            {
                lock (sync)
                {
                    session.RunningStep = null;
                    session.Cancellation = null;
                    session.LastUsed = clock.GetUtcNow();
                }
                cancellation.Dispose();
                Changed?.Invoke();
            }
        });
    }

    /// <summary>The draft as <see cref="ProjectFileStore"/> would parse it; returns the error, or null.</summary>
    private string? TryDraft(OnboardingSession session, out ProjectForm form, out ProjectConfig draft)
    {
        form = session.Form.Clone();
        draft = null!;
        var errors = form.Validate(providers);
        if (errors.Count > 0) return Bounded(string.Join("; ", errors));
        try
        {
            var projects = Path.GetFullPath(SymphonistPaths.Resolve("projects"));
            draft = ProjectConfigLoader.Parse(ProjectYamlWriter.NewFile(form, providers), projects, DraftSource, providers);
            return null;
        }
        catch (Exception e) when (e is ProjectConfigException or ProjectYamlEditException)
        {
            return Bounded(e.Message);
        }
    }

    /// <summary>
    /// Writes the host's spelling into the form's full-name keys, unless the developer changed the repository type or
    /// name since the check started.
    /// </summary>
    private void AdoptFullName(OnboardingSession session, ProjectForm checkedForm, string fullName)
    {
        var slash = fullName.IndexOf('/');
        if (slash <= 0 || slash == fullName.Length - 1) return;
        var keys = providers.Repo(checkedForm.Repo.Type).FullNameKeys;
        if (keys.Count != 2) return;
        lock (sync)
        {
            var live = session.Form.Repo;
            if (live.Type != checkedForm.Repo.Type) return;
            if (keys.Any(k => !string.Equals(live.Values.GetValueOrDefault(k), checkedForm.Repo.Values.GetValueOrDefault(k),
                    StringComparison.Ordinal)))
                return;
            live.Values[keys[0]] = fullName[..slash];
            live.Values[keys[1]] = fullName[(slash + 1)..];
        }
    }

    private void DropIfExpired()
    {
        if (current is { Busy: false } session && clock.GetUtcNow() - session.LastUsed > IdleTimeout) current = null;
    }

    private static string Bounded(string text) => ReviewerText.Line(text, ReviewerText.MaxSummaryChars);

    /// <summary>Reports synchronously on the reporting thread (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class StepProgress(Action<IReadOnlyList<CheckStep>> report) : IProgress<IReadOnlyList<CheckStep>>
    {
        public void Report(IReadOnlyList<CheckStep> value) => report(value);
    }
}
