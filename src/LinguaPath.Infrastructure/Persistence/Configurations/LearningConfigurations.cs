using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

internal sealed class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> b)
    {
        b.ToTable("Courses");
        b.Property(c => c.Title).HasMaxLength(150).IsRequired();
        b.Property(c => c.Description).HasMaxLength(2000);
        b.Property(c => c.CoverUrl).HasMaxLength(500);
        b.Property(c => c.StartLevel).HasConversion<int>();
        b.Property(c => c.EndLevel).HasConversion<int>();
        b.Property(c => c.Status).HasConversion<int>();
        b.Property(c => c.Source).HasConversion<int>();
        b.HasOne(c => c.Language).WithMany().HasForeignKey(c => c.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(c => new { c.LanguageId, c.Status });
        b.HasQueryFilter(c => !c.IsDeleted);
    }
}

internal sealed class CourseModuleConfiguration : IEntityTypeConfiguration<CourseModule>
{
    public void Configure(EntityTypeBuilder<CourseModule> b)
    {
        b.ToTable("Modules");
        b.Property(m => m.Title).HasMaxLength(150).IsRequired();
        b.Property(m => m.Objective).HasMaxLength(500);
        b.Property(m => m.Level).HasConversion<int>().HasColumnName("CefrLevel");
        b.HasOne(m => m.Course).WithMany(c => c.Modules).HasForeignKey(m => m.CourseId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CourseModule>().WithMany().HasForeignKey(m => m.PrerequisiteModuleId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(m => new { m.CourseId, m.SortOrder });
        b.HasQueryFilter(m => !m.IsDeleted);
    }
}

internal sealed class LectureConfiguration : IEntityTypeConfiguration<Lecture>
{
    public void Configure(EntityTypeBuilder<Lecture> b)
    {
        b.ToTable("Lectures");
        b.Property(l => l.Title).HasMaxLength(200).IsRequired();
        b.Property(l => l.Topic).HasMaxLength(150);
        b.Property(l => l.Objectives).HasMaxLength(1000);
        b.Property(l => l.CoverUrl).HasMaxLength(500);
        b.Property(l => l.Level).HasConversion<int>().HasColumnName("CefrLevel");
        b.Property(l => l.Status).HasConversion<int>();
        b.Property(l => l.Source).HasConversion<int>();
        b.HasOne(l => l.Module).WithMany(m => m.Lectures).HasForeignKey(l => l.ModuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(l => new { l.ModuleId, l.SortOrder });
        b.HasIndex(l => new { l.Status, l.IsDeleted });
        b.HasQueryFilter(l => !l.IsDeleted);
    }
}

internal sealed class LanguageRequestConfiguration : IEntityTypeConfiguration<LanguageRequest>
{
    public void Configure(EntityTypeBuilder<LanguageRequest> b)
    {
        b.ToTable("LanguageRequests");
        b.Property(r => r.LanguageName).HasMaxLength(80).IsRequired();
        b.Property(r => r.NormalizedName).HasMaxLength(80).IsRequired();
        b.Property(r => r.Note).HasMaxLength(500);
        b.Property(r => r.Status).HasConversion<int>();
        b.HasOne(r => r.Language).WithMany().HasForeignKey(r => r.LanguageId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.RequestedByUserId).OnDelete(DeleteBehavior.Cascade);
        // One request per learner per language name.
        b.HasIndex(r => new { r.RequestedByUserId, r.NormalizedName }).IsUnique();
        b.HasIndex(r => new { r.NormalizedName, r.Status });
    }
}

internal sealed class LearnerCourseConfiguration : IEntityTypeConfiguration<LearnerCourse>
{
    public void Configure(EntityTypeBuilder<LearnerCourse> b)
    {
        b.ToTable("LearnerCourses");
        b.Property(c => c.CurrentLevel).HasConversion<int>();
        b.Property(c => c.TargetLevel).HasConversion<int>();
        b.Property(c => c.Goal).HasConversion<int>();
        b.Property(c => c.ExamName).HasMaxLength(100);
        b.Property(c => c.StudyDays).HasConversion<int>();
        b.Property(c => c.FocusSkills).HasConversion<int>();
        b.Property(c => c.Status).HasConversion<int>();
        b.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.Course).WithMany().HasForeignKey(c => c.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(c => new { c.UserId, c.Status });
        b.HasIndex(c => new { c.UserId, c.LanguageId });
    }
}

internal sealed class LearnerRoadmapItemConfiguration : IEntityTypeConfiguration<LearnerRoadmapItem>
{
    public void Configure(EntityTypeBuilder<LearnerRoadmapItem> b)
    {
        b.ToTable("LearnerRoadmapItems");
        b.Property(i => i.State).HasConversion<int>();
        b.HasOne(i => i.LearnerCourse).WithMany(c => c.Items).HasForeignKey(i => i.LearnerCourseId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(i => i.Lecture).WithMany().HasForeignKey(i => i.LectureId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(i => new { i.LearnerCourseId, i.State });           // SRS 7.6 index
        b.HasIndex(i => new { i.LearnerCourseId, i.LectureId }).IsUnique();
        b.HasIndex(i => new { i.LearnerCourseId, i.PlannedDate });
        // Lectures are soft-deleted; hide roadmap rows whose lecture is gone so query filters stay consistent.
        b.HasQueryFilter(i => !i.Lecture.IsDeleted);
    }
}
