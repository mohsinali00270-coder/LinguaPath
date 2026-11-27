using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.Property(n => n.Type).HasConversion<int>();
        b.Property(n => n.Title).HasMaxLength(150).IsRequired();
        b.Property(n => n.Body).HasMaxLength(500).IsRequired();
        b.Property(n => n.DataJson).HasMaxLength(1000);
        b.Property(n => n.DedupKey).HasMaxLength(100);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(n => new { n.UserId, n.Id });
        b.HasIndex(n => new { n.UserId, n.ReadAt });
        // A reminder goes out once: the worker can run on more than one server without doubling up.
        b.HasIndex(n => new { n.UserId, n.DedupKey }).IsUnique().HasFilter("[DedupKey] IS NOT NULL");
    }
}
