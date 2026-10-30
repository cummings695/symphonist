using System.CommandLine;
using System.Text;
using Symphonist.Core.Integrations;

namespace Symphonist.Cli;

/// <summary>The <c>symphonist</c> command tree. Exit codes: 0 success, 1 API/usage/other error, 2 host not running.</summary>
public static class Cli
{
    public const int Ok = 0, ApiError = 1, HostDown = 2;

    /// <param name="interactive">True when stdin is a terminal: <c>secret set</c> then prompts and reads without echo.</param>
    public static Task<int> RunAsync(string[] args, SymphonistClient client, TextWriter stdout, TextWriter stderr,
        ISecretStore secrets, TextReader stdin, bool interactive = false, TimeProvider? clock = null,
        Func<Uri, bool>? openBrowser = null)
    {
        var time = clock ?? TimeProvider.System;
        var open = openBrowser ?? BrowserLauncher.TryOpen;
        var root = new RootCommand("Symphonist command-line client");

        var projectOption = new Option<string?>("--project") { Description = "Only tickets of this project" };
        var status = new Command("status", "List tickets, those waiting on you first") { projectOption };
        Run(status, async (r, ct) =>
        {
            var tickets = await client.ListTicketsAsync(r.GetValue(projectOption), ct);
            stdout.Write(StatusTable.Render(tickets, time.GetUtcNow()));
        });
        root.Add(status);

        var show = new Command("show", "Show a ticket and its events");
        var (showProject, showKey) = Ticket(show);
        Run(show, async (r, ct) =>
        {
            var t = await client.GetTicketAsync(r.GetValue(showProject)!, r.GetValue(showKey)!, ct);
            stdout.Write(StatusTable.RenderDetail(t, time.GetUtcNow()));
        });
        root.Add(show);

        var approve = new Command("approve", "Approve the stage a ticket is waiting on");
        var (approveProject, approveKey) = Ticket(approve);
        Run(approve, async (r, ct) =>
            stdout.WriteLine((await client.ApproveAsync(r.GetValue(approveProject)!, r.GetValue(approveKey)!, ct)).Message));
        root.Add(approve);

        var sendBack = new Command("send-back", "Send a ticket back with feedback");
        var (sendProject, sendKey) = Ticket(sendBack);
        var message = new Option<string>("-m", "--message") { Description = "Feedback for the agent", Required = true };
        var toStage = new Option<string?>("--to") { Description = "Stage to send back to (default: rerun the current stage)" };
        sendBack.Add(message);
        sendBack.Add(toStage);
        Run(sendBack, async (r, ct) =>
            stdout.WriteLine((await client.SendBackAsync(r.GetValue(sendProject)!, r.GetValue(sendKey)!,
                r.GetValue(message)!, r.GetValue(toStage), ct)).Message));
        root.Add(sendBack);

        var unblock = new Command("unblock", "Unblock a blocked ticket");
        var (unblockProject, unblockKey) = Ticket(unblock);
        Run(unblock, async (r, ct) =>
            stdout.WriteLine((await client.UnblockAsync(r.GetValue(unblockProject)!, r.GetValue(unblockKey)!, ct)).Message));
        root.Add(unblock);

        var forget = new Command("forget", "Remove a ticket from Symphonist only (tracker, repo and workspace are unchanged)");
        var (forgetProject, forgetKey) = Ticket(forget);
        Run(forget, async (r, ct) =>
        {
            var (project, key) = (r.GetValue(forgetProject)!, r.GetValue(forgetKey)!);
            await client.ForgetAsync(project, key, ct);
            stdout.WriteLine($"Forgot {project}/{key}. Its downloaded attachments were removed; Jira, Bitbucket and the workspace are unchanged; " +
                "if it still matches the tracker query it will be picked up again as new.");
        });
        root.Add(forget);

        var poll = new Command("poll", "Ask the host to poll now");
        Run(poll, async (_, ct) =>
        {
            await client.PollAsync(ct);
            stdout.WriteLine("Poll requested");
        });
        root.Add(poll);

        var noBrowser = new Option<bool>("--no-browser") { Description = "Print the link instead of opening it" };
        var dashboard = new Command("dashboard", "Open the dashboard in your browser") { noBrowser };
        Run(dashboard, async (r, ct) =>
        {
            var url = await client.CreateDashboardLinkAsync(ct);
            if (r.GetValue(noBrowser)) stdout.WriteLine(url);
            else if (open(url)) stdout.WriteLine("Opened the dashboard (link valid for 2 minutes)");
            else stdout.WriteLine($"Could not open a browser. Open this link within 2 minutes: {url}");
        });
        root.Add(dashboard);

        var projects = new Command("projects", "List projects, pause/resume one, or reload the project files");
        Run(projects, async (_, ct) => stdout.Write(StatusTable.RenderProjects(await client.ListProjectsAsync(ct))));
        foreach (var (name, paused, done) in new[] { ("pause", true, "Paused"), ("resume", false, "Resumed") })
        {
            var id = new Argument<string>("id") { Description = "Project id" };
            var sub = new Command(name, $"{char.ToUpperInvariant(name[0])}{name[1..]} a project") { id };
            Run(sub, async (r, ct) =>
            {
                var project = r.GetValue(id)!;
                await client.SetPausedAsync(project, paused, ct);
                stdout.WriteLine($"{done} {project}");
            });
            projects.Add(sub);
        }

        var reload = new Command("reload", "Reload the project files after editing them by hand");
        RunWithExitCode(reload, async (_, ct) =>
        {
            var result = await client.ReloadProjectsAsync(ct);
            stdout.WriteLine($"Reloaded {result.Projects.Count} project(s)");
            foreach (var (file, error) in result.Errors.OrderBy(e => e.Key, StringComparer.Ordinal))
                stderr.WriteLine($"skipped {file}: {error}");
            return result.Errors.Count > 0 ? ApiError : Ok;
        });
        projects.Add(reload);
        root.Add(projects);

        var limits = new Command("limits", "List agent providers paused by a usage limit, or resume one");
        Run(limits, async (_, ct) =>
        {
            var pauses = await client.ListLimitsAsync(ct);
            if (pauses.Count == 0) stdout.WriteLine("No provider is paused by a usage limit");
            else stdout.Write(StatusTable.RenderLimits(pauses));
        });
        var limitKey = new Argument<string>("key") { Description = "Usage-limit key, as `symphonist limits` lists it" };
        var limitResume = new Command("resume", "Let jobs on a paused provider start again now") { limitKey };
        Run(limitResume, async (r, ct) =>
        {
            var key = r.GetValue(limitKey)!;
            await client.ResumeLimitAsync(key, ct);
            stdout.WriteLine($"Resumed {key}");
        });
        limits.Add(limitResume);
        root.Add(limits);

        var releaseProject = new Argument<string>("project") { Description = "Project id" };
        var releaseYes = new Option<bool>("--yes", "-y") { Description = "Start without asking" };
        var release = new Command("release", "Release now: ship the tickets ready to release as one release") { releaseProject, releaseYes };
        RunWithExitCode(release, async (r, ct) =>
        {
            var project = r.GetValue(releaseProject)!;
            var preview = await client.PreviewReleaseAsync(project, ct);
            if (preview.Unavailable is { Length: > 0 } unavailable)
            {
                stderr.WriteLine(unavailable);
                return ApiError;
            }
            stdout.Write(StatusTable.RenderReleasePreview(preview));
            if (!r.GetValue(releaseYes))
            {
                var count = preview.Ready.Count + preview.AlsoShipped.Count(a => a.Finishes);
                stdout.Write($"Release {count} ticket(s) to {preview.ProductionBranch ?? "production"}? [y/N] ");
                var answer = stdin.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    stdout.WriteLine();
                    stdout.WriteLine("Not released");
                    return Ok;
                }
            }
            stdout.WriteLine((await client.ReleaseNowAsync(project, preview, ct)).Message);
            return Ok;
        });
        root.Add(release);

