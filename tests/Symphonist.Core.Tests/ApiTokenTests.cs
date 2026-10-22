using Symphonist.Core;

namespace Symphonist.Core.Tests;

public sealed class ApiTokenTests
{
    [Fact]
    public void TryLoad_returns_null_and_creates_nothing_when_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "api-token");

        Assert.Null(ApiToken.TryLoad(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryLoad_reads_existing_token()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "api-token");
            var token = ApiToken.LoadOrCreate(path);

            Assert.Equal(token, ApiToken.TryLoad(path));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
