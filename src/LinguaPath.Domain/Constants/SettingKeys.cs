namespace LinguaPath.Domain.Constants;

/// <summary>
/// Keys for the SystemSettings table. Defaults are the business rules in SRS sections 6 and 11.6;
/// all of them are editable by a Super Admin (FR-A111).
/// </summary>
public static class SettingKeys
{
    // App version control (FR-A112)
    public const string MinAppVersionAndroid = "App.MinVersion.Android";
    public const string MinAppVersionIos = "App.MinVersion.iOS";
    public const string LatestAppVersionAndroid = "App.LatestVersion.Android";
    public const string LatestAppVersionIos = "App.LatestVersion.iOS";
    public const string ForceUpdateMessage = "App.ForceUpdateMessage";
    public const string StoreUrlAndroid = "App.StoreUrl.Android";
    public const string StoreUrlIos = "App.StoreUrl.iOS";

    // Assessment (BR-01 .. BR-08)
    public const string DailyQuizPassMark = "Assessment.DailyQuizPassMark";
    public const string MonthlyExamPassMark = "Assessment.MonthlyExamPassMark";
    public const string MonthlyExamUnlockPercent = "Assessment.MonthlyExamUnlockPercent";
    public const string ExamMaxRetakes = "Assessment.ExamMaxRetakes";
    public const string ExamRetakeGapDays = "Assessment.ExamRetakeGapDays";
    public const string CourseGradeQuizWeight = "Assessment.CourseGradeQuizWeight";
    public const string CourseGradeExamWeight = "Assessment.CourseGradeExamWeight";
    public const string DailyQuizFromTodayCount = "Assessment.DailyQuiz.FromRecentCount";
    public const string DailyQuizFromWeekCount = "Assessment.DailyQuiz.FromWeekCount";
    public const string DailyQuizReviewCount = "Assessment.DailyQuiz.ReviewCount";
    public const string PlacementQuestionsPerLevel = "Assessment.Placement.QuestionsPerLevel";
    public const string PlacementLevelPassPercent = "Assessment.Placement.LevelPassPercent";
    public const string CertificateMinGrade = "Certificate.MinGrade";
    public const string GradeBandDistinction = "Certificate.Band.Distinction";
    public const string GradeBandMerit = "Certificate.Band.Merit";
    public const string CertificateNumberPrefix = "Certificate.NumberPrefix";

    // Learning (BR-09 .. BR-12)
    public const string MaxPauseDays = "Learning.MaxPauseDays";
    public const string LateQuizCountsTowardMarks = "Learning.LateQuizCountsTowardMarks";
    public const string LecturesUnlockSequentially = "Learning.LecturesUnlockSequentially";
    public const string MinReadingTimePercent = "Learning.MinReadingTimePercent";

    // Translation / AI (BR-15, FR-A110)
    public const string DailyTranslationLimit = "Translation.DailyLimitPerUser";
    public const string TranslationMaxCharacters = "Translation.MaxCharacters";
    public const string TranslationHistorySize = "Translation.HistorySize";

    // Practice (FR-L90 .. FR-L94, SRS 5.4 per-learner limits on paid services)
    public const string PracticeAiCallsPerDay = "Practice.AiCallsPerUserPerDay";
    public const string PracticeSpeechPerDay = "Practice.SpeechChecksPerUserPerDay";
    public const string PracticeConversationTurns = "Practice.ConversationMaxTurns";
    public const string PracticeAutoGradeSpeaking = "Practice.AutoGradeExamSpeaking";
    public const string AiProvider = "Ai.Provider";
    public const string AiModel = "Ai.Model";
    public const string AiMonthlyBudgetUsd = "Ai.MonthlyBudgetUsd";
    public const string AiBudgetAlertPercent = "Ai.BudgetAlertPercent";
    public const string AiContentRequiresReview = "Ai.ContentRequiresReview";
    public const string AiAutoGradeWriting = "Ai.AutoGradeWriting";
    public const string AiGradingConfidence = "Ai.GradingConfidencePercent";
    public const string AiNativeCheckLanguages = "Ai.NativeCheckLanguages";
    public const string AiQuestionsPerLecture = "Ai.QuestionsPerLecture";
    public const string AiInputPricePerMillion = "Ai.InputPricePerMillionUsd";
    public const string AiOutputPricePerMillion = "Ai.OutputPricePerMillionUsd";

    // Points (BR-20 .. BR-22)
    public const string PointsDailyCap = "Points.DailyCap";
    public const string PointsStudyAheadPerDay = "Points.StudyAheadBonusPerDay";
    public const string PointsStudyAheadMax = "Points.StudyAheadBonusMax";

    // Chat & moderation (BR-23 .. BR-32)
    public const string ChatDeleteForEveryoneHours = "Chat.DeleteForEveryoneHours";
    public const string ChatMaxMessageLength = "Chat.MaxMessageLength";
    public const string ChatMessagesPerMinute = "Chat.MessagesPerMinute";
    public const string ChatGroupMaxMembers = "Chat.GroupMaxMembers";
    public const string ChatMaxGroupsPerUser = "Chat.MaxGroupsCreatedPerUser";
    public const string ModerationStrikeWindowDays = "Moderation.StrikeWindowDays";
    public const string ModerationLinkAttemptsPerStrike = "Moderation.LinkAttemptsPerStrike";
    public const string ModerationSuspensionHours = "Moderation.SuspensionHours";
    public const string ModerationAiEnabled = "Moderation.AiEnabled";
    public const string ModerationAiAbusivePercent = "Moderation.AiAbusiveThresholdPercent";
    public const string ModerationAiSeverePercent = "Moderation.AiSevereThresholdPercent";
    public const string ModerationSpamRecipients = "Moderation.SpamRecipients";
    public const string ModerationSpamWindowMinutes = "Moderation.SpamWindowMinutes";

    // Competitions (BR-29)
    public const string CompetitionMaxRankedPerDay = "Competition.MaxRankedPerDay";
    public const string CompetitionMaxWinsVsSameOpponent = "Competition.MaxWinsVsSameOpponentPerDay";
}
