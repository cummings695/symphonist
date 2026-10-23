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
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add(url.AbsoluteUri);
            }

            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
