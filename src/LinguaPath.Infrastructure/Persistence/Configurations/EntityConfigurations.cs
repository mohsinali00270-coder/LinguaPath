using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> b)
    {
        b.Property(u => u.FullName).HasMaxLength(100).IsRequired();
        b.Property(u => u.DisplayName).HasMaxLength(50);
        b.Property(u => u.Bio).HasMaxLength(300);
        b.Property(u => u.PhotoUrl).HasMaxLength(500);
        b.Property(u => u.UiLanguage).HasMaxLength(8).IsRequired();
        b.Property(u => u.TimeZone).HasMaxLength(64).IsRequired();
        b.Property(u => u.BlockReason).HasMaxLength(500);
        b.Property(u => u.UserType).HasConversion<int>();

        b.HasOne(u => u.NativeLanguage).WithMany().HasForeignKey(u => u.NativeLanguageId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(u => u.DisplayName);
        b.HasIndex(u => new { u.UserType, u.IsDeleted, u.CreatedAt });
    }
}

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> b) => b.Property(r => r.Description).HasMaxLength(300);
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.HasKey(t => t.Id);
        b.Property(t => t.TokenHash).HasMaxLength(88).IsRequired();
        b.Property(t => t.ReplacedByTokenHash).HasMaxLength(88);
        b.Property(t => t.DeviceId).HasMaxLength(128);
        b.Property(t => t.CreatedByIp).HasMaxLength(64);
        b.Property(t => t.RevokedReason).HasMaxLength(100);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => new { t.UserId, t.RevokedAt });
        b.HasIndex(t => t.FamilyId);
        b.HasOne(t => t.User).WithMany(u => u.RefreshTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserDeviceConfiguration : IEntityTypeConfiguration<UserDevice>
{
    public void Configure(EntityTypeBuilder<UserDevice> b)
    {
        b.ToTable("UserDevices");
        b.Property(d => d.DeviceId).HasMaxLength(128).IsRequired();
        b.Property(d => d.PushToken).HasMaxLength(512);
        b.Property(d => d.AppVersion).HasMaxLength(32);
        b.Property(d => d.Platform).HasConversion<int>();
        b.HasIndex(d => new { d.UserId, d.DeviceId }).IsUnique();
        b.HasIndex(d => d.DeviceId);
        b.HasOne(d => d.User).WithMany(u => u.Devices).HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EmailOtpConfiguration : IEntityTypeConfiguration<EmailOtp>
{
    public void Configure(EntityTypeBuilder<EmailOtp> b)
    {
        b.ToTable("EmailOtps");
        b.Property(o => o.CodeHash).HasMaxLength(88).IsRequired();
        b.Property(o => o.Purpose).HasConversion<int>();
        b.HasIndex(o => new { o.UserId, o.Purpose, o.CreatedAt });
        b.HasOne(o => o.User).WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BlockedIdentityConfiguration : IEntityTypeConfiguration<BlockedIdentity>
{
    public void Configure(EntityTypeBuilder<BlockedIdentity> b)
    {
        b.ToTable("BlockedIdentities");
        b.Property(x => x.NormalizedEmail).HasMaxLength(256);
        b.Property(x => x.ExternalLoginKey).HasMaxLength(300);
        b.Property(x => x.DeviceId).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => x.NormalizedEmail);
        b.HasIndex(x => x.ExternalLoginKey);
        b.HasIndex(x => x.DeviceId);
    }
}

internal sealed class LanguageConfiguration : IEntityTypeConfiguration<Language>
{
    public void Configure(EntityTypeBuilder<Language> b)
    {
        b.ToTable("Languages");
        b.Property(l => l.Name).HasMaxLength(80).IsRequired();
        b.Property(l => l.NativeName).HasMaxLength(80).IsRequired();
        b.Property(l => l.IsoCode).HasMaxLength(10).IsRequired();
        b.Property(l => l.FlagUrl).HasMaxLength(500);
        b.Property(l => l.TtsVoice).HasMaxLength(100);
        b.Property(l => l.SttLocale).HasMaxLength(20);
        b.Property(l => l.Direction).HasConversion<int>();
        b.Property(l => l.Status).HasConversion<int>();
        b.Property(l => l.MinLevel).HasConversion<int>();
        b.Property(l => l.MaxLevel).HasConversion<int>();
        b.HasIndex(l => l.IsoCode).IsUnique();
        b.HasIndex(l => new { l.Status, l.IsDeleted });
        b.HasQueryFilter(l => !l.IsDeleted);
    }
}

internal sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> b)
    {
        b.ToTable("SystemSettings");
        b.HasKey(s => s.Key);
        b.Property(s => s.Key).HasMaxLength(100);
        b.Property(s => s.Value).HasMaxLength(2000).IsRequired();
        b.Property(s => s.Group).HasMaxLength(50).IsRequired();
        b.Property(s => s.Description).HasMaxLength(500);
        b.Property(s => s.DataType).HasConversion<int>();
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.Property(a => a.UserEmail).HasMaxLength(256);
        b.Property(a => a.Action).HasMaxLength(50).IsRequired();
        b.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        b.Property(a => a.EntityId).HasMaxLength(100);
        b.Property(a => a.IpAddress).HasMaxLength(64);
        b.HasIndex(a => a.CreatedAt);
        b.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
