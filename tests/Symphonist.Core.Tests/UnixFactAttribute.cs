namespace Symphonist.Core.Tests;

/// <summary>A fact that only runs on Linux/macOS.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix file modes do not apply on Windows";
    }
}
