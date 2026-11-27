using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class PointTransactionConfiguration : IEntityTypeConfiguration<PointTransaction>
{
    public void Configure(EntityTypeBuilder<PointTransaction> b)
    {
        b.ToTable("PointTransactions");
        b.Property(p => p.Action).HasConversion<string>().HasMaxLength(40);
        b.Property(p => p.ReferenceType).HasMaxLength(40);
        b.Property(p => p.ReferenceId).HasMaxLength(60);
        b.Property(p => p.ReferenceKey).HasMaxLength(160).IsRequired();
        b.Property(p => p.Note).HasMaxLength(500);
        b.Property(p => p.CreatedByEmail).HasMaxLength(256);
        b.Property(p => p.RevokedReason).HasMaxLength(500);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(p => p.Language).WithMany().HasForeignKey(p => p.LanguageId).OnDelete(DeleteBehavior.Restrict);
        // Idempotent awards: one row per learner and action/reference.
        b.HasIndex(p => new { p.UserId, p.ReferenceKey }).IsUnique();
        // History, daily cap, weekly/monthly leaderboards.
        b.HasIndex(p => new { p.UserId, p.CreatedAt });
        b.HasIndex(p => new { p.CreatedAt, p.LanguageId });
    }
}

internal sealed class UserPointsSummaryConfiguration : IEntityTypeConfiguration<UserPointsSummary>
{
    public void Configure(EntityTypeBuilder<UserPointsSummary> b)
    {
        b.ToTable("UserPointsSummary");
        b.HasKey(s => s.UserId);
        b.Property(s => s.Tier).HasConversion<int>();
        b.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserPointsSummary>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(s => s.TotalPoints);
    }
}

internal sealed class PointRuleConfiguration : IEntityTypeConfiguration<PointRule>
{
    public void Configure(EntityTypeBuilder<PointRule> b)
    {
        b.ToTable("PointRules");
        b.HasKey(r => r.Action);
        b.Property(r => r.Action).HasConversion<string>().HasMaxLength(40);
        b.Property(r => r.Description).HasMaxLength(300);
        b.Property(r => r.UpdatedByEmail).HasMaxLength(256);
    }
}

internal sealed class UserStreakConfiguration : IEntityTypeConfiguration<UserStreak>
{
    public void Configure(EntityTypeBuilder<UserStreak> b)
    {
        b.ToTable("Streaks");
        b.HasKey(s => s.UserId);
        b.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserStreak>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserBadgeConfiguration : IEntityTypeConfiguration<UserBadge>
{
    public void Configure(EntityTypeBuilder<UserBadge> b)
    {
        b.ToTable("UserBadges");
        b.Property(x => x.Badge).HasConversion<string>().HasMaxLength(40);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.Badge }).IsUnique();
    }
}

internal sealed class UserPrivacySettingsConfiguration : IEntityTypeConfiguration<UserPrivacySettings>
{
    public void Configure(EntityTypeBuilder<UserPrivacySettings> b)
    {
        b.ToTable("UserPrivacySettings");
        b.HasKey(s => s.UserId);
        b.Property(s => s.WhoCanMessage).HasConversion<int>();
        b.Property(s => s.WhoCanChallenge).HasConversion<int>();
        b.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserPrivacySettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CertificateTemplateConfiguration : IEntityTypeConfiguration<CertificateTemplate>
{
    public void Configure(EntityTypeBuilder<CertificateTemplate> b)
    {
        b.ToTable("CertificateTemplates");
        b.Property(t => t.Name).HasMaxLength(150).IsRequired();
        b.Property(t => t.Heading).HasMaxLength(150).IsRequired();
        b.Property(t => t.BodyText).HasMaxLength(2000).IsRequired();
        b.Property(t => t.SignatoryName).HasMaxLength(150);
        b.Property(t => t.SignatoryTitle).HasMaxLength(150);
        b.Property(t => t.SignatureUrl).HasMaxLength(500);
        b.Property(t => t.LogoUrl).HasMaxLength(500);
        b.Property(t => t.BackgroundUrl).HasMaxLength(500);
        b.Property(t => t.AccentColor).HasMaxLength(9);
        b.HasOne(t => t.Language).WithMany().HasForeignKey(t => t.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasQueryFilter(t => !t.IsDeleted);
    }
}

internal sealed class CertificateConfiguration : IEntityTypeConfiguration<Certificate>
{
    public void Configure(EntityTypeBuilder<Certificate> b)
    {
        b.ToTable("Certificates");
        b.Property(c => c.CertificateNo).HasMaxLength(40).IsRequired();
        b.Property(c => c.LearnerName).HasMaxLength(200).IsRequired();
        b.Property(c => c.CourseTitle).HasMaxLength(200).IsRequired();
        b.Property(c => c.LevelAchieved).HasConversion<int>();
        b.Property(c => c.GradeBand).HasConversion<int>();
        b.Property(c => c.PdfUrl).HasMaxLength(500);
        b.Property(c => c.VerificationCode).HasMaxLength(20).IsRequired();
        b.Property(c => c.RevokedReason).HasMaxLength(500);
        b.Property(c => c.RevokedByEmail).HasMaxLength(256);
        b.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.LearnerCourse).WithMany().HasForeignKey(c => c.LearnerCourseId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(c => c.Language).WithMany().HasForeignKey(c => c.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(c => c.CertificateNo).IsUnique();
        // One certificate per learner course (reissue updates it).
        b.HasIndex(c => c.LearnerCourseId).IsUnique();
        b.HasIndex(c => new { c.UserId, c.IssuedAt });
    }
}
