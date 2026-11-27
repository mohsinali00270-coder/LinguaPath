using LinguaPath.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaPath.Infrastructure.Persistence.Configurations;

// Questions are soft-deleted but have no global query filter: old attempts must still show the questions they used.
// Bank and selection queries filter IsDeleted explicitly.

internal sealed class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> b)
    {
        b.ToTable("Questions");
        b.Property(q => q.Type).HasConversion<int>().HasColumnName("QuestionType");
        b.Property(q => q.Skill).HasConversion<int>();
        b.Property(q => q.Level).HasConversion<int>().HasColumnName("CefrLevel");
        b.Property(q => q.Status).HasConversion<int>();
        b.Property(q => q.Source).HasConversion<int>();
        b.Property(q => q.Stem).HasMaxLength(1000).IsRequired();
        b.Property(q => q.Prompt).HasMaxLength(500).HasColumnName("SourceSentence");
        b.Property(q => q.MediaUrl).HasMaxLength(500);
        b.Property(q => q.Explanation).HasMaxLength(1000);
        b.Property(q => q.SourceText).HasMaxLength(1000);
        b.HasOne(q => q.Language).WithMany().HasForeignKey(q => q.LanguageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(q => q.Course).WithMany().HasForeignKey(q => q.CourseId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(q => q.Module).WithMany().HasForeignKey(q => q.ModuleId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(q => q.Lecture).WithMany().HasForeignKey(q => q.LectureId).OnDelete(DeleteBehavior.NoAction);
        // SRS 7.4 index: filter the bank and build quizzes quickly.
        b.HasIndex(q => new { q.LanguageId, q.Level, q.Skill, q.Status });
        b.HasIndex(q => new { q.LectureId, q.Status });
    }
}

internal sealed class QuestionOptionConfiguration : IEntityTypeConfiguration<QuestionOption>
{
    public void Configure(EntityTypeBuilder<QuestionOption> b)
    {
        b.ToTable("QuestionOptions");
        b.Property(o => o.Text).HasMaxLength(500).IsRequired().HasColumnName("OptionText");
        b.Property(o => o.MatchKey).HasMaxLength(300);
        b.HasOne(o => o.Question).WithMany(q => q.Options).HasForeignKey(o => o.QuestionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(o => new { o.QuestionId, o.SortOrder });
    }
}

internal sealed class QuizConfiguration : IEntityTypeConfiguration<Quiz>
{
    public void Configure(EntityTypeBuilder<Quiz> b)
    {
        b.ToTable("Quizzes");
        b.Property(q => q.Type).HasConversion<int>().HasColumnName("QuizType");
        b.Property(q => q.Status).HasConversion<int>();
        b.Property(q => q.Title).HasMaxLength(200).IsRequired();
        b.Property(q => q.Instructions).HasMaxLength(1000);
        b.HasOne(q => q.Course).WithMany().HasForeignKey(q => q.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(q => new { q.CourseId, q.Type, q.MonthNo });
        b.HasQueryFilter(q => !q.IsDeleted);
    }
}

internal sealed class QuizSectionConfiguration : IEntityTypeConfiguration<QuizSection>
{
    public void Configure(EntityTypeBuilder<QuizSection> b)
    {
        b.ToTable("QuizSections");
        b.Property(s => s.Title).HasMaxLength(150).IsRequired();
        b.Property(s => s.Skill).HasConversion<int?>();
        b.Property(s => s.QuestionType).HasConversion<int?>();
        b.Property(s => s.SelectionMode).HasConversion<int>();
        b.HasOne(s => s.Quiz).WithMany(q => q.Sections).HasForeignKey(s => s.QuizId).OnDelete(DeleteBehavior.Cascade);
        b.HasQueryFilter(s => !s.Quiz.IsDeleted);
    }
}

internal sealed class QuizQuestionConfiguration : IEntityTypeConfiguration<QuizQuestion>
{
    public void Configure(EntityTypeBuilder<QuizQuestion> b)
    {
        b.ToTable("QuizQuestions");
        b.HasKey(x => new { x.SectionId, x.QuestionId });
        b.HasOne(x => x.Section).WithMany(s => s.Questions).HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.NoAction);
        b.HasQueryFilter(x => !x.Section.Quiz.IsDeleted);
    }
}

internal sealed class QuizAttemptConfiguration : IEntityTypeConfiguration<QuizAttempt>
{
    public void Configure(EntityTypeBuilder<QuizAttempt> b)
    {
        b.ToTable("QuizAttempts");
        b.Property(a => a.Type).HasConversion<int>().HasColumnName("QuizType");
        b.Property(a => a.Status).HasConversion<int>();
        b.Property(a => a.SuggestedLevel).HasConversion<int?>();
        b.Property(a => a.Title).HasMaxLength(200).IsRequired();
        b.Property(a => a.Score).HasPrecision(8, 2);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.Course).WithMany().HasForeignKey(a => a.CourseId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(a => a.LearnerCourse).WithMany().HasForeignKey(a => a.LearnerCourseId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(a => a.Quiz).WithMany().HasForeignKey(a => a.QuizId).OnDelete(DeleteBehavior.NoAction);
        // SRS 7.4 index: marks screen and "one active attempt" checks.
        b.HasIndex(a => new { a.UserId, a.LearnerCourseId, a.Type, a.Status });
        b.HasIndex(a => new { a.LearnerCourseId, a.ForDate });
        b.HasIndex(a => new { a.Status, a.ExpiresAt });
    }
}

internal sealed class AttemptAnswerConfiguration : IEntityTypeConfiguration<AttemptAnswer>
{
    public void Configure(EntityTypeBuilder<AttemptAnswer> b)
    {
        b.ToTable("AttemptAnswers");
        b.Property(a => a.SectionTitle).HasMaxLength(150);
        b.Property(a => a.OptionOrder).HasMaxLength(500);
        b.Property(a => a.AnswerText).HasMaxLength(4000);
        b.Property(a => a.SelectedOptionIds).HasMaxLength(500);
        b.Property(a => a.MatchAnswer).HasMaxLength(2000);
        b.Property(a => a.AudioUrl).HasMaxLength(500);
        b.Property(a => a.Feedback).HasMaxLength(1000);
        b.Property(a => a.ReviewedByEmail).HasMaxLength(256);
        b.Property(a => a.Score).HasPrecision(8, 2);
        b.Property(a => a.AiScore).HasPrecision(8, 2);
        b.Property(a => a.AiFeedback).HasMaxLength(2000);
        b.HasOne(a => a.Attempt).WithMany(x => x.Answers).HasForeignKey(a => a.AttemptId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.Question).WithMany().HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(a => new { a.AttemptId, a.SortOrder });
        b.HasIndex(a => a.NeedsReview);
    }
}

internal sealed class MarkOverrideConfiguration : IEntityTypeConfiguration<MarkOverride>
{
    public void Configure(EntityTypeBuilder<MarkOverride> b)
    {
        b.ToTable("MarkOverrides");
        b.Property(m => m.Reason).HasMaxLength(500).IsRequired();
        b.Property(m => m.ChangedByEmail).HasMaxLength(256);
        b.Property(m => m.OldScore).HasPrecision(8, 2);
        b.Property(m => m.NewScore).HasPrecision(8, 2);
        b.HasOne(m => m.AttemptAnswer).WithMany().HasForeignKey(m => m.AttemptAnswerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReviewCardConfiguration : IEntityTypeConfiguration<ReviewCard>
{
    public void Configure(EntityTypeBuilder<ReviewCard> b)
    {
        b.ToTable("ReviewCards");
        b.HasOne(c => c.LearnerCourse).WithMany().HasForeignKey(c => c.LearnerCourseId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.Question).WithMany().HasForeignKey(c => c.QuestionId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(c => new { c.LearnerCourseId, c.QuestionId }).IsUnique();
        b.HasIndex(c => new { c.LearnerCourseId, c.DueDate });
    }
}
