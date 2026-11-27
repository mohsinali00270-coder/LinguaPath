namespace LinguaPath.Shared;

/// <summary>Kinds of inbox notification (FR-L110, FR-L112). Each maps to one of the learner's notification switches.</summary>
public enum NotificationType
{
    StudyReminder = 1,
    DailyQuizReady = 2,
    ExamSoon = 3,
    StreakAtRisk = 4,
    Certificate = 5,
    NewPost = 6,
    /// <summary>Account and service messages from the team; always delivered.</summary>
    System = 7
}
