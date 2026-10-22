using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Symphonist.Integrations.Storage;

/// <summary>Lets <c>dotnet ef</c> create the context without a host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SymphonistDbContext>
{
    public SymphonistDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SymphonistDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
