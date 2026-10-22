namespace Symphonist.Core;

public static class SymphonistPaths
{
    public static string Home
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("SYMPHONIST_HOME");
            return !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".symphonist");
        }
    }

    public static string Resolve(params string[] parts) => Path.Combine([Home, .. parts]);
}
