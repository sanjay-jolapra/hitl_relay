using HitlRelay.Models;
using Microsoft.EntityFrameworkCore;

namespace HitlRelay.Data;

public class HitlDbContext : DbContext
{
    public HitlDbContext(DbContextOptions<HitlDbContext> options) : base(options) { }

    public DbSet<HitlRequest> Requests => Set<HitlRequest>();
    public DbSet<HitlResponse> Responses => Set<HitlResponse>();
    public DbSet<HitlAuditEvent> AuditEvents => Set<HitlAuditEvent>();
    public DbSet<HitlChannelConnection> ChannelConnections => Set<HitlChannelConnection>();
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<ProcessedInbound> ProcessedInbounds => Set<ProcessedInbound>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<HitlRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CorrelationToken).HasMaxLength(100).IsRequired();
            e.Property(x => x.ShortCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.Channel).HasMaxLength(20).IsRequired();
            e.Ignore(x => x.Keywords);
            e.Ignore(x => x.Config);
            e.HasIndex(x => x.CorrelationToken).IsUnique();
            e.HasIndex(x => x.ShortCode).IsUnique();
            e.HasIndex(x => new { x.Channel, x.ExternalConversationId, x.Status });
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.HasIndex(x => x.WorkflowRunId);
        });

        b.Entity<HitlResponse>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RequestId);
        });

        b.Entity<HitlAuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RequestId);
        });

        b.Entity<HitlChannelConnection>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Channel, x.Key }).IsUnique();
        });

        b.Entity<WorkflowRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
        });

        b.Entity<ProcessedInbound>(e => e.HasKey(x => new { x.Channel, x.DedupeKey }));
    }
}
