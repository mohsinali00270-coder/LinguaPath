using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class AiPromptTemplateConfiguration : IEntityTypeConfiguration<AiPromptTemplate>
{
    public void Configure(EntityTypeBuilder<AiPromptTemplate> b)
    {
        b.ToTable("AiPromptTemplates");
        b.Property(t => t.Key).HasMaxLength(60).IsRequired();
        b.Property(t => t.SystemText).HasMaxLength(8000).IsRequired();
        b.Property(t => t.PromptText).HasMaxLength(16000).IsRequired();
        b.Property(t => t.Model).HasMaxLength(100);
        b.Property(t => t.Notes).HasMaxLength(500);
        b.Property(t => t.CreatedByEmail).HasMaxLength(256);
        b.HasIndex(t => new { t.Key, t.Version }).IsUnique();
        b.HasIndex(t => new { t.Key, t.IsActive });
    }
}

internal sealed class AiUsageLogConfiguration : IEntityTypeConfiguration<AiUsageLog>
{
    public void Configure(EntityTypeBuilder<AiUsageLog> b)
    {
        b.ToTable("AiUsageLogs");
        b.Property(l => l.Feature).HasConversion<int>();
        b.Property(l => l.Status).HasConversion<int>();
        b.Property(l => l.Provider).HasMaxLength(50);
        b.Property(l => l.Model).HasMaxLength(100);
        b.Property(l => l.Cost).HasPrecision(12, 6);
        b.Property(l => l.Error).HasMaxLength(1000);
        b.HasIndex(l => l.CreatedAt);
        b.HasIndex(l => new { l.Feature, l.CreatedAt });
    }
}

internal sealed class AiGenerationRequestConfiguration : IEntityTypeConfiguration<AiGenerationRequest>
{
    public void Configure(EntityTypeBuilder<AiGenerationRequest> b)
    {
        b.ToTable("AiGenerationRequests");
        b.Property(r => r.Type).HasConversion<int>();
        b.Property(r => r.Status).HasConversion<int>();
        b.Property(r => r.ParametersJson).HasMaxLength(4000).IsRequired();
        b.Property(r => r.Notes).HasMaxLength(1000);
        b.Property(r => r.Stage).HasMaxLength(20);
        b.Property(r => r.Summary).HasMaxLength(1000);
        b.Property(r => r.Error).HasMaxLength(2000);
        b.Property(r => r.EstimatedCost).HasPrecision(12, 4);
        b.Property(r => r.ActualCost).HasPrecision(12, 6);
        b.Property(r => r.RequestedByEmail).HasMaxLength(256);
        b.Property(r => r.DecidedByEmail).HasMaxLength(256);
        b.Property(r => r.DecisionComment).HasMaxLength(1000);
        b.Property(r => r.LockedBy).HasMaxLength(100);
        b.HasOne(r => r.Language).WithMany().HasForeignKey(r => r.LanguageId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(r => r.Course).WithMany().HasForeignKey(r => r.CourseId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(r => new { r.Status, r.CreatedAt });
    }
}

internal sealed class AiGeneratedItemConfiguration : IEntityTypeConfiguration<AiGeneratedItem>
{
    public void Configure(EntityTypeBuilder<AiGeneratedItem> b)
    {
        b.ToTable("AiGeneratedItems");
        b.Property(i => i.EntityType).HasConversion<int>();
        b.Property(i => i.Feature).HasConversion<int>();
        b.Property(i => i.Model).HasMaxLength(100);
        b.Property(i => i.Prompt).HasMaxLength(16000);
        b.HasOne(i => i.Request).WithMany().HasForeignKey(i => i.RequestId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(i => new { i.EntityType, i.EntityId });
        b.HasIndex(i => i.RequestId);
    }
}
