using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class LectureExampleConfiguration : IEntityTypeConfiguration<LectureExample>
{
    public void Configure(EntityTypeBuilder<LectureExample> b)
    {
        b.ToTable("LectureExamples");
        b.Property(e => e.TargetText).HasMaxLength(500).IsRequired();
        b.Property(e => e.Translation).HasMaxLength(500);
        b.Property(e => e.Note).HasMaxLength(300);
        b.Property(e => e.AudioUrl).HasMaxLength(500);
        b.HasOne(e => e.Lecture).WithMany(l => l.Examples).HasForeignKey(e => e.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.LectureId, e.SortOrder });
        b.HasQueryFilter(e => !e.Lecture.IsDeleted);
    }
}

internal sealed class LectureVocabularyConfiguration : IEntityTypeConfiguration<LectureVocabulary>
{
    public void Configure(EntityTypeBuilder<LectureVocabulary> b)
    {
        b.ToTable("LectureVocabulary");
        b.Property(v => v.Word).HasMaxLength(100).IsRequired();
        b.Property(v => v.Meaning).HasMaxLength(300).IsRequired();
        b.Property(v => v.Pronunciation).HasMaxLength(150);
        b.Property(v => v.PartOfSpeech).HasMaxLength(30);
        b.Property(v => v.AudioUrl).HasMaxLength(500);
        b.HasOne(v => v.Lecture).WithMany(l => l.Vocabulary).HasForeignKey(v => v.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(v => new { v.LectureId, v.SortOrder });
        b.HasQueryFilter(v => !v.Lecture.IsDeleted);
    }
}

internal sealed class LectureLinkConfiguration : IEntityTypeConfiguration<LectureLink>
{
    public void Configure(EntityTypeBuilder<LectureLink> b)
    {
        b.ToTable("LectureLinks");
        b.Property(l => l.Title).HasMaxLength(200).IsRequired();
        b.Property(l => l.Url).HasMaxLength(1000).IsRequired();
        b.Property(l => l.LinkType).HasConversion<int>();
        b.HasOne(l => l.Lecture).WithMany(x => x.Links).HasForeignKey(l => l.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasQueryFilter(l => !l.Lecture.IsDeleted);
    }
}

internal sealed class LectureVersionConfiguration : IEntityTypeConfiguration<LectureVersion>
{
    public void Configure(EntityTypeBuilder<LectureVersion> b)
    {
        b.ToTable("LectureVersions");
        b.Property(v => v.Title).HasMaxLength(200).IsRequired();
        b.Property(v => v.Note).HasMaxLength(300);
        b.Property(v => v.ChangedByEmail).HasMaxLength(256);
        b.HasOne<Lecture>().WithMany().HasForeignKey(v => v.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(v => new { v.LectureId, v.Version }).IsUnique();
    }
}

internal sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> b)
    {
        b.ToTable("Posts");
        b.Property(p => p.Title).HasMaxLength(200).IsRequired();
        b.Property(p => p.Body).HasMaxLength(4000);
        b.Property(p => p.Url).HasMaxLength(1000);
        b.Property(p => p.ImageUrl).HasMaxLength(500);
        b.Property(p => p.Tags).HasMaxLength(300);
        b.Property(p => p.PostType).HasConversion<int>();
        b.Property(p => p.Level).HasConversion<int?>().HasColumnName("CefrLevel");
        b.Property(p => p.Status).HasConversion<int>();
        b.Property(p => p.Source).HasConversion<int>();
        b.HasOne(p => p.Language).WithMany().HasForeignKey(p => p.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(p => p.Lecture).WithMany().HasForeignKey(p => p.LectureId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(p => new { p.LanguageId, p.Status, p.PublishAt });
        b.HasQueryFilter(p => !p.IsDeleted);
    }
}

internal sealed class PostInteractionConfiguration : IEntityTypeConfiguration<PostInteraction>
{
    public void Configure(EntityTypeBuilder<PostInteraction> b)
    {
        b.ToTable("PostInteractions");
        b.HasKey(i => new { i.PostId, i.UserId });
        b.HasOne(i => i.Post).WithMany().HasForeignKey(i => i.PostId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(i => new { i.UserId, i.Saved });
        b.HasQueryFilter(i => !i.Post.IsDeleted);
    }
}

internal sealed class ReviewItemConfiguration : IEntityTypeConfiguration<ReviewItem>
{
    public void Configure(EntityTypeBuilder<ReviewItem> b)
    {
        b.ToTable("ReviewItems");
        b.Property(r => r.EntityType).HasConversion<int>();
        b.Property(r => r.Status).HasConversion<int>();
        b.Property(r => r.Comment).HasMaxLength(1000);
        b.Property(r => r.SubmittedByEmail).HasMaxLength(256);
        b.Property(r => r.ReviewerEmail).HasMaxLength(256);
        b.HasIndex(r => new { r.Status, r.SubmittedAt });
        b.HasIndex(r => new { r.EntityType, r.EntityId });
    }
}

internal sealed class ContentReportConfiguration : IEntityTypeConfiguration<ContentReport>
{
    public void Configure(EntityTypeBuilder<ContentReport> b)
    {
        b.ToTable("ContentReports");
        b.Property(r => r.EntityType).HasConversion<int>();
        b.Property(r => r.Status).HasConversion<int>();
        b.Property(r => r.Message).HasMaxLength(1000).IsRequired();
        b.Property(r => r.ResolutionNote).HasMaxLength(500);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => new { r.Status, r.CreatedAt });
    }
}

internal sealed class LectureBookmarkConfiguration : IEntityTypeConfiguration<LectureBookmark>
{
    public void Configure(EntityTypeBuilder<LectureBookmark> b)
    {
        b.ToTable("LectureBookmarks");
        b.HasKey(x => new { x.UserId, x.LectureId });
        b.HasOne(x => x.Lecture).WithMany().HasForeignKey(x => x.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasQueryFilter(x => !x.Lecture.IsDeleted);
    }
}

internal sealed class LectureNoteConfiguration : IEntityTypeConfiguration<LectureNote>
{
    public void Configure(EntityTypeBuilder<LectureNote> b)
    {
        b.ToTable("LectureNotes");
        b.HasKey(x => new { x.UserId, x.LectureId });
        b.Property(x => x.NoteText).HasMaxLength(2000).IsRequired();
        b.HasOne<Lecture>().WithMany().HasForeignKey(x => x.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LectureRatingConfiguration : IEntityTypeConfiguration<LectureRating>
{
    public void Configure(EntityTypeBuilder<LectureRating> b)
    {
        b.ToTable("LectureRatings");
        b.HasKey(x => new { x.UserId, x.LectureId });
        b.Property(x => x.Comment).HasMaxLength(500);
        b.HasOne<Lecture>().WithMany().HasForeignKey(x => x.LectureId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.LectureId);
    }
}
