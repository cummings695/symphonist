using Microsoft.Extensions.Time.Testing;
using Symphonist.Host.Dashboard;

namespace Symphonist.Host.Tests.Dashboard;

public sealed class DashboardLoginTests
{
    private readonly FakeTimeProvider time = new();
    private readonly DashboardLogin login;

    public DashboardLoginTests() => login = new DashboardLogin(time);

    [Fact]
    public void Code_is_32_random_bytes_base64url()
    {
        var code = login.IssueCode();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", code);
        Assert.NotEqual(code, login.IssueCode());
    }

    [Fact]
    public void Code_redeems_once()
    {
        var c = login.IssueCode();
        Assert.True(login.Redeem(c));
        Assert.False(login.Redeem(c));
    }

    [Fact]
    public void Code_redeems_just_before_expiry()
    {
        var c = login.IssueCode();
        time.Advance(DashboardLogin.CodeLifetime - TimeSpan.FromSeconds(1));
        Assert.True(login.Redeem(c));
    }

    [Fact]
    public void Code_expires_after_two_minutes()
    {
        var c = login.IssueCode();
        time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.False(login.Redeem(c));
    }

    [Fact]
    public void Unknown_or_empty_code_fails()
    {
        login.IssueCode();
        Assert.False(login.Redeem(null));
        Assert.False(login.Redeem(""));
        Assert.False(login.Redeem("nope"));
    }

    [Fact]
    public void Oldest_code_evicted_beyond_max()
    {
        var codes = Enumerable.Range(0, DashboardLogin.MaxOutstanding + 1).Select(_ => login.IssueCode()).ToList();
        Assert.False(login.Redeem(codes[0]));
        Assert.True(login.Redeem(codes[^1]));
        Assert.True(login.Redeem(codes[1]));
    }
}
