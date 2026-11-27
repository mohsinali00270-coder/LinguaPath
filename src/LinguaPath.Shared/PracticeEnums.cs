namespace LinguaPath.Shared;

/// <summary>Practice exercises (FR-L90 .. FR-L93).</summary>
public enum PracticeType
{
    Read = 1,
    Write = 2,
    Speak = 3,
    Listen = 4,
    /// <summary>Free-talk with the AI conversation partner (FR-L92).</summary>
    Conversation = 5
}

/// <summary>How a word in a dictation compares with the sentence (FR-L93).</summary>
public enum DiffKind
{
    Correct = 0,
    /// <summary>Typed, but spelled differently.</summary>
    Wrong = 1,
    /// <summary>In the sentence, not typed.</summary>
    Missing = 2,
    /// <summary>Typed, not in the sentence.</summary>
    Extra = 3
}

/// <summary>Flashcard answer (FR-L94, SM-2 quality).</summary>
public enum RecallGrade
{
    Again = 1,
    Hard = 3,
    Good = 4,
    Easy = 5
}
