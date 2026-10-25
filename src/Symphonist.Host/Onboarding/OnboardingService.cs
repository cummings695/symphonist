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

    /// <summary>Edited by the page, on the page's thread only.</summary>
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

    /// <summary>
    /// The host's spelling of the repository name, found by the last connection check and not applied yet
    /// (see <see cref="OnboardingService.ApplyPendingAdoption"/>).
    /// </summary>
    public string? PendingFullName => PendingAdoption?.FullName;

    internal PendingAdoption? PendingAdoption { get; set; }

    /// <summary>The tracker section the last connection check ran with, as the writer renders it.</summary>
    internal string? CheckedTracker { get; set; }

    /// <summary>Why the session is no longer current; null while it is.</summary>
    internal string? Ended { get; set; }

    internal DateTimeOffset LastUsed { get; set; }

    internal bool Saving { get; set; }

    internal CancellationTokenSource? Cancellation { get; set; }

    internal bool Busy => RunningStep is not null || Saving;
}

/// <summary>A repository name to adopt, with the repository type and full-name values the check ran with.</summary>
internal sealed record PendingAdoption(string FullName, string RepoType, IReadOnlyDictionary<string, string?> CheckedValues);

/// <summary>
/// The session passed in was replaced by another one, saved, or expired; the page must reload. The message says which.
/// </summary>
public sealed class OnboardingSessionEndedException(string message) : InvalidOperationException(message);

