using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SpecOps.Data;

public sealed class SpecOpsDbContext(DbContextOptions<SpecOpsDbContext> options)
    : IdentityDbContext<SpecOpsUser>(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<RunRecord> Runs => Set<RunRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SpecOpsUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(256);
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
            user.HasIndex(u => u.Status);
        });

        builder.Entity<AuditEvent>(audit =>
        {
            audit.Property(a => a.Actor).HasMaxLength(256);
            audit.Property(a => a.ActorId).HasMaxLength(450);
            audit.Property(a => a.Action).HasMaxLength(64);
            audit.Property(a => a.Target).HasMaxLength(256);
            audit.Property(a => a.ClientIp).HasMaxLength(64);
            audit.HasIndex(a => a.TimestampUtc);
            audit.HasIndex(a => a.Action);
        });

        builder.Entity<RunRecord>(run =>
        {
            run.Property(r => r.ScriptId).HasMaxLength(128);
            run.Property(r => r.ScriptName).HasMaxLength(256);
            run.Property(r => r.UserId).HasMaxLength(450);
            run.Property(r => r.UserName).HasMaxLength(256);
            run.Property(r => r.ClientIp).HasMaxLength(64);
            run.Property(r => r.Status).HasMaxLength(32);
            run.HasIndex(r => r.QueuedAtUtc);
            run.HasIndex(r => new { r.UserId, r.QueuedAtUtc });
            run.HasIndex(r => r.EndedAtUtc);
        });
    }
}
