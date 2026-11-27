using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;

namespace LinguaPath.Infrastructure.Persistence.Seed;

internal static class SeedData
{
    /// <summary>
    /// Starter languages. None has courses yet, so none is Published; Phase 2 adds courses and the admin publishes them.
    /// TTS voices and STT locales are Azure AI Speech names.
    /// </summary>
    public static IEnumerable<Language> Languages() =>
    [
        Lang("English", "English", "en", TextDirection.Ltr, "en-US-JennyNeural", "en-US", 1, LanguageStatus.ComingSoon),
        Lang("Arabic", "العربية", "ar", TextDirection.Rtl, "ar-SA-ZariyahNeural", "ar-SA", 2, LanguageStatus.ComingSoon),
        Lang("Spanish", "Español", "es", TextDirection.Ltr, "es-ES-ElviraNeural", "es-ES", 3, LanguageStatus.ComingSoon),
        Lang("French", "Français", "fr", TextDirection.Ltr, "fr-FR-DeniseNeural", "fr-FR", 4, LanguageStatus.ComingSoon),
        Lang("German", "Deutsch", "de", TextDirection.Ltr, "de-DE-KatjaNeural", "de-DE", 5, LanguageStatus.ComingSoon),
        Lang("Turkish", "Türkçe", "tr", TextDirection.Ltr, "tr-TR-EmelNeural", "tr-TR", 6, LanguageStatus.ComingSoon),
        Lang("Chinese (Mandarin)", "中文", "zh", TextDirection.Ltr, "zh-CN-XiaoxiaoNeural", "zh-CN", 7, LanguageStatus.ComingSoon),
        Lang("Japanese", "日本語", "ja", TextDirection.Ltr, "ja-JP-NanamiNeural", "ja-JP", 8, LanguageStatus.ComingSoon),
        Lang("Korean", "한국어", "ko", TextDirection.Ltr, "ko-KR-SunHiNeural", "ko-KR", 9, LanguageStatus.ComingSoon),
        Lang("Urdu", "اردو", "ur", TextDirection.Rtl, "ur-PK-UzmaNeural", "ur-PK", 10, LanguageStatus.Draft),
        Lang("Hindi", "हिन्दी", "hi", TextDirection.Ltr, "hi-IN-SwaraNeural", "hi-IN", 11, LanguageStatus.Draft),
        Lang("Persian", "فارسی", "fa", TextDirection.Rtl, "fa-IR-DilaraNeural", "fa-IR", 12, LanguageStatus.Draft),
    ];

