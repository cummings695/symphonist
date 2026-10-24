using System.Diagnostics;

namespace Symphonist.Cli;

/// <summary>Opens a URL in the user's default browser.</summary>
public static class BrowserLauncher
{
    /// <summary>Starts the OS opener for an http(s) URL; false (never an exception) when that is not possible.</summary>
    public static bool TryOpen(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https")) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // ShellExecute usually hands the URL to a running browser and returns no process: no exception is success.
                using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                return true;
            }

            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(url.AbsoluteUri);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
