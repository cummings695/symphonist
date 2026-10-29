using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Symphonist.Core.Config;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

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
    public string? Summary { get; set; }
    public bool IsReview { get; set; }
    public bool IsEvidence { get; set; }
}

public sealed class EvidenceEntity
{
    public long Id { get; set; }
    public Guid TicketId { get; set; }
    public string ProjectId { get; set; } = "";
    public string StageId { get; set; } = "";
    public DateTimeOffset WaitingSince { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Head { get; set; }
    public string? ReportJson { get; set; }
    public bool HasIssues { get; set; }
    public string? Error { get; set; }
    /// <summary>JSON array of strings.</summary>
    public string ScreenshotsJson { get; set; } = "[]";
    public bool Posted { get; set; }
}

public sealed class GateReviewEntity
{
    public long Id { get; set; }
    public Guid TicketId { get; set; }
    public string ProjectId { get; set; } = "";
    public string StageId { get; set; } = "";
    public DateTimeOffset WaitingSince { get; set; }
    public DateTimeOffset At { get; set; }
    public ReviewMode Mode { get; set; }
    public GatePolicy Gate { get; set; }
    public RiskLevel Risk { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>JSON arrays of strings.</summary>
    public string ReasonsJson { get; set; } = "[]";
    public string CheckedJson { get; set; } = "[]";
    public string AreasJson { get; set; } = "[]";
    public string? Error { get; set; }
}

public sealed class GateDecisionEntity
{
    public long Id { get; set; }
    public Guid TicketId { get; set; }
    public string ProjectId { get; set; } = "";
    public string StageId { get; set; } = "";
    public DateTimeOffset WaitingSince { get; set; }
    public GateDecision Decision { get; set; }
    public bool Auto { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class TouchEntity
{
    public long Id { get; set; }
    public Guid TicketId { get; set; }
    public string ProjectId { get; set; } = "";
    public string StageId { get; set; } = "";
    public TouchKind Kind { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class ProviderPauseEntity
{
    public string Key { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public DateTimeOffset? NextProbeAt { get; set; }
}

public sealed class SymphonistDbContext(DbContextOptions<SymphonistDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<AgentRunEntity> AgentRuns => Set<AgentRunEntity>();
    public DbSet<GateReviewEntity> GateReviews => Set<GateReviewEntity>();
    public DbSet<GateDecisionEntity> GateDecisions => Set<GateDecisionEntity>();
    public DbSet<EvidenceEntity> Evidence => Set<EvidenceEntity>();

    public DbSet<TouchEntity> Touches => Set<TouchEntity>();
    public DbSet<ProviderPauseEntity> ProviderPauses => Set<ProviderPauseEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQLite cannot order DateTimeOffset natively; store as sortable binary longs.
        var dto = new DateTimeOffsetToBinaryConverter();

        b.Entity<Ticket>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.ProjectId, t.Key }).IsUnique();
            e.Property(t => t.Status).HasConversion<string>();
            // Rows from before release batches are work tickets: the migration's column default is "Work".
            e.Property(t => t.Kind).HasConversion<string>();
            e.Property(t => t.CreatedAt).HasConversion(dto);
            e.Property(t => t.UpdatedAt).HasConversion(dto);
            e.Property(t => t.StageStartedAt).HasConversion(dto);
            e.Property(t => t.WaitingSince).HasConversion(dto);
            e.Property(t => t.ClaudeCommentsHandledAt).HasConversion(dto);
            e.Property(t => t.MergeAcceptedAt).HasConversion(dto);
            e.Property(t => t.PrCommentsHandledAt).HasConversion(dto);
            e.Property(t => t.Risk).HasConversion<string>();
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

        b.Entity<GateReviewEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TicketId, x.StageId, x.WaitingSince });
            e.Property(x => x.WaitingSince).HasConversion(dto);
            e.Property(x => x.At).HasConversion(dto);
            e.Property(x => x.Mode).HasConversion<string>();
            e.Property(x => x.Gate).HasConversion<string>();
            e.Property(x => x.Risk).HasConversion<string>();
        });

        b.Entity<GateDecisionEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TicketId, x.StageId, x.WaitingSince });
            e.Property(x => x.WaitingSince).HasConversion(dto);
            e.Property(x => x.At).HasConversion(dto);
            e.Property(x => x.Decision).HasConversion<string>();
        });

        b.Entity<TouchEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TicketId);
            e.Property(x => x.At).HasConversion(dto);
            e.Property(x => x.Kind).HasConversion<string>();
        });

        b.Entity<ProviderPauseEntity>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Since).HasConversion(dto);
            e.Property(x => x.ResetsAt).HasConversion(dto);
            e.Property(x => x.NextProbeAt).HasConversion(dto);
        });

        b.Entity<EvidenceEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TicketId);
            e.Property(x => x.WaitingSince).HasConversion(dto);
            e.Property(x => x.At).HasConversion(dto);
        });
    }
}