    /// <summary>Defaults from the SRS business rules (sections 6 and 11.6).</summary>
    public static IEnumerable<SystemSetting> Settings() =>
    [
        S(SettingKeys.MinAppVersionAndroid, "1.0.0", "App", SettingDataType.Version, "Oldest Android app version allowed to sign in (FR-A112)."),
        S(SettingKeys.MinAppVersionIos, "1.0.0", "App", SettingDataType.Version, "Oldest iOS app version allowed to sign in (FR-A112)."),
        S(SettingKeys.LatestAppVersionAndroid, "1.0.0", "App", SettingDataType.Version, "Latest Android version in the store."),
        S(SettingKeys.LatestAppVersionIos, "1.0.0", "App", SettingDataType.Version, "Latest iOS version in the store."),
        S(SettingKeys.ForceUpdateMessage, "A new version of LinguaPath is available. Please update to continue.", "App", SettingDataType.String, "Shown when the app is older than the minimum version."),
        S(SettingKeys.StoreUrlAndroid, "", "App", SettingDataType.String, "Google Play link."),
        S(SettingKeys.StoreUrlIos, "", "App", SettingDataType.String, "App Store link."),

        S(SettingKeys.DailyQuizPassMark, "50", "Assessment", SettingDataType.Int, "BR-01 Daily quiz pass mark (%), feedback only."),
        S(SettingKeys.MonthlyExamPassMark, "60", "Assessment", SettingDataType.Int, "BR-02 Monthly exam pass mark (%)."),
        S(SettingKeys.MonthlyExamUnlockPercent, "80", "Assessment", SettingDataType.Int, "BR-03 % of the month's lectures needed to unlock the exam."),
        S(SettingKeys.ExamMaxRetakes, "2", "Assessment", SettingDataType.Int, "BR-04 Maximum retakes per monthly exam."),
        S(SettingKeys.ExamRetakeGapDays, "7", "Assessment", SettingDataType.Int, "BR-04 Days between retakes."),
        S(SettingKeys.CourseGradeQuizWeight, "30", "Assessment", SettingDataType.Int, "BR-05 Weight of daily quizzes in the course grade (%)."),
        S(SettingKeys.CourseGradeExamWeight, "70", "Assessment", SettingDataType.Int, "BR-05 Weight of monthly exams in the course grade (%)."),
        S(SettingKeys.DailyQuizFromTodayCount, "3", "Assessment", SettingDataType.Int, "FR-A63 Daily quiz: questions from lectures completed in the last 2 days."),
        S(SettingKeys.DailyQuizFromWeekCount, "3", "Assessment", SettingDataType.Int, "FR-A63 Daily quiz: questions from lectures completed in the last 7 days."),
        S(SettingKeys.DailyQuizReviewCount, "2", "Assessment", SettingDataType.Int, "FR-A63 Daily quiz: questions from the learner's review list (wrong answers)."),
        S(SettingKeys.PlacementQuestionsPerLevel, "2", "Assessment", SettingDataType.Int, "FR-L11 Placement test: questions per CEFR level of the course (10 in total for 5 levels)."),
        S(SettingKeys.PlacementLevelPassPercent, "60", "Assessment", SettingDataType.Int, "FR-L11 Placement test: % correct needed at a level to place above it."),
        S(SettingKeys.CertificateMinGrade, "60", "Certificate", SettingDataType.Int, "BR-06 Minimum course grade for a certificate (%)."),
        S(SettingKeys.GradeBandDistinction, "90", "Certificate", SettingDataType.Int, "BR-07 Distinction from (%)."),
        S(SettingKeys.GradeBandMerit, "75", "Certificate", SettingDataType.Int, "BR-07 Merit from (%)."),
        S(SettingKeys.CertificateNumberPrefix, "LP", "Certificate", SettingDataType.String, "Certificate number prefix, e.g. LP-ES-2026-000123."),

        S(SettingKeys.MaxPauseDays, "30", "Learning", SettingDataType.Int, "BR-11 Maximum pause per course (days)."),
        S(SettingKeys.LateQuizCountsTowardMarks, "false", "Learning", SettingDataType.Bool, "FR-L53 A daily quiz done after midnight counts toward marks."),
        S(SettingKeys.LecturesUnlockSequentially, "true", "Learning", SettingDataType.Bool, "FR-L31 Lectures unlock in order."),
        S(SettingKeys.MinReadingTimePercent, "50", "Learning", SettingDataType.Int, "FR-S05 Minimum % of estimated minutes before a lecture counts as complete."),

        S(SettingKeys.DailyTranslationLimit, "100", "Translation", SettingDataType.Int, "BR-15 Translation requests per learner per day."),
        S(SettingKeys.TranslationMaxCharacters, "500", "Translation", SettingDataType.Int, "FR-L100 Longest text one translation may have."),
        S(SettingKeys.TranslationHistorySize, "100", "Translation", SettingDataType.Int, "FR-L103 Translations kept in a learner's history."),
        S(SettingKeys.PracticeAiCallsPerDay, "30", "Practice", SettingDataType.Int, "SRS 5.4 AI writing checks + conversation messages per learner per day."),
        S(SettingKeys.PracticeSpeechPerDay, "50", "Practice", SettingDataType.Int, "SRS 5.4 Pronunciation checks per learner per day."),
        S(SettingKeys.PracticeConversationTurns, "12", "Practice", SettingDataType.Int, "FR-L92 Earlier messages sent to the AI with each conversation reply."),
        S(SettingKeys.PracticeAutoGradeSpeaking, "true", "Practice", SettingDataType.Bool, "Let pronunciation scoring mark exam speaking answers when it is confident; others go to the grading queue."),
        S(SettingKeys.AiProvider, "DeepSeek", "AI", SettingDataType.String, "FR-A110 AI provider."),
        S(SettingKeys.AiModel, "deepseek-chat", "AI", SettingDataType.String, "FR-A110 AI model."),
        S(SettingKeys.AiMonthlyBudgetUsd, "100", "AI", SettingDataType.Decimal, "Monthly AI budget in USD (5.4)."),
        S(SettingKeys.AiBudgetAlertPercent, "80", "AI", SettingDataType.Int, "Alert when this % of the AI budget is used."),
        S(SettingKeys.AiContentRequiresReview, "true", "AI", SettingDataType.Bool, "BR-13 AI content needs Reviewer approval."),
        S(SettingKeys.AiAutoGradeWriting, "true", "AI", SettingDataType.Bool, "FR-L63 Let AI grade exam writing when it is confident; others go to the grading queue."),
        S(SettingKeys.AiGradingConfidence, "80", "AI", SettingDataType.Int, "FR-L63 AI confidence (%) needed to grade writing without a reviewer."),
        S(SettingKeys.AiNativeCheckLanguages, "am,ha,ig,km,lo,my,ne,ps,sd,si,so,yo", "AI", SettingDataType.String, "SRS 5.3 Low-resource languages (ISO codes): AI drafts are flagged \"Native speaker check required\"."),
        S(SettingKeys.AiQuestionsPerLecture, "5", "AI", SettingDataType.Int, "FR-A23 Questions generated per lecture when a whole language or course is generated."),
        S(SettingKeys.AiInputPricePerMillion, "0.27", "AI", SettingDataType.Decimal, "5.4 Price per million input tokens (USD) for cost tracking."),
        S(SettingKeys.AiOutputPricePerMillion, "1.10", "AI", SettingDataType.Decimal, "5.4 Price per million output tokens (USD) for cost tracking."),

        S(SettingKeys.PointsDailyCap, "500", "Points", SettingDataType.Int, "BR-22 Daily points cap (exams, certificates, prizes excluded)."),
        S(SettingKeys.PointsStudyAheadPerDay, "5", "Points", SettingDataType.Int, "BR-21 Bonus points per day a lecture is done early."),
        S(SettingKeys.PointsStudyAheadMax, "15", "Points", SettingDataType.Int, "BR-21 Maximum study-ahead bonus per lecture."),

        S(SettingKeys.ChatDeleteForEveryoneHours, "24", "Chat", SettingDataType.Int, "BR-23 Hours a sender can delete a message for everyone."),
        S(SettingKeys.ChatMaxMessageLength, "1000", "Chat", SettingDataType.Int, "BR-31 Maximum message length."),
        S(SettingKeys.ChatMessagesPerMinute, "30", "Chat", SettingDataType.Int, "BR-31 Messages per minute per user."),
        S(SettingKeys.ChatGroupMaxMembers, "256", "Chat", SettingDataType.Int, "BR-30 Maximum group members."),
        S(SettingKeys.ChatMaxGroupsPerUser, "20", "Chat", SettingDataType.Int, "BR-30 Groups a learner can create."),
        S(SettingKeys.ModerationStrikeWindowDays, "90", "Moderation", SettingDataType.Int, "BR-26 Rolling strike window (days)."),
        S(SettingKeys.ModerationLinkAttemptsPerStrike, "5", "Moderation", SettingDataType.Int, "BR-25 Rejected link attempts per day that count as one strike."),
        S(SettingKeys.ModerationSuspensionHours, "24", "Moderation", SettingDataType.Int, "BR-26 Chat suspension after the 2nd strike (hours)."),
        S(SettingKeys.ModerationAiEnabled, "true", "Moderation", SettingDataType.Bool, "FR-M02 Scan delivered messages with the AI moderation model."),
        S(SettingKeys.ModerationAiAbusivePercent, "70", "Moderation", SettingDataType.Int, "FR-M02 AI score (0-100) from which a message is abusive."),
        S(SettingKeys.ModerationAiSeverePercent, "90", "Moderation", SettingDataType.Int, "FR-M02 AI score for threats, hate or sexual harassment from which it is severe (block)."),
        S(SettingKeys.ModerationSpamRecipients, "5", "Moderation", SettingDataType.Int, "FR-M04 Same message to this many chats counts as spam."),
        S(SettingKeys.ModerationSpamWindowMinutes, "10", "Moderation", SettingDataType.Int, "FR-M04 Window for the spam rule (minutes)."),

        S(SettingKeys.CompetitionMaxRankedPerDay, "20", "Competition", SettingDataType.Int, "BR-29 Ranked competitions per day."),
        S(SettingKeys.CompetitionMaxWinsVsSameOpponent, "3", "Competition", SettingDataType.Int, "BR-29 Point-earning wins vs the same opponent per day."),
    ];

