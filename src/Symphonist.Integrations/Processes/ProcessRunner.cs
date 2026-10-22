using System.Diagnostics;
using System.Text;

namespace Symphonist.Integrations.Processes;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    string? StandardInput = null, IReadOnlyDictionary<string, string?>? Environment = null, TimeSpan? Timeout = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>Runs external processes; kills the entire process tree on timeout or cancellation.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory ?? "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        if (spec.Environment is not null)
            foreach (var (k, v) in spec.Environment)
            {
                if (v is null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.Timeout is { } timeout) timeoutCts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdin = WriteStdinAsync(process, spec.StandardInput);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            // Grandchildren holding the pipes open must not hang us after the kill.
            await Task.WhenAny(Task.WhenAll(stdout, stderr, stdin), Task.Delay(TimeSpan.FromSeconds(2)));
            return new ProcessResult(-1,
                stdout.IsCompletedSuccessfully ? stdout.Result : "",
                stderr.IsCompletedSuccessfully ? stderr.Result : "",
                TimedOut: true);
        }

        await stdin;
        return new ProcessResult(process.ExitCode, await stdout, await stderr, TimedOut: false);
    }

    private static async Task WriteStdinAsync(Process process, string? input)
    {
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        catch (IOException) { /* process exited before reading stdin */ }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }
}
