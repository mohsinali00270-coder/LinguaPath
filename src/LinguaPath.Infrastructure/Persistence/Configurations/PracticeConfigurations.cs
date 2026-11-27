using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class PracticeItemConfiguration : IEntityTypeConfiguration<PracticeItem>
{
    public void Configure(EntityTypeBuilder<PracticeItem> b)
    {
        b.ToTable("PracticeItems");
        b.Property(p => p.Type).HasConversion<int>();
        b.Property(p => p.Level).HasConversion<int>();
        b.Property(p => p.Status).HasConversion<int>();
        b.Property(p => p.Source).HasConversion<int>();
        b.Property(p => p.Title).HasMaxLength(200).IsRequired();
        b.Property(p => p.Topic).HasMaxLength(100);
        b.Property(p => p.Text).HasMaxLength(6000).IsRequired();
        b.Property(p => p.Translation).HasMaxLength(6000);
        b.Property(p => p.QuestionsJson).HasMaxLength(8000);
        b.HasOne(p => p.Language).WithMany().HasForeignKey(p => p.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => new { p.LanguageId, p.Type, p.Level, p.Status });
        b.HasQueryFilter(p => !p.IsDeleted);
    }
}

internal sealed class PracticeAttemptConfiguration : IEntityTypeConfiguration<PracticeAttempt>
{
    public void Configure(EntityTypeBuilder<PracticeAttempt> b)
    {
        b.ToTable("PracticeAttempts");
        b.Property(p => p.Type).HasConversion<int>();
        b.Property(p => p.PromptText).HasMaxLength(6000);
        b.Property(p => p.LearnerInput).HasMaxLength(6000);
        b.Property(p => p.AudioUrl).HasMaxLength(500);
        b.Property(p => p.FeedbackJson).HasMaxLength(16000);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(p => p.PracticeItem).WithMany().HasForeignKey(p => p.PracticeItemId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(p => new { p.UserId, p.Type, p.CreatedAt });
    }
}

internal sealed class UserWordConfiguration : IEntityTypeConfiguration<UserWord>
{
    public void Configure(EntityTypeBuilder<UserWord> b)
    {
        b.ToTable("UserWords");
        b.Property(w => w.Word).HasMaxLength(200).IsRequired();
        b.Property(w => w.Meaning).HasMaxLength(500).IsRequired();
        b.Property(w => w.Example).HasMaxLength(500);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(w => w.Language).WithMany().HasForeignKey(w => w.LanguageId).OnDelete(DeleteBehavior.Restrict);
        // SRS 7.6 index: due cards per learner.
        b.HasIndex(w => new { w.UserId, w.NextReviewAt });
        b.HasIndex(w => new { w.UserId, w.LanguageId, w.Word }).IsUnique();
    }
}

internal sealed class TranslationEntryConfiguration : IEntityTypeConfiguration<TranslationEntry>
{
    public void Configure(EntityTypeBuilder<TranslationEntry> b)
    {
        b.ToTable("Translations");
        b.Property(t => t.From).HasMaxLength(12);
        b.Property(t => t.To).HasMaxLength(12);
        b.Property(t => t.SourceText).HasMaxLength(2000).IsRequired();
        b.Property(t => t.TranslatedText).HasMaxLength(4000).IsRequired();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(t => new { t.UserId, t.CreatedAt });
    }
}

internal sealed class TranslationCacheConfiguration : IEntityTypeConfiguration<TranslationCacheEntry>
{
    public void Configure(EntityTypeBuilder<TranslationCacheEntry> b)
    {
        b.ToTable("TranslationCache");
        b.Property(t => t.Hash).HasMaxLength(64).IsRequired();
        b.Property(t => t.From).HasMaxLength(12);
        b.Property(t => t.To).HasMaxLength(12);
        b.Property(t => t.SourceText).HasMaxLength(2000).IsRequired();
        b.Property(t => t.TranslatedText).HasMaxLength(4000).IsRequired();
        b.Property(t => t.Provider).HasMaxLength(50);
        b.HasIndex(t => t.Hash).IsUnique();
    }
}