    /// <summary>Sample roadmap used when Seed:SampleContent is true (development).</summary>
    public static Course SampleSpanishCourse(int languageId)
    {
        var now = DateTimeOffset.UtcNow;
        var modules = new (string Title, string Objective, CefrLevel Level, int Month, (string Title, string Topic, int Minutes, bool Core)[] Lectures)[]
        {
            ("First steps", "Greet people, introduce yourself and count to 20.", CefrLevel.A1, 1,
            [
                ("The Spanish alphabet and sounds", "Pronunciation", 15, true),
                ("Greetings and goodbyes", "Vocabulary", 15, true),
                ("Ser: I am, you are", "Grammar", 20, true),
                ("Numbers 0-20", "Vocabulary", 15, true),
                ("Accents and stress marks", "Pronunciation", 10, false)
            ]),
            ("People and places", "Describe yourself, family and where you live.", CefrLevel.A1, 1,
            [
                ("Nouns and gender: el / la", "Grammar", 20, true),
                ("Family members", "Vocabulary", 15, true),
                ("Adjectives that agree", "Grammar", 20, true),
                ("Tener: having and age", "Grammar", 20, true),
                ("Countries and nationalities", "Vocabulary", 15, false)
            ]),
            ("Everyday life", "Talk about routines, time and food.", CefrLevel.A2, 2,
            [
                ("Present tense of regular -ar verbs", "Grammar", 25, true),
                ("Telling the time", "Vocabulary", 15, true),
                ("Ordering food in a café", "Conversation", 20, true),
                ("Reflexive verbs: daily routine", "Grammar", 25, true),
                ("Spanish meal times and culture", "Culture", 10, false)
            ]),
            ("Getting around", "Ask for directions, travel and make plans.", CefrLevel.A2, 2,
            [
                ("Ir a + infinitive: making plans", "Grammar", 20, true),
                ("Directions and places in town", "Vocabulary", 15, true),
                ("Buying a train ticket", "Conversation", 20, true),
                ("Estar: location and feelings", "Grammar", 25, true),
                ("Weather and seasons", "Vocabulary", 15, false)
            ])
        };

        var course = new Course
        {
            LanguageId = languageId,
            Title = "Spanish Essentials (A1 → A2)",
            Description = "Everyday Spanish for beginners: greetings, people, routines and travel.",
            StartLevel = CefrLevel.A1,
            EndLevel = CefrLevel.A2,
            Status = ContentStatus.Published,
            PublishedAt = now,
            CreatedAt = now
        };
        for (var m = 0; m < modules.Length; m++)
        {
            var mod = modules[m];
            var module = new CourseModule
            {
                Title = mod.Title, Objective = mod.Objective, Level = mod.Level, MonthNo = mod.Month, SortOrder = m + 1, CreatedAt = now
            };
            for (var l = 0; l < mod.Lectures.Length; l++)
            {
                var lec = mod.Lectures[l];
                module.Lectures.Add(new Lecture
                {
                    Title = lec.Title, Topic = lec.Topic, EstimatedMinutes = lec.Minutes, IsCore = lec.Core, Level = mod.Level,
                    SortOrder = l + 1, Status = ContentStatus.Published, CreatedAt = now
                });
            }
            course.Modules.Add(module);
        }
        return course;
    }

