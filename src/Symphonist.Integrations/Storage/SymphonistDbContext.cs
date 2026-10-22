using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Symphonist.Core.Pipeline;

namespace Symphonist.Integrations.Storage;

public sealed class AgentRunEntity
{
    public long Id { get; set; }
    public Guid TicketId { get; set; }
    public string StageId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Succeeded { get; set; }
    public decimal? CostUsd { get; set; }
    public string? Error { get; set; }
    public string RawLogPath { get; set; } = "";
}

public sealed class SymphonistDbContext(DbContextOptions<SymphonistDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<AgentRunEntity> AgentRuns => Set<AgentRunEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQLite cannot order DateTimeOffset natively; store as sortable binary longs.
        var dto = new DateTimeOffsetToBinaryConverter();

        b.Entity<Ticket>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.ProjectId, t.Key }).IsUnique();
            e.Property(t => t.Status).HasConversion<string>();
            e.Property(t => t.CreatedAt).HasConversion(dto);
            e.Property(t => t.UpdatedAt).HasConversion(dto);
            e.Property(t => t.StageStartedAt).HasConversion(dto);
            e.Property(t => t.WaitingSince).HasConversion(dto);
        });

        b.Entity<TicketEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TicketId);
            e.Property(x => x.At).HasConversion(dto);
        });

        b.Entity<AgentRunEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TicketId);
            e.Property(x => x.StartedAt).HasConversion(dto);
        });
    }
}
