using Symphonist.Integrations.Secrets;

namespace Symphonist.Integrations.Tests.Secrets;

public class SecretStoreTests
{
    [Fact]
    public void Env_override_wins()
    {
        Environment.SetEnvironmentVariable("SYMPHONIST_SECRET_JIRA_ACME", "abc");
        try
        {
            Assert.Equal("abc", new CredentialManagerSecretStore().Get("jira-acme"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYMPHONIST_SECRET_JIRA_ACME", null);
        }
    }

    [Fact]
    public void Env_var_name_is_upper_case_with_underscores()
    {
        Assert.Equal("SYMPHONIST_SECRET_CLAUDE_CODE_TOKEN", CredentialManagerSecretStore.EnvironmentVariable("claude-code-token"));
    }

    [Fact, Trait("Category", "Live")]
    public void Roundtrips_through_os_store()
    {
        var store = new CredentialManagerSecretStore();
        var name = $"symphonist-test-{Guid.NewGuid():N}";
        try
        {
            store.Set(name, "s3cret");
            Assert.Equal("s3cret", store.Get(name));
            store.Set(name, "changed");
            Assert.Equal("changed", store.Get(name));
        }
        finally
        {
            store.Remove(name);
        }
        Assert.Null(store.Get(name));
    }
}
