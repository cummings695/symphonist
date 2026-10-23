using System.Buffers.Text;
using System.Security.Cryptography;

namespace Symphonist.Host.Dashboard;

/// <summary>
/// Single-use, short-lived codes that turn a link printed by <c>symphonist dashboard</c> into a sign-in cookie. Codes
/// live only in memory: a host restart invalidates every outstanding link (cookies already issued stay valid).
/// </summary>
public sealed class DashboardLogin(TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    public const int MaxOutstanding = 16; // issuing more evicts the oldest

    private readonly Lock sync = new();
    private readonly List<(string Code, DateTimeOffset Expires)> outstanding = []; // oldest first, at most MaxOutstanding

    /// <summary>32 random bytes, base64url.</summary>
    public string IssueCode()
    {
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (sync)
        {
            if (outstanding.Count >= MaxOutstanding) outstanding.RemoveAt(0);
            outstanding.Add((code, time.GetUtcNow() + CodeLifetime));
        }
        return code;
    }

    /// <summary>True once per issued code, and only before it expires.</summary>
    public bool Redeem(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        lock (sync)
        {
            var i = outstanding.FindIndex(o => o.Code == code);
            if (i < 0) return false;
            var expires = outstanding[i].Expires;
            outstanding.RemoveAt(i);
            return time.GetUtcNow() < expires;
        }
    }
}
