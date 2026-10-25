using Symphonist.Integrations.Secrets;

namespace Symphonist.Integrations.Tests.Secrets;

/// <summary>Never touches the OS credential store: only the memory and file stores are exercised.</summary>
public sealed class SecretStoreSelectionTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"symphonist-secrets-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
    }

    [Theory]
    [InlineData(null, typeof(CredentialManagerSecretStore))]
    [InlineData("", typeof(CredentialManagerSecretStore))]
    [InlineData("os", typeof(CredentialManagerSecretStore))]
    [InlineData("memory", typeof(MemorySecretStore))]
    [InlineData(" Memory ", typeof(MemorySecretStore))]
    [InlineData("file", typeof(FileSecretStore))]
    public void Kind_picks_the_store(string? kind, Type expected)
    {
        var store = SecretStores.Create(kind, home);

        Assert.IsType(expected, store);
        if (store is FileSecretStore file) Assert.Equal(Path.Combine(home, "secrets.json"), file.Path);
        Assert.False(Directory.Exists(home)); // choosing a store writes nothing
    }

    [Fact]
    public void Unknown_kind_is_refused()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SecretStores.Create("vault", home));
        Assert.Equal("SYMPHONIST_SECRET_STORE must be os, memory or file (got 'vault')", e.Message);
    }

    [Fact]
    public void Memory_store_round_trips_and_honours_overrides()
    {
        var store = new MemorySecretStore();
        store.Set("jira-mem", "a");
        Assert.Equal("a", store.Get("jira-mem"));
        store.Remove("jira-mem");
        Assert.Null(store.Get("jira-mem"));

        Environment.SetEnvironmentVariable("SYMPHONIST_SECRET_JIRA_MEM", "from-env");
        try
        {
            store.Set("jira-mem", "stored");
            Assert.Equal("from-env", store.Get("jira-mem"));
            Assert.True(store.IsOverridden("jira-mem"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYMPHONIST_SECRET_JIRA_MEM", null);
        }
        Assert.Equal("stored", store.Get("jira-mem"));
    }

    [Fact]
    public void File_store_round_trips_across_instances_and_keeps_the_file_private()
    {
        var path = Path.Combine(home, "secrets.json");
        var store = new FileSecretStore(path);
        Assert.Null(store.Get("jira-file"));

        store.Set("jira-file", "s3cret-value");
        store.Set("bitbucket-file", "other");
        store.Set("jira-file", "changed-value");

        var again = new FileSecretStore(path);
        Assert.Equal("changed-value", again.Get("jira-file"));
        Assert.Equal("other", again.Get("bitbucket-file"));
        again.Remove("bitbucket-file");
        Assert.Null(store.Get("bitbucket-file"));
        Assert.Equal(["secrets.json"], Directory.GetFiles(home).Select(Path.GetFileName));

        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain("changed-value", File.ReadAllText(path)); // DPAPI-encrypted for the user
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
