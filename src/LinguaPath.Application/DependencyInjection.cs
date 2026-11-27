using FluentValidation;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Auth;
using LinguaPath.Application.Features.Catalog;
using LinguaPath.Application.Features.Content;
using LinguaPath.Application.Features.Learning;
using LinguaPath.Application.Features.Profile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaPath.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddSingleton(TimeProvider.System);

        // Learner-facing
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<ILanguageCatalogService, LanguageCatalogService>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IAppVersionService, AppVersionService>();
        services.AddScoped<ICourseCatalogService, CourseCatalogService>();
        services.AddScoped<ILearnerCourseService, LearnerCourseService>();
        services.AddScoped<ILectureReaderService, LectureReaderService>();
        services.AddScoped<IPostFeedService, PostFeedService>();

        // Admin-facing (used in-process by the admin panel, and by /api/v1/admin endpoints in later phases)
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IAdminUserService, AdminUserService>();
        services.AddScoped<ILearnerAdminService, LearnerAdminService>();
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<ILanguageAdminService, LanguageAdminService>();
        services.AddScoped<ILanguageRequestAdminService, LanguageRequestAdminService>();
        services.AddScoped<ICourseAdminService, CourseAdminService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<ILectureContentAdminService, LectureContentAdminService>();
        services.AddScoped<IPostAdminService, PostAdminService>();
        services.AddScoped<IContentReportAdminService, ContentReportAdminService>();
        services.AddScoped<Features.Assessment.IAssessmentService, Features.Assessment.AssessmentService>();
        services.AddScoped<IQuestionAdminService, QuestionAdminService>();
        services.AddScoped<IQuizAdminService, QuizAdminService>();
        services.AddScoped<IGradingService, GradingService>();
        services.AddScoped<Features.Ai.IAiContentService, Features.Ai.AiContentService>();
        services.AddScoped<Features.Ai.IAiGenerationService, Features.Ai.AiGenerationService>();
        services.AddScoped<Features.Ai.IAiJobProcessor, Features.Ai.AiJobProcessor>();
        services.AddScoped<Features.Practice.IPracticeService, Features.Practice.PracticeService>();
        services.AddScoped<Features.Practice.ITranslationService, Features.Practice.TranslationService>();
        services.AddScoped<Features.Practice.IWordsService, Features.Practice.WordsService>();
        services.AddScoped<Features.Practice.IPracticeAdminService, Features.Practice.PracticeAdminService>();
        services.AddScoped<Features.Progress.PointsService>();
        services.AddScoped<Features.Progress.IPointsService>(sp => sp.GetRequiredService<Features.Progress.PointsService>());
        services.AddScoped<Features.Progress.IPointsAdminService>(sp => sp.GetRequiredService<Features.Progress.PointsService>());
        services.AddScoped<Features.Progress.CertificateService>();
        services.AddScoped<Features.Progress.ICertificateService>(sp => sp.GetRequiredService<Features.Progress.CertificateService>());
        services.AddScoped<Features.Progress.ICertificateAdminService>(sp => sp.GetRequiredService<Features.Progress.CertificateService>());
        services.AddScoped<Features.Progress.IProgressService, Features.Progress.ProgressService>();

        // Phase 8: social, chat, moderation. Hosts with the chat hub (the API) replace the no-op real-time ports.
        services.AddSingleton<Features.Moderation.ContentFilterCache>();
        services.TryAddSingleton<Features.Moderation.NullChatNotifier>();
        services.TryAddSingleton<Features.Moderation.IChatNotifier>(sp => sp.GetRequiredService<Features.Moderation.NullChatNotifier>());
        services.TryAddSingleton<Features.Moderation.IPresenceTracker>(sp => sp.GetRequiredService<Features.Moderation.NullChatNotifier>());
        services.TryAddSingleton<Features.Moderation.IUserSessionCache>(sp => sp.GetRequiredService<Features.Moderation.NullChatNotifier>());
        services.TryAddSingleton<Features.Moderation.IModerationQueue>(sp => sp.GetRequiredService<Features.Moderation.NullChatNotifier>());
        services.AddScoped<Features.Moderation.IModerationService, Features.Moderation.ModerationService>();
        services.AddScoped<Features.Moderation.IModerationAdminService, Features.Moderation.ModerationAdminService>();
        services.AddScoped<Features.Social.ISocialService, Features.Social.SocialService>();
        services.AddScoped<Features.Social.IChatService, Features.Social.ChatService>();

        // Phase 9: competitions, tournaments, leagues. The API replaces the no-op notifier with SignalR.
        services.TryAddSingleton<Features.Competitions.ICompetitionNotifier, Features.Competitions.NullCompetitionNotifier>();
        services.AddScoped<Features.Competitions.CompetitionService>();
        services.AddScoped<Features.Competitions.ICompetitionService>(sp => sp.GetRequiredService<Features.Competitions.CompetitionService>());
        services.AddScoped<Features.Competitions.ICompetitionEngine>(sp => sp.GetRequiredService<Features.Competitions.CompetitionService>());
        services.AddScoped<Features.Competitions.TournamentService>();
        services.AddScoped<Features.Competitions.ITournamentService>(sp => sp.GetRequiredService<Features.Competitions.TournamentService>());
        services.AddScoped<Features.Competitions.ITournamentAdminService>(sp => sp.GetRequiredService<Features.Competitions.TournamentService>());
        services.AddScoped<Features.Competitions.ILeagueService, Features.Competitions.LeagueService>();

        // Phase 10: inbox, reminders, retention clean-up, admin analytics.
        services.AddScoped<Features.Notifications.INotificationService, Features.Notifications.NotificationService>();
        services.AddScoped<Features.Notifications.IReminderService, Features.Notifications.ReminderService>();
        services.AddScoped<Features.Notifications.IMaintenanceService, Features.Notifications.MaintenanceService>();
        services.AddScoped<Features.Admin.IAnalyticsReportService, Features.Admin.AnalyticsReportService>();

        return services;
    }
}
