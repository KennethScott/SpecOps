using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SpecOps.Data;

public sealed class SpecOpsDbContext(DbContextOptions<SpecOpsDbContext> options) : IdentityDbContext<SpecOpsUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SpecOpsUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(256);
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
            user.HasIndex(u => u.Status);
        });
    }
}
