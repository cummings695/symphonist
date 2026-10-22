using Symphonist.Core;

namespace Symphonist.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Home_uses_SYMPHONIST_HOME_when_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", dir);
        try { Assert.Equal(dir, SymphonistPaths.Home); Assert.Equal(Path.Combine(dir, "a", "b"), SymphonistPaths.Resolve("a", "b")); }
        finally { Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null); }
    }

    [Fact]
    public void Home_defaults_to_dot_symphonist_in_user_profile()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_HOME", null);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".symphonist");
        Assert.Equal(expected, SymphonistPaths.Home);
    }
}
