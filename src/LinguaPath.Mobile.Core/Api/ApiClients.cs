using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Catalog;
using LinguaPath.Shared.Contracts.Learning;
using LinguaPath.Shared.Contracts.Profile;
using Refit;

namespace LinguaPath.Mobile.Core.Api;

/// <summary>Anonymous auth endpoints. This client has no auth handler, so it is also used for token refresh.</summary>
public interface IAuthApi
{
    [Post("/api/v1/auth/register")]
    Task<ApiResponse<RegisterResponse>> RegisterAsync([Body] RegisterRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/verify-email")]
    Task<ApiResponse<AuthResponse>> VerifyEmailAsync([Body] VerifyEmailRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/resend-otp")]
    Task<ApiResponse<ResendOtpResponse>> ResendOtpAsync([Body] ResendOtpRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/login")]
    Task<ApiResponse<AuthResponse>> LoginAsync([Body] LoginRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/google")]
    Task<ApiResponse<AuthResponse>> GoogleAsync([Body] ExternalLoginRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/apple")]
    Task<ApiResponse<AuthResponse>> AppleAsync([Body] ExternalLoginRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/refresh")]
    Task<ApiResponse<AuthResponse>> RefreshAsync([Body] RefreshTokenRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/forgot-password")]
    Task<ApiResponse> ForgotPasswordAsync([Body] ForgotPasswordRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/reset-password")]
    Task<ApiResponse> ResetPasswordAsync([Body] ResetPasswordRequest request, CancellationToken ct = default);

    [Get("/api/v1/languages")]
    Task<ApiResponse<List<LanguageDto>>> GetLanguagesAsync(bool all = false, CancellationToken ct = default);

    [Get("/api/v1/app/version-check")]
    Task<ApiResponse<VersionCheckResponse>> VersionCheckAsync(DevicePlatform platform, string version, CancellationToken ct = default);
}

/// <summary>Endpoints that need the signed-in learner's access token.</summary>
public interface IAccountApi
{
    [Post("/api/v1/auth/logout")]
    Task<ApiResponse> LogoutAsync([Body] LogoutRequest request, CancellationToken ct = default);

    [Post("/api/v1/auth/logout-all")]
    Task<ApiResponse> LogoutAllAsync(CancellationToken ct = default);

    [Get("/api/v1/me")]
    Task<ApiResponse<UserProfileDto>> GetProfileAsync(CancellationToken ct = default);

    [Put("/api/v1/me")]
    Task<ApiResponse<UserProfileDto>> UpdateProfileAsync([Body] UpdateProfileRequest request, CancellationToken ct = default);

    [Multipart]
    [Post("/api/v1/me/photo")]
    Task<ApiResponse<PhotoUploadResponse>> UploadPhotoAsync([AliasAs("file")] StreamPart file, CancellationToken ct = default);

    [Post("/api/v1/me/change-password")]
    Task<ApiResponse> ChangePasswordAsync([Body] ChangePasswordRequest request, CancellationToken ct = default);

    [Delete("/api/v1/me")]
    Task<ApiResponse> DeleteAccountAsync([Body] DeleteAccountRequest request, CancellationToken ct = default);

    [Get("/api/v1/me/export")]
    Task<string> ExportMyDataAsync(CancellationToken ct = default);

    [Post("/api/v1/devices")]
    Task<ApiResponse> RegisterDeviceAsync([Body] RegisterDeviceRequest request, CancellationToken ct = default);
}

/// <summary>Courses, onboarding and the learner's roadmap (authenticated).</summary>
public interface ILearningApi
{
    [Get("/api/v1/languages")]
    Task<ApiResponse<List<LanguageDto>>> GetLanguagesAsync(CancellationToken ct = default);

    [Get("/api/v1/languages/{languageId}/courses")]
    Task<ApiResponse<List<CourseDto>>> GetCoursesAsync(int languageId, CancellationToken ct = default);

    [Post("/api/v1/languages/requests")]
    Task<ApiResponse> RequestLanguageAsync([Body] CreateLanguageRequest request, CancellationToken ct = default);

    [Post("/api/v1/learner-courses/feasibility")]
    Task<ApiResponse<FeasibilityResponse>> CheckPlanAsync([Body] LearningPlanRequest request, CancellationToken ct = default);

    [Post("/api/v1/learner-courses")]
    Task<ApiResponse<EnrolResponse>> EnrolAsync([Body] LearningPlanRequest request, CancellationToken ct = default);

    [Get("/api/v1/learner-courses")]
    Task<ApiResponse<List<LearnerCourseDto>>> GetMyCoursesAsync(CancellationToken ct = default);

    [Get("/api/v1/learner-courses/{id}")]
    Task<ApiResponse<LearnerCourseDto>> GetMyCourseAsync(int id, CancellationToken ct = default);

    [Put("/api/v1/learner-courses/{id}")]
    Task<ApiResponse<EnrolResponse>> UpdatePlanAsync(int id, [Body] UpdateLearningPlanRequest request, CancellationToken ct = default);

    [Get("/api/v1/learner-courses/{id}/today")]
    Task<ApiResponse<TodayDto>> GetTodayAsync(int id, CancellationToken ct = default);

    [Get("/api/v1/learner-courses/{id}/roadmap")]
    Task<ApiResponse<RoadmapDto>> GetRoadmapAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/learner-courses/{id}/pause")]
    Task<ApiResponse<LearnerCourseDto>> PauseAsync(int id, [Body] PauseCourseRequest request, CancellationToken ct = default);

    [Post("/api/v1/learner-courses/{id}/resume")]
    Task<ApiResponse<LearnerCourseDto>> ResumeAsync(int id, CancellationToken ct = default);
}

/// <summary>Lecture reader and posts (Phase 3).</summary>
public interface IContentApi
{
    [Get("/api/v1/lectures/{id}")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Content.LectureDto>> GetLectureAsync(int id, [Query] int? learnerCourseId, CancellationToken ct = default);

    [Post("/api/v1/lectures/{id}/progress")]
    Task<ApiResponse> AddProgressAsync(int id, [Body] LinguaPath.Shared.Contracts.Content.LectureProgressRequest request, CancellationToken ct = default);

    [Post("/api/v1/lectures/{id}/complete")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Content.CompleteLectureResponse>> CompleteAsync(int id, [Body] LinguaPath.Shared.Contracts.Content.CompleteLectureRequest request, CancellationToken ct = default);

    [Get("/api/v1/lectures/bookmarks")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Content.BookmarkDto>>> GetBookmarksAsync(CancellationToken ct = default);

    [Post("/api/v1/lectures/{id}/bookmark")]
    Task<ApiResponse> AddBookmarkAsync(int id, CancellationToken ct = default);

    [Delete("/api/v1/lectures/{id}/bookmark")]
    Task<ApiResponse> RemoveBookmarkAsync(int id, CancellationToken ct = default);

    [Put("/api/v1/lectures/{id}/note")]
    Task<ApiResponse> SaveNoteAsync(int id, [Body] LinguaPath.Shared.Contracts.Content.SaveNoteRequest request, CancellationToken ct = default);

    [Post("/api/v1/lectures/{id}/rating")]
    Task<ApiResponse> RateAsync(int id, [Body] LinguaPath.Shared.Contracts.Content.RateLectureRequest request, CancellationToken ct = default);

    [Post("/api/v1/lectures/{id}/report")]
    Task<ApiResponse> ReportLectureAsync(int id, [Body] LinguaPath.Shared.Contracts.Content.ReportContentRequest request, CancellationToken ct = default);

    [Get("/api/v1/posts")]
    Task<ApiResponse<PagedResult<LinguaPath.Shared.Contracts.Content.PostDto>>> GetFeedAsync(
        [Query] int? languageId = null, [Query] LinguaPath.Shared.PostType? type = null, [Query] bool? saved = null,
        [Query] int page = 1, [Query] int pageSize = 20, CancellationToken ct = default);

    [Post("/api/v1/posts/{id}/like")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Content.PostInteractionResponse>> ToggleLikeAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/posts/{id}/save")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Content.PostInteractionResponse>> ToggleSaveAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/posts/{id}/report")]
    Task<ApiResponse> ReportPostAsync(int id, [Body] LinguaPath.Shared.Contracts.Content.ReportContentRequest request, CancellationToken ct = default);
}

/// <summary>Daily quiz, exams, placement test, answers and marks (Phase 4).</summary>
public interface IAssessmentApi
{
    [Get("/api/v1/learner-courses/{id}/daily-quiz")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.DailyQuizDto>> GetDailyQuizAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/learner-courses/{id}/daily-quiz/start")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptDto>> StartDailyQuizAsync(int id, [Query] bool practice = false, CancellationToken ct = default);

    [Get("/api/v1/learner-courses/{id}/exams")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Assessment.ExamSummaryDto>>> GetExamsAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/learner-courses/{id}/exams/{quizId}/start")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptDto>> StartExamAsync(int id, int quizId, CancellationToken ct = default);

    [Get("/api/v1/learner-courses/{id}/marks")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.MarksDto>> GetMarksAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/courses/{courseId}/placement-test")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptDto>> StartPlacementAsync(int courseId, CancellationToken ct = default);

    [Get("/api/v1/attempts/{id}")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptDto>> GetAttemptAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/attempts/{id}/answers")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AnswerFeedbackDto>> SaveAnswerAsync(int id, [Body] LinguaPath.Shared.Contracts.Assessment.SaveAnswerRequest request, CancellationToken ct = default);

    [Multipart]
    [Post("/api/v1/attempts/{id}/answers/{questionId}/audio")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AnswerFeedbackDto>> UploadAudioAsync(int id, int questionId, [AliasAs("file")] StreamPart file, CancellationToken ct = default);

    [Post("/api/v1/attempts/{id}/submit")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptResultDto>> SubmitAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/attempts/{id}/events")]
    Task<ApiResponse> LogEventAsync(int id, [Body] LinguaPath.Shared.Contracts.Assessment.AttemptEventRequest request, CancellationToken ct = default);

    [Get("/api/v1/attempts/{id}/result")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptResultDto>> GetResultAsync(int id, CancellationToken ct = default);

    [Get("/api/v1/attempts/{id}/review")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Assessment.AttemptReviewDto>> GetReviewAsync(int id, CancellationToken ct = default);
}

/// <summary>Practice tab, translation and "My words" (FR-L90 .. FR-L94, FR-L100 .. FR-L104).</summary>
public interface IPracticeApi
{
    [Get("/api/v1/practice/summary")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.PracticeSummaryDto>> GetSummaryAsync([Query] int? languageId = null, CancellationToken ct = default);

    [Get("/api/v1/practice/{type}/next")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.PracticeItemDto>> NextAsync(PracticeType type, [Query] int? languageId = null, [Query] int? skip = null,
        CancellationToken ct = default);

    [Post("/api/v1/practice/read/{itemId}/answers")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.ReadResultDto>> AnswerReadingAsync(int itemId,
        [Body] LinguaPath.Shared.Contracts.Practice.ReadAnswersRequest request, CancellationToken ct = default);

    [Post("/api/v1/practice/write/check")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.WriteCheckDto>> CheckWritingAsync([Body] LinguaPath.Shared.Contracts.Practice.WriteCheckRequest request,
        CancellationToken ct = default);

    [Multipart]
    [Post("/api/v1/practice/speak/assess")]
    /// <remarks>Form fields are strings so empty values are sent as empty (the API reads "" as "none").</remarks>
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.SpeakResultDto>> AssessSpeakingAsync([AliasAs("languageId")] string languageId,
        [AliasAs("itemId")] string itemId, [AliasAs("referenceText")] string referenceText, [AliasAs("audio")] StreamPart audio, CancellationToken ct = default);

    [Post("/api/v1/practice/listen/{itemId}/check")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.ListenResultDto>> CheckListeningAsync(int itemId,
        [Body] LinguaPath.Shared.Contracts.Practice.ListenCheckRequest request, CancellationToken ct = default);

    [Post("/api/v1/practice/conversation")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Practice.ConversationReplyDto>> ConverseAsync(
        [Body] LinguaPath.Shared.Contracts.Practice.ConversationRequest request, CancellationToken ct = default);

    [Post("/api/v1/translate")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Translation.TranslationDto>> TranslateAsync(
        [Body] LinguaPath.Shared.Contracts.Translation.TranslateRequest request, CancellationToken ct = default);

    [Multipart]
    [Post("/api/v1/translate/speech")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Translation.SpeechTranslationDto>> TranslateSpeechAsync([AliasAs("from")] string from,
        [AliasAs("to")] string to, [AliasAs("audio")] StreamPart audio, CancellationToken ct = default);

    [Get("/api/v1/translate/history")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Translation.TranslationDto>>> GetTranslationHistoryAsync(CancellationToken ct = default);

    [Delete("/api/v1/translate/history")]
    Task<ApiResponse> ClearTranslationHistoryAsync(CancellationToken ct = default);

    [Get("/api/v1/translate/languages")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Translation.TranslationLanguageDto>>> GetTranslationLanguagesAsync(CancellationToken ct = default);

    [Get("/api/v1/words")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Words.UserWordDto>>> GetWordsAsync([Query] int? languageId = null, [Query] bool due = false,
        [Query] string? search = null, CancellationToken ct = default);

    [Get("/api/v1/words/summary")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Words.WordsSummaryDto>> GetWordsSummaryAsync([Query] int? languageId = null, CancellationToken ct = default);

    [Post("/api/v1/words")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Words.UserWordDto>> SaveWordAsync([Body] LinguaPath.Shared.Contracts.Words.SaveWordRequest request,
        CancellationToken ct = default);

    [Post("/api/v1/words/{id}/review")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Words.UserWordDto>> ReviewWordAsync(int id, [Body] LinguaPath.Shared.Contracts.Words.ReviewWordRequest request,
        CancellationToken ct = default);

    [Delete("/api/v1/words/{id}")]
    Task<ApiResponse> DeleteWordAsync(int id, CancellationToken ct = default);
}

/// <summary>Points, streak, badges, leaderboards and certificates (Phase 7).</summary>
public interface IProgressApi
{
    [Get("/api/v1/points")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Progress.PointsSummaryDto>> GetPointsAsync(CancellationToken ct = default);

    [Get("/api/v1/points/history")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Progress.PointTransactionDto>>> GetHistoryAsync([Query] int page = 1, CancellationToken ct = default);

    [Get("/api/v1/leaderboard")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Progress.LeaderboardDto>> GetLeaderboardAsync([Query] LeaderboardPeriod period,
        [Query] int? languageId = null, [Query] string? scope = null, CancellationToken ct = default);

    [Put("/api/v1/me/leaderboard-visibility")]
    Task<ApiResponse> SetLeaderboardVisibilityAsync([Body] LinguaPath.Shared.Contracts.Progress.LeaderboardVisibilityRequest request,
        CancellationToken ct = default);

    [Get("/api/v1/certificates")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Certificates.CertificateDto>>> GetCertificatesAsync(CancellationToken ct = default);

    /// <summary>The PDF bytes; errors come back as a non-success status.</summary>
    [Get("/api/v1/certificates/{id}/pdf")]
    Task<HttpResponseMessage> DownloadCertificateAsync(int id, CancellationToken ct = default);

    [Get("/api/v1/learner-courses/{learnerCourseId}/certificate-progress")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Certificates.CertificateProgressDto>> GetCertificateProgressAsync(int learnerCourseId,
        CancellationToken ct = default);
}

/// <summary>Phase 8: profiles, friends, blocks, reports, privacy and chat (REST side; live events come from <see cref="Services.IChatConnection"/>).</summary>
public interface ISocialApi
{
    // ---- people
    [Get("/api/v1/users/search")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Social.UserCardDto>>> SearchUsersAsync([Query] string? q, [Query] int? languageId = null,
        CancellationToken ct = default);

    [Get("/api/v1/users/{userId}/profile")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Social.PublicProfileDto>> GetProfileAsync(Guid userId, CancellationToken ct = default);

    [Get("/api/v1/friends")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Social.FriendsDto>> GetFriendsAsync(CancellationToken ct = default);

    [Post("/api/v1/friends/requests")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Social.FriendRequestDto>> SendFriendRequestAsync([Body] LinguaPath.Shared.Contracts.Social.SendFriendRequest request,
        CancellationToken ct = default);

    [Post("/api/v1/friends/requests/{id}/respond")]
    Task<ApiResponse> RespondFriendRequestAsync(int id, [Body] LinguaPath.Shared.Contracts.Social.RespondFriendRequest request, CancellationToken ct = default);

    [Delete("/api/v1/friends/{userId}")]
    Task<ApiResponse> RemoveFriendAsync(Guid userId, CancellationToken ct = default);

    [Get("/api/v1/blocks")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Social.BlockedUserDto>>> GetBlockedAsync(CancellationToken ct = default);

    [Post("/api/v1/blocks/{userId}")]
    Task<ApiResponse> BlockAsync(Guid userId, CancellationToken ct = default);

    [Delete("/api/v1/blocks/{userId}")]
    Task<ApiResponse> UnblockAsync(Guid userId, CancellationToken ct = default);

    [Post("/api/v1/reports")]
    Task<ApiResponse> ReportAsync([Body] LinguaPath.Shared.Contracts.Social.ReportRequest request, CancellationToken ct = default);

    [Get("/api/v1/me/privacy")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Social.PrivacyDto>> GetPrivacyAsync(CancellationToken ct = default);

    [Put("/api/v1/me/privacy")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Social.PrivacyDto>> UpdatePrivacyAsync([Body] LinguaPath.Shared.Contracts.Social.PrivacyDto request,
        CancellationToken ct = default);

    [Get("/api/v1/me/moderation")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.MyModerationDto>> GetModerationAsync(CancellationToken ct = default);

    [Post("/api/v1/me/moderation/acknowledge")]
    Task<ApiResponse> AcknowledgeWarningAsync(CancellationToken ct = default);

    // ---- chats
    [Get("/api/v1/conversations")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Chat.ConversationSummaryDto>>> GetConversationsAsync([Query] bool archived = false,
        CancellationToken ct = default);

    [Post("/api/v1/conversations/direct")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.ConversationDto>> OpenDirectAsync([Body] LinguaPath.Shared.Contracts.Chat.CreateDirectRequest request,
        CancellationToken ct = default);

    [Post("/api/v1/conversations/groups")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.ConversationDto>> CreateGroupAsync([Body] LinguaPath.Shared.Contracts.Chat.CreateGroupRequest request,
        CancellationToken ct = default);

    [Get("/api/v1/conversations/{id}")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.ConversationDto>> GetConversationAsync(int id, CancellationToken ct = default);

    [Patch("/api/v1/conversations/{id}")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.ConversationDto>> UpdateConversationAsync(int id,
        [Body] LinguaPath.Shared.Contracts.Chat.UpdateConversationRequest request, CancellationToken ct = default);

    [Get("/api/v1/conversations/{id}/messages")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Chat.MessageDto>>> GetMessagesAsync(int id, [Query] long? before = null, [Query] int take = 40,
        CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/messages")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.MessageDto>> SendMessageAsync(int id, [Body] LinguaPath.Shared.Contracts.Chat.SendMessageRequest request,
        CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/seen")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.SeenDto>> MarkSeenAsync(int id, [Query] long upTo, CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/clear")]
    Task<ApiResponse> ClearAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/leave")]
    Task<ApiResponse> LeaveAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/join")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.ConversationDto>> JoinAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/members")]
    Task<ApiResponse> AddMembersAsync(int id, [Body] LinguaPath.Shared.Contracts.Chat.AddMembersRequest request, CancellationToken ct = default);

    [Delete("/api/v1/conversations/{id}/members/{userId}")]
    Task<ApiResponse> RemoveMemberAsync(int id, Guid userId, CancellationToken ct = default);

    [Post("/api/v1/conversations/{id}/members/{userId}/approve")]
    Task<ApiResponse> ApproveMemberAsync(int id, Guid userId, [Query] bool approve = true, CancellationToken ct = default);

    [Put("/api/v1/conversations/{id}/members/{userId}/role")]
    Task<ApiResponse> ChangeRoleAsync(int id, Guid userId, [Body] LinguaPath.Shared.Contracts.Chat.ChangeRoleRequest request, CancellationToken ct = default);

    [Get("/api/v1/groups/public")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Chat.PublicGroupDto>>> PublicGroupsAsync([Query] string? q = null, [Query] int? languageId = null,
        CancellationToken ct = default);

    [Delete("/api/v1/messages/{id}")]
    Task<ApiResponse> DeleteMessageAsync(long id, [Query] bool forEveryone = false, CancellationToken ct = default);

    [Put("/api/v1/messages/{id}/reaction")]
    Task<ApiResponse> ReactAsync(long id, [Body] LinguaPath.Shared.Contracts.Chat.ReactionRequest request, CancellationToken ct = default);

    [Delete("/api/v1/messages/{id}/reaction")]
    Task<ApiResponse> UnreactAsync(long id, CancellationToken ct = default);

    [Get("/api/v1/messages/{id}/seen-by")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Chat.SeenByDto>>> SeenByAsync(long id, CancellationToken ct = default);

    [Post("/api/v1/messages/{id}/translate")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Chat.TranslatedMessageDto>> TranslateMessageAsync(long id, [Query] string? to = null,
        CancellationToken ct = default);

    [Get("/api/v1/messages/search")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Chat.MessageSearchHitDto>>> SearchMessagesAsync([Query] string q, CancellationToken ct = default);
}

/// <summary>Phase 9: competitions, tournaments, leagues (REST side; live play also over <see cref="Services.ICompetitionConnection"/>).</summary>
public interface ICompetitionApi
{
    [Post("/api/v1/competitions/challenge")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> ChallengeAsync([Body] LinguaPath.Shared.Contracts.Competitions.ChallengeRequest request,
        CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/accept")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> AcceptAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/decline")]
    Task<ApiResponse> DeclineAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/quick-match")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> QuickMatchAsync([Body] LinguaPath.Shared.Contracts.Competitions.QuickMatchRequest request,
        CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/bot")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> PlayBotAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/cancel")]
    Task<ApiResponse> CancelAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/conversations/{conversationId}/competitions")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> GroupBattleAsync(int conversationId,
        [Body] LinguaPath.Shared.Contracts.Competitions.GroupBattleRequest request, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/join")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> JoinAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/start")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> StartNowAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/play")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> PlayAsync(int id, CancellationToken ct = default);

    [Get("/api/v1/competitions/{id}")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> GetAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/answers")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.AnswerAckDto>> AnswerAsync(int id,
        [Body] LinguaPath.Shared.Contracts.Competitions.SubmitAnswerRequest request, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/leave")]
    Task<ApiResponse> LeaveAsync(int id, CancellationToken ct = default);

    [Get("/api/v1/competitions/{id}/result")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionResultDto>> ResultAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/rematch")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionDto>> RematchAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/competitions/{id}/share")]
    Task<ApiResponse> ShareAsync(int id, [Body] LinguaPath.Shared.Contracts.Competitions.ShareRequest request, CancellationToken ct = default);

    [Get("/api/v1/competitions/active")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Competitions.CompetitionSummaryDto>>> ActiveAsync(CancellationToken ct = default);

    [Get("/api/v1/competitions/history")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Competitions.CompetitionSummaryDto>>> HistoryAsync([Query] int page = 1, CancellationToken ct = default);

    [Get("/api/v1/competitions/stats")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.CompetitionStatsDto>> StatsAsync(CancellationToken ct = default);

    [Get("/api/v1/tournaments")]
    Task<ApiResponse<List<LinguaPath.Shared.Contracts.Competitions.TournamentDto>>> TournamentsAsync([Query] int? languageId = null, CancellationToken ct = default);

    [Get("/api/v1/tournaments/{id}")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.TournamentDto>> TournamentAsync(int id, CancellationToken ct = default);

    [Post("/api/v1/tournaments/{id}/join")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.TournamentDto>> JoinTournamentAsync(int id, CancellationToken ct = default);

    [Delete("/api/v1/tournaments/{id}/join")]
    Task<ApiResponse> WithdrawAsync(int id, CancellationToken ct = default);

    [Get("/api/v1/leagues/current")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Competitions.LeagueDto>> LeagueAsync(CancellationToken ct = default);
}

/// <summary>Phase 10: in-app notification inbox (FR-L112).</summary>
public interface INotificationsApi
{
    [Get("/api/v1/notifications")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Notifications.NotificationPageDto>> ListAsync([Query] long? before, [Query] int take = 30,
        CancellationToken ct = default);

    [Get("/api/v1/notifications/unread-count")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Notifications.UnreadCountDto>> UnreadCountAsync(CancellationToken ct = default);

    [Put("/api/v1/notifications/{id}/read")]
    Task<ApiResponse> MarkReadAsync(long id, CancellationToken ct = default);

    [Put("/api/v1/notifications/read-all")]
    Task<ApiResponse<LinguaPath.Shared.Contracts.Notifications.UnreadCountDto>> MarkAllReadAsync(CancellationToken ct = default);

    [Delete("/api/v1/notifications/{id}")]
    Task<ApiResponse> DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>Phase 10: crash reports. Anonymous client, so a crash before sign-in is still reported.</summary>
public interface IClientErrorsApi
{
    [Post("/api/v1/client-errors")]
    Task ReportAsync([Body] LinguaPath.Shared.Contracts.Notifications.ClientErrorReport report, CancellationToken ct = default);
}