    private static Language Lang(string name, string native, string iso, TextDirection dir, string voice, string stt, int order, LanguageStatus status) => new()
    {
        Name = name,
        NativeName = native,
        IsoCode = iso,
        Direction = dir,
        TtsVoice = voice,
        SttLocale = stt,
        SortOrder = order,
        Status = status,
        MinLevel = CefrLevel.A1,
        MaxLevel = CefrLevel.C1
    };

    private static SystemSetting S(string key, string value, string group, SettingDataType type, string description) => new()
    {
        Key = key,
        Value = value,
        Group = group,
        DataType = type,
        Description = description
    };

    /// <summary>FR-S03 defaults (Phase 7).</summary>
    /// <summary>English starter list for every language (LanguageId null). Mild = masked, Abusive = refused + strike, Severe = block.</summary>
    public static IEnumerable<(string Term, ModerationSeverity Severity)> AbusiveWords() =>
    [
        ("damn", ModerationSeverity.Mild),
        ("crap", ModerationSeverity.Mild),
        ("shut up", ModerationSeverity.Mild),
        ("idiot", ModerationSeverity.Abusive),
        ("stupid", ModerationSeverity.Abusive),
        ("moron", ModerationSeverity.Abusive),
        ("loser", ModerationSeverity.Abusive),
        ("dumbass", ModerationSeverity.Abusive),
        ("bastard", ModerationSeverity.Abusive),
        ("bitch", ModerationSeverity.Abusive),
        ("asshole", ModerationSeverity.Abusive),
        ("fuck you", ModerationSeverity.Abusive),
        ("kill yourself", ModerationSeverity.Severe),
        ("kys", ModerationSeverity.Severe),
        ("i will kill you", ModerationSeverity.Severe)
    ];

