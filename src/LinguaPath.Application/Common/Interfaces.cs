using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<ApplicationUser> Users { get; }
    DbSet<ApplicationRole> Roles { get; }
    DbSet<IdentityUserRole<Guid>> UserRoles { get; }
    DbSet<IdentityUserLogin<Guid>> UserLogins { get; }

    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<UserDevice> UserDevices { get; }
    DbSet<EmailOtp> EmailOtps { get; }
    DbSet<BlockedIdentity> BlockedIdentities { get; }
    DbSet<Language> Languages { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<AuditLog> AuditLogs { get; }

    // Phase 2: courses and learner roadmaps
    DbSet<Course> Courses { get; }
    DbSet<CourseModule> CourseModules { get; }
    DbSet<Lecture> Lectures { get; }
    DbSet<LanguageRequest> LanguageRequests { get; }
    DbSet<LearnerCourse> LearnerCourses { get; }
    DbSet<LearnerRoadmapItem> LearnerRoadmapItems { get; }

    // Phase 3: lecture content, posts, reviews
    DbSet<LectureExample> LectureExamples { get; }
    DbSet<LectureVocabulary> LectureVocabulary { get; }
    DbSet<LectureLink> LectureLinks { get; }
    DbSet<LectureVersion> LectureVersions { get; }
    DbSet<Post> Posts { get; }
    DbSet<PostInteraction> PostInteractions { get; }
    DbSet<ReviewItem> ReviewItems { get; }
    DbSet<ContentReport> ContentReports { get; }
    DbSet<LectureBookmark> LectureBookmarks { get; }
    DbSet<LectureNote> LectureNotes { get; }
    DbSet<LectureRating> LectureRatings { get; }

    // Phase 4: assessment
    DbSet<Question> Questions { get; }
    DbSet<QuestionOption> QuestionOptions { get; }
    DbSet<Quiz> Quizzes { get; }
    DbSet<QuizSection> QuizSections { get; }
    DbSet<QuizQuestion> QuizQuestions { get; }
    DbSet<QuizAttempt> QuizAttempts { get; }
    DbSet<AttemptAnswer> AttemptAnswers { get; }
    DbSet<MarkOverride> MarkOverrides { get; }
    DbSet<ReviewCard> ReviewCards { get; }

    // Phase 5: AI
    DbSet<AiPromptTemplate> AiPromptTemplates { get; }
    DbSet<AiUsageLog> AiUsageLogs { get; }
    DbSet<AiGenerationRequest> AiGenerationRequests { get; }
    DbSet<AiGeneratedItem> AiGeneratedItems { get; }

    // Phase 6: practice and translation
    DbSet<PracticeItem> PracticeItems { get; }
    DbSet<PracticeAttempt> PracticeAttempts { get; }
    DbSet<UserWord> UserWords { get; }
    DbSet<TranslationEntry> TranslationEntries { get; }
    DbSet<TranslationCacheEntry> TranslationCache { get; }

    // Phase 7: points, streaks, badges, certificates
    DbSet<PointTransaction> PointTransactions { get; }
    DbSet<UserPointsSummary> UserPointsSummaries { get; }
    DbSet<PointRule> PointRules { get; }
    DbSet<UserStreak> UserStreaks { get; }
    DbSet<UserBadge> UserBadges { get; }
    DbSet<UserPrivacySettings> UserPrivacySettings { get; }
    DbSet<CertificateTemplate> CertificateTemplates { get; }
    DbSet<Certificate> Certificates { get; }

    // Phase 8: social, chat, moderation
    DbSet<Friendship> Friendships { get; }
    DbSet<UserBlock> UserBlocks { get; }
    DbSet<UserReport> UserReports { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationMember> ConversationMembers { get; }
    DbSet<Message> Messages { get; }
    DbSet<MessageRecipientState> MessageRecipientStates { get; }
    DbSet<MessageReaction> MessageReactions { get; }
    DbSet<AbusiveWord> AbusiveWords { get; }
    DbSet<BlockedLinkPattern> BlockedLinkPatterns { get; }
    DbSet<ModerationEvent> ModerationEvents { get; }
    DbSet<ModerationStrike> ModerationStrikes { get; }

    // Phase 9: competitions, tournaments, leagues
    DbSet<Competition> Competitions { get; }
    DbSet<CompetitionPlayer> CompetitionPlayers { get; }
    DbSet<CompetitionQuestion> CompetitionQuestions { get; }
    DbSet<CompetitionAnswer> CompetitionAnswers { get; }
    DbSet<CompetitionRating> CompetitionRatings { get; }
    DbSet<Tournament> Tournaments { get; }
    DbSet<TournamentEntry> TournamentEntries { get; }
    DbSet<League> Leagues { get; }
    DbSet<LeagueMember> LeagueMembers { get; }
    DbSet<Notification> Notifications { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets unsaved changes, e.g. after a save lost a race on a unique index.</summary>
    void DiscardChanges();
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(ApplicationUser user, IEnumerable<string> roles);
    string GenerateRefreshToken();
    string HashToken(string token);
}

public interface IOtpService
{
    /// <summary>Creates a new code (older unused codes for the same purpose are invalidated). Returns the plain code to email.</summary>
    Task<(string Code, DateTimeOffset ExpiresAt)> CreateAsync(Guid userId, OtpPurpose purpose, CancellationToken ct = default);

    /// <summary>Checks and consumes a code, counting failed attempts.</summary>
    Task<Result> VerifyAsync(Guid userId, OtpPurpose purpose, string code, CancellationToken ct = default);

    /// <summary>Seconds the user must still wait before another code can be sent (0 = can send now).</summary>
    Task<int> SecondsUntilResendAllowedAsync(Guid userId, OtpPurpose purpose, CancellationToken ct = default);
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

public sealed record ExternalUserInfo(string Provider, string Subject, string? Email, bool EmailVerified, string? Name);

public interface IExternalAuthValidator
{
    bool IsEnabled(string provider);
    Task<ExternalUserInfo?> ValidateAsync(string provider, string idToken, CancellationToken ct = default);
}

public interface IFileStorage
{
    /// <summary>Saves a file and returns its public URL.</summary>
    Task<string> SaveAsync(Stream content, string folder, string extension, string contentType, CancellationToken ct = default);
    Task DeleteAsync(string? url, CancellationToken ct = default);

    /// <summary>Reads a stored file back (e.g. exam recordings for speaking pre-grading). Null when it's gone.</summary>
    Task<byte[]?> ReadAsync(string url, int maxBytes, CancellationToken ct = default);
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}

public interface IAuditLogger
{
    Task LogAsync(string action, string entityType, string? entityId,
        object? oldValues = null, object? newValues = null, CancellationToken ct = default);
}

/// <summary>Removes scripts, event handlers and unknown tags from rich text before it is stored.</summary>
public interface IHtmlSanitizerService
{
    string Sanitize(string? html);
}

/// <summary>Excel template for bulk question import/export (FR-A65).</summary>
public interface IQuestionSpreadsheet
{
    byte[] Export(IEnumerable<QuestionSheetRow> rows);
    /// <summary>Reads the first worksheet. Rows that can't be read come back with an Error.</summary>
    IReadOnlyList<QuestionSheetRow> Read(Stream stream);
}

/// <summary>One spreadsheet row. Options: "text" per cell; prefix "*" marks a correct option; "left => right" for matching.</summary>
public sealed record QuestionSheetRow(
    int RowNumber, int? Id, string? Lecture, string Type, string Skill, string Level, int Difficulty, int Points,
    string Stem, string? Prompt, string? MediaUrl, string? Explanation, IReadOnlyList<string> Options, string? Error = null);

public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);
    Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Validates the value against the setting's data type, saves it and writes an audit entry.</summary>
    Task<Result> SetAsync(string key, string value, CancellationToken ct = default);
}
