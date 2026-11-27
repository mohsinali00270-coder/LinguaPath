namespace LinguaPath.Shared;

/// <summary>Question types (FR-L51, FR-A61).</summary>
public enum QuestionType
{
    /// <summary>Pick the correct option(s).</summary>
    MultipleChoice = 1,
    TrueFalse = 2,
    /// <summary>Stem contains "___"; options are the accepted answers.</summary>
    FillBlank = 3,
    /// <summary>Options are the left items; MatchKey is the right item each one belongs to.</summary>
    MatchPairs = 4,
    /// <summary>Options are the accepted sentences; the first one is cut into word tiles.</summary>
    ReorderWords = 5,
    /// <summary>Prompt is the sentence to translate; options are accepted translations.</summary>
    Translate = 6,
    /// <summary>Learner hears the prompt (audio or device voice) and picks an option.</summary>
    ListenChoose = 7,
    /// <summary>Learner records themselves saying the prompt; graded by a reviewer (AI scoring in Phase 5).</summary>
    Speak = 8,
    /// <summary>Free writing task; graded by a reviewer (AI scoring in Phase 5).</summary>
    Writing = 9
}

public enum Skill
{
    Grammar = 1,
    Vocabulary = 2,
    Reading = 3,
    Listening = 4,
    Writing = 5,
    Speaking = 6
}

public enum QuizType
{
    Daily = 1,
    Monthly = 2,
    Placement = 3
}

/// <summary>How a monthly exam section picks its questions (FR-A64).</summary>
public enum SelectionMode
{
    Random = 0,
    Fixed = 1
}

public enum AttemptStatus
{
    InProgress = 0,
    /// <summary>Submitted; some writing or speaking answers wait for a reviewer (FR-A81).</summary>
    PendingReview = 1,
    Graded = 2,
    /// <summary>A daily quiz that was never finished before midnight.</summary>
    Expired = 3
}

public enum DailyQuizState
{
    /// <summary>No completed lectures yet, so there is nothing to ask.</summary>
    NotReady = 0,
    Ready = 1,
    InProgress = 2,
    Done = 3,
    /// <summary>Lectures are done but they have no published questions yet.</summary>
    NoQuestions = 4
}

public enum ExamAvailability
{
    Locked = 0,
    Available = 1,
    InProgress = 2,
    Passed = 3,
    /// <summary>Failed; a retake opens after the waiting period.</summary>
    RetakeLater = 4,
    /// <summary>Failed and no retakes left.</summary>
    NoRetakesLeft = 5,
    /// <summary>Failed and a retake is available now.</summary>
    RetakeAvailable = 6,
    /// <summary>Waiting for a reviewer to grade writing/speaking answers.</summary>
    PendingReview = 7
}
