namespace LinguaPath.Shared;

/// <summary>What an AI call was for (usage log, prompt template key, budget reports).</summary>
public enum AiFeature
{
    Roadmap = 1,
    GrammarLecture = 2,
    LectureAssist = 3,
    QuizFromLecture = 4,
    Posts = 5,
    WritingGrading = 6,
    /// <summary>Practice library drafts: passages, prompts, sentences (FR-L90 .. FR-L93).</summary>
    PracticeItems = 7,
    /// <summary>Free-talk replies to learners (FR-L92).</summary>
    Conversation = 8,
    /// <summary>Writing practice check for learners (FR-L91).</summary>
    WritingCheck = 9,
    /// <summary>Chat message scan for abuse, threats, hate and harassment (FR-M02, FR-M03).</summary>
    Moderation = 10
}

/// <summary>A queued AI job (FR-A21 .. FR-A24, FR-A31, FR-A42, FR-A44, FR-A52, FR-A62, FR-L63).</summary>
public enum AiRequestType
{
    /// <summary>A whole language: roadmap, then (after roadmap approval) lectures and questions. Needs Super Admin approval.</summary>
    NewLanguage = 1,
    /// <summary>A roadmap (modules and lecture titles) for a new course in an existing language.</summary>
    Roadmap = 2,
    /// <summary>Lesson text, examples and vocabulary for one lecture.</summary>
    LectureContent = 3,
    /// <summary>Lesson text for every lecture of a course that has none yet.</summary>
    AutoFillLectures = 4,
    /// <summary>Questions grounded in one lecture.</summary>
    Questions = 5,
    /// <summary>A month of tips or words of the day.</summary>
    Posts = 6,
    /// <summary>Pre-grading of writing answers in a submitted exam.</summary>
    WritingGrading = 7,
    /// <summary>Drafts for the practice library.</summary>
    PracticeItems = 8
}

public enum AiRequestStatus
{
    /// <summary>Waiting for a Super Admin (new languages only).</summary>
    PendingApproval = 0,
    Queued = 1,
    Running = 2,
    /// <summary>Roadmap drafted; an admin reviews it in the builder, then starts content generation.</summary>
    AwaitingRoadmapReview = 3,
    Completed = 4,
    Failed = 5,
    Rejected = 6,
    /// <summary>Stopped because the monthly AI budget is used up; can be resumed.</summary>
    Paused = 7,
    Cancelled = 8
}

public enum AiCallStatus
{
    Success = 0,
    /// <summary>The model answered but the JSON didn't match the schema (after one retry).</summary>
    InvalidOutput = 1,
    Failed = 2,
    BudgetExceeded = 3
}

/// <summary>Lecture assist actions (FR-A43).</summary>
public enum AssistAction
{
    Expand = 1,
    Simplify = 2,
    AddExamples = 3
}