/// <summary>
/// The single setup session of this host (one signed-in user): opens a new or existing project's form, runs the
/// connection checks, the proposal agent and the container check one at a time on the thread pool, writes secrets
/// straight to <see cref="ISecretStore"/> and saves through <see cref="ProjectFileStore"/>. Nothing reaches the projects
/// folder or the orchestrator before <see cref="SaveAsync"/>. Only one session is kept, and it is dropped after
/// <see cref="IdleTimeout"/> without use (see <see cref="Touch"/>). Actions take the caller's session and throw
/// <see cref="OnboardingSessionEndedException"/> when it is no longer the current one, so a stale tab never acts on
/// another tab's form.
/// </summary>
public sealed class OnboardingService(ProjectRegistry registry, ProviderRegistry providers, SetupChecks checks,
    OnboardingProposer proposer, ContainerCheck container, ProjectFileStore files, ISecretStore secrets,
    IHostApplicationLifetime lifetime, TimeProvider? time = null, ILogger<OnboardingService>? log = null) : IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

    private const string Busy = "another setup step is running";
    private const string Replaced = "this setup was replaced by another one; reload the page";
    private const string Expired = "this setup expired after 8 hours without use; reload the page";
    private const string Saved = "this setup was saved; reload the page to edit the project again";
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
            if (current is not null) current.Ended = Replaced;
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

    /// <summary>
    /// Builds the draft from the form; on a draft error, <see cref="OnboardingSession.LastError"/> is the loader's
    /// message. A repository name the host spells differently (case only) becomes
    /// <see cref="OnboardingSession.PendingFullName"/>, for the page to apply with <see cref="ApplyPendingAdoption"/>.
    /// </summary>
    /// <exception cref="OnboardingSessionEndedException">The session is no longer the current one.</exception>
    public Task CheckConnectionsAsync(OnboardingSession session) => StartStep(session, "connections", null,
        async (form, draft, ct) =>
        {
            var report = await checks.CheckAsync(draft, fullName => RecordAdoption(session, form, fullName), ct);
            lock (sync)
            {
                session.Connections = report;
                session.CheckedTracker = TrackerSection(form);
            }
        });

    /// <summary>
    /// Needs the tracker reached by the connection checks, with the tracker section unchanged since; else LastError
    /// "check connections first" or "the tracker changed since the connection check; check connections again".
    /// </summary>
    /// <exception cref="OnboardingSessionEndedException">The session is no longer the current one.</exception>
    public Task ProposeAsync(OnboardingSession session) => StartStep(session, "proposal",
        form => session.Connections?.Tracker is null ? "check connections first"
            : session.CheckedTracker != TrackerSection(form)
                ? "the tracker changed since the connection check; check connections again"
                : null,
        async (form, draft, ct) =>
            session.Proposal = await proposer.ProposeAsync(draft, form, session.Connections!.Tracker!, ct));

    /// <exception cref="OnboardingSessionEndedException">The session is no longer the current one.</exception>
    public Task CheckContainerAsync(OnboardingSession session) => StartStep(session, "container", null,
        async (_, draft, ct) =>
        {
            session.ContainerSteps = [];
            session.ContainerSteps = await container.RunAsync(draft, new StepProgress(steps =>
            {
                session.ContainerSteps = steps;
                Changed?.Invoke();
            }), ct);
        });

    /// <summary>Cancels the session's running step, if any.</summary>
    public void Cancel(OnboardingSession session)
    {
        lock (sync) session.Cancellation?.Cancel();
    }

    /// <summary>
    /// Marks the session used (the page calls it on form edits), so editing without running a step does not expire it.
    /// </summary>
    /// <exception cref="OnboardingSessionEndedException">The session is no longer the current one.</exception>
    public void Touch(OnboardingSession session)
    {
        lock (sync)
        {
            EnsureCurrent(session);
            session.LastUsed = clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Writes <see cref="OnboardingSession.PendingFullName"/> into the form's full-name keys, unless the developer
    /// changed the repository type or name since the check ran. Call it on the page's own thread: the form is not
    /// thread-safe. True when the form changed.
    /// </summary>
    public bool ApplyPendingAdoption(OnboardingSession session)
    {
        PendingAdoption? pending;
        lock (sync)
        {
            pending = session.PendingAdoption;
            session.PendingAdoption = null;
        }
        if (pending is null) return false;
        var live = session.Form.Repo;
        if (live.Type != pending.RepoType
            || pending.CheckedValues.Any(e => !string.Equals(live.Values.GetValueOrDefault(e.Key), e.Value, StringComparison.Ordinal)))
            return false;
        var keys = pending.CheckedValues.Keys.ToList();
        var slash = pending.FullName.IndexOf('/');
        live.Values[keys[0]] = pending.FullName[..slash];
        live.Values[keys[1]] = pending.FullName[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// Saves through <see cref="ProjectFileStore"/>; on success <see cref="Current"/> is null. Refused while a step runs.
    /// </summary>
    /// <exception cref="OnboardingSessionEndedException">The session is no longer the current one.</exception>
    public async Task<ProjectSaveResult> SaveAsync(OnboardingSession session)
    {
        ProjectForm form;
        lock (sync)
        {
            EnsureCurrent(session);
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
                if (saved && current == session)
                {
                    session.Ended = Saved;
                    current = null;
                }
            }
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The distinct Secret-kind values of the session's tracker, repo and build sections, plus the agent token in
    /// container mode.
    /// </summary>
    public IReadOnlyList<string> SecretNamesInUse(OnboardingSession session)
    {
        var form = session.Form;
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
        // The name is not quoted: a token pasted into the name field must not be echoed back.
        if (!SecretNames.IsValid(name))
            throw new ArgumentException("invalid secret name; use lowercase letters, digits and '-'", nameof(name));
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
    private Task StartStep(OnboardingSession session, string name, Func<ProjectForm, string?>? precondition,
        Func<ProjectForm, ProjectConfig, CancellationToken, Task> work)
    {
        ProjectForm form = null!;
        ProjectConfig draft = null!;
        CancellationTokenSource? cancellation = null;
        lock (sync)
        {
            EnsureCurrent(session);
            session.LastUsed = clock.GetUtcNow();
            var refusal = session.Busy ? Busy : TryDraft(session, out form, out draft) ?? precondition?.Invoke(form);
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
                await work(form, draft, cancellation.Token);
            }
            catch (Exception e)
            {
                // After a cancel, whatever the dependency threw (not always OperationCanceledException) means "cancelled".
                if (cancellation.IsCancellationRequested)
                {
                    session.LastError = "cancelled";
                }
                else
                {
                    log?.LogWarning(e, "Setup step {Step} failed", name);
                    session.LastError = Bounded(e.Message);
                }
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
    /// Records the host's spelling for <see cref="ApplyPendingAdoption"/>; the form itself is left to the page's thread.
    /// </summary>
    private void RecordAdoption(OnboardingSession session, ProjectForm checkedForm, string fullName)
    {
        var slash = fullName.IndexOf('/');
        if (slash <= 0 || slash == fullName.Length - 1) return;
        var keys = providers.Repo(checkedForm.Repo.Type).FullNameKeys;
        if (keys.Count != 2) return;
        var values = keys.ToDictionary(k => k, k => checkedForm.Repo.Values.GetValueOrDefault(k), StringComparer.Ordinal);
        lock (sync) session.PendingAdoption = new PendingAdoption(fullName, checkedForm.Repo.Type, values);
    }

    private string? TrackerSection(ProjectForm form) =>
        ProjectYamlWriter.Sections(form, providers).GetValueOrDefault("tracker");

    /// <summary>Call under the lock.</summary>
    private void EnsureCurrent(OnboardingSession session)
    {
        DropIfExpired();
        if (current != session) throw new OnboardingSessionEndedException(session.Ended ?? Replaced);
    }

    private void DropIfExpired()
    {
        if (current is { Busy: false } session && clock.GetUtcNow() - session.LastUsed > IdleTimeout)
        {
            session.Ended = Expired;
            current = null;
        }
    }

    private static string Bounded(string text) => ReviewerText.Line(text, ReviewerText.MaxSummaryChars);

    /// <summary>Reports synchronously on the reporting thread (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class StepProgress(Action<IReadOnlyList<CheckStep>> report) : IProgress<IReadOnlyList<CheckStep>>
    {
        public void Report(IReadOnlyList<CheckStep> value) => report(value);
    }
}
