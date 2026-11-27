namespace LinguaPath.Shared;

/// <summary>Content lifecycle used by courses, lectures, posts and questions (SRS 4: Draft → In review → Approved → Published → Archived).</summary>
public enum ContentStatus
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    Published = 3,
    Archived = 4
}

public enum ContentSource
{
    Manual = 0,
    Ai = 1
}

/// <summary>Why the learner is studying (FR-L12).</summary>
public enum LearningGoal
{
    Travel = 1,
    JobBusiness = 2,
    ExamPrep = 3,
    StudyAbroad = 4,
    Conversation = 5,
    Hobby = 6
}

/// <summary>Skills the learner wants to focus on (FR-L14). Flags so several can be chosen.</summary>
[Flags]
public enum FocusSkill
{
    None = 0,
    Reading = 1,
    Writing = 2,
    Listening = 4,
    Speaking = 8,
    Grammar = 16,
    Vocabulary = 32,
    All = Reading | Writing | Listening | Speaking | Grammar | Vocabulary
}

/// <summary>Days of the week the learner studies (FR-L14). Bit n = DayOfWeek n.</summary>
[Flags]
public enum StudyDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Everyday = Weekdays | Saturday | Sunday
}

public static class StudyDaysExtensions
{
    public static bool Includes(this StudyDays days, DayOfWeek day) => ((int)days & (1 << (int)day)) != 0;

    public static int CountPerWeek(this StudyDays days) =>
        Enum.GetValues<DayOfWeek>().Count(d => days.Includes(d));
}

public enum LearnerCourseStatus
{
    Active = 0,
    Paused = 1,
    Completed = 2,
    Cancelled = 3
}

/// <summary>State of a lecture in the learner's roadmap (FR-L30).</summary>
public enum RoadmapItemState
{
    Locked = 0,
    Available = 1,
    InProgress = 2,
    Completed = 3
}

/// <summary>Result of the duration / daily-time check (FR-L15).</summary>
public enum FeasibilityStatus
{
    /// <summary>Every lecture fits.</summary>
    Ok = 0,
    /// <summary>All core lectures fit; some optional lectures were left out (FR-A32).</summary>
    OptionalDropped = 1,
    /// <summary>Even the core lectures do not fit: show the suggested end date or daily time.</summary>
    NotFeasible = 2
}

public enum LanguageRequestStatus
{
    Pending = 0,
    Planned = 1,
    Added = 2,
    Rejected = 3
}