        var secret = new Command("secret", "Manage secrets in the OS credential store");
        var secretSet = new Command("set", "Store a secret; the value is read from stdin, never from arguments");
        var secretName = new Argument<string>("name") { Description = "Secret name" };
        secretSet.Add(secretName);
        Run(secretSet, (r, _) =>
        {
            var name = r.GetValue(secretName)!;
            var value = interactive ? ReadHidden(stdout, name) : stdin.ReadLine();
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) throw new ApiException("Secret value must not be empty");
            try
            {
                secrets.Set(name, value);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ApiException($"Could not save secret '{name}': {e.Message}");
            }
            stdout.WriteLine($"Secret '{name}' saved");
            return Task.CompletedTask;
        });
        secret.Add(secretSet);
        root.Add(secret);

        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr });

        // Wraps an action: maps client failures to exit codes and messages.
        void Run(Command command, Func<ParseResult, CancellationToken, Task> action) =>
            RunWithExitCode(command, async (r, ct) =>
            {
                await action(r, ct);
                return Ok;
            });

        // The same, for an action that decides its own exit code.
        void RunWithExitCode(Command command, Func<ParseResult, CancellationToken, Task<int>> action) =>
            command.SetAction(async (r, ct) =>
            {
                try
                {
                    return await action(r, ct);
                }
                catch (HostNotRunningException e)
                {
                    stderr.WriteLine(e.Message);
                    return HostDown;
                }
                catch (ApiException e)
                {
                    stderr.WriteLine(e.Message);
                    return ApiError;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Anything else: a one-line message, never a stack trace.
                    stderr.WriteLine($"Error: {e.Message}");
                    return ApiError;
                }
            });
    }

    private static (Argument<string> Project, Argument<string> Key) Ticket(Command command)
    {
        var project = new Argument<string>("project") { Description = "Project id" };
        var key = new Argument<string>("key") { Description = "Ticket key" };
        command.Add(project);
        command.Add(key);
        return (project, key);
    }

    private static string ReadHidden(TextWriter stdout, string name)
    {
        stdout.Write($"Value for {name}: ");
        stdout.Flush();
        var value = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        stdout.WriteLine();
        return value.ToString();
    }
}
