using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Common;
using LinguaPath.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LinguaPath.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser? currentUser = null, TimeProvider? clock = null)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IApplicationDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<EmailOtp> EmailOtps => Set<EmailOtp>();
    public DbSet<BlockedIdentity> BlockedIdentities => Set<BlockedIdentity>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseModule> CourseModules => Set<CourseModule>();
    public DbSet<Lecture> Lectures => Set<Lecture>();
    public DbSet<LanguageRequest> LanguageRequests => Set<LanguageRequest>();
    public DbSet<LearnerCourse> LearnerCourses => Set<LearnerCourse>();
    public DbSet<LearnerRoadmapItem> LearnerRoadmapItems => Set<LearnerRoadmapItem>();
    public DbSet<LectureExample> LectureExamples => Set<LectureExample>();
    public DbSet<LectureVocabulary> LectureVocabulary => Set<LectureVocabulary>();
    public DbSet<LectureLink> LectureLinks => Set<LectureLink>();
    public DbSet<LectureVersion> LectureVersions => Set<LectureVersion>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostInteraction> PostInteractions => Set<PostInteraction>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();
    public DbSet<ContentReport> ContentReports => Set<ContentReport>();
    public DbSet<LectureBookmark> LectureBookmarks => Set<LectureBookmark>();
    public DbSet<LectureNote> LectureNotes => Set<LectureNote>();
    public DbSet<LectureRating> LectureRatings => Set<LectureRating>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<QuizSection> QuizSections => Set<QuizSection>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<AttemptAnswer> AttemptAnswers => Set<AttemptAnswer>();
    public DbSet<MarkOverride> MarkOverrides => Set<MarkOverride>();
    public DbSet<ReviewCard> ReviewCards => Set<ReviewCard>();
    public DbSet<AiPromptTemplate> AiPromptTemplates => Set<AiPromptTemplate>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<AiGenerationRequest> AiGenerationRequests => Set<AiGenerationRequest>();
    public DbSet<AiGeneratedItem> AiGeneratedItems => Set<AiGeneratedItem>();
    public DbSet<PracticeItem> PracticeItems => Set<PracticeItem>();
    public DbSet<PracticeAttempt> PracticeAttempts => Set<PracticeAttempt>();
    public DbSet<UserWord> UserWords => Set<UserWord>();
    public DbSet<TranslationEntry> TranslationEntries => Set<TranslationEntry>();
    public DbSet<TranslationCacheEntry> TranslationCache => Set<TranslationCacheEntry>();
    public DbSet<PointTransaction> PointTransactions => Set<PointTransaction>();
    public DbSet<UserPointsSummary> UserPointsSummaries => Set<UserPointsSummary>();
    public DbSet<PointRule> PointRules => Set<PointRule>();
    public DbSet<UserStreak> UserStreaks => Set<UserStreak>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<UserPrivacySettings> UserPrivacySettings => Set<UserPrivacySettings>();
    public DbSet<CertificateTemplate> CertificateTemplates => Set<CertificateTemplate>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<UserReport> UserReports => Set<UserReport>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageRecipientState> MessageRecipientStates => Set<MessageRecipientState>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<AbusiveWord> AbusiveWords => Set<AbusiveWord>();
    public DbSet<BlockedLinkPattern> BlockedLinkPatterns => Set<BlockedLinkPattern>();
    public DbSet<ModerationEvent> ModerationEvents => Set<ModerationEvent>();
    public DbSet<ModerationStrike> ModerationStrikes => Set<ModerationStrike>();
    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<CompetitionPlayer> CompetitionPlayers => Set<CompetitionPlayer>();
    public DbSet<CompetitionQuestion> CompetitionQuestions => Set<CompetitionQuestion>();
    public DbSet<CompetitionAnswer> CompetitionAnswers => Set<CompetitionAnswer>();
    public DbSet<CompetitionRating> CompetitionRatings => Set<CompetitionRating>();
    public DbSet<Tournament> Tournaments => Set<Tournament>();
    public DbSet<TournamentEntry> TournamentEntries => Set<TournamentEntry>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<LeagueMember> LeagueMembers => Set<LeagueMember>();
    public DbSet<Notification> Notifications => Set<Notification>();

    private bool IsSqlite => Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Unicode everywhere for RTL and CJK scripts (NFR-11): EF maps string to NVARCHAR on SQL Server by default.
        if (IsSqlite)
        {
            // SQLite (used for tests / quick local runs) cannot compare or sort DateTimeOffset; store as ticks instead.
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    public void DiscardChanges() => ChangeTracker.Clear();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAuditFields();
        return base.SaveChanges();
    }

    private void StampAuditFields()
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var userId = currentUser?.UserId;

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAt == default) entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy ??= userId;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = userId;
                    break;
            }
        }

        // Soft delete: a Remove() on a soft-deletable entity becomes an update.
        foreach (var entry in ChangeTracker.Entries<ISoftDelete>().Where(e => e.State == EntityState.Deleted))
        {
            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
        }
    }
}