    public static IEnumerable<PointRule> PointRules()
    {
        PointRule R(PointAction action, int points, int dailyCap, string description) =>
            new() { Action = action, Points = points, DailyCap = dailyCap, Description = description, IsActive = true };
        return
        [
            R(PointAction.LectureCompleted, 10, 0, "Complete a lecture (once per lecture)."),
            R(PointAction.StudyAheadBonus, 0, 0, "Study-ahead bonus; the amount comes from the Points.StudyAheadBonus settings (BR-21)."),
            R(PointAction.DailyQuiz, 2, 0, "Per correct answer in a daily quiz."),
            R(PointAction.DailyQuizPerfect, 10, 0, "Extra for 100% in a daily quiz."),
            R(PointAction.MonthlyExamPassed, 100, 0, "Passing a monthly exam (once per exam; not capped)."),
            R(PointAction.ExamGradeBonus, 50, 0, "Exam grade bonus: this for Merit, twice this for Distinction."),
            R(PointAction.PracticeSession, 5, 4, "A practice exercise; daily limit is the number of times."),
            R(PointAction.StreakDay, 5, 0, "Each day of a streak."),
            R(PointAction.StreakWeek, 50, 0, "Reaching a 7-day streak."),
            R(PointAction.StreakMonth, 200, 0, "Reaching a 30-day streak."),
            R(PointAction.CertificateEarned, 500, 0, "Earning a certificate (not capped)."),
            R(PointAction.Referral, 50, 0, "A referred friend completes onboarding (referrals come later)."),
            R(PointAction.CompetitionWin, 30, 0, "Quiz competition win (max 3 point-earning wins vs the same opponent a day, BR-29)."),
            R(PointAction.CompetitionDraw, 15, 0, "Quiz competition draw."),
            R(PointAction.CompetitionLoss, 5, 0, "Quiz competition played and lost."),
            R(PointAction.TournamentPrize, 0, 0, "Tournament prizes are set per tournament (not capped)."),
            R(PointAction.GroupBattleBonus, 30, 0, "Group battle winner; 2nd gets two thirds, 3rd one third (FR-Q10)."),
            R(PointAction.AdminAdjustment, 0, 0, "Admin adjustments with a reason (FR-S10).")
        ];
    }
}
