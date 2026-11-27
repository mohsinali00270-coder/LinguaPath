using LinguaPath.Domain.Common;
using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>A reviewed practice exercise: reading passage, writing task, speaking or dictation sentence (FR-L90 .. FR-L93).</summary>
public class PracticeItem : AuditableEntity<int>
{
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public PracticeType Type { get; set; }
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    public string Title { get; set; } = string.Empty;
    public string? Topic { get; set; }
    /// <summary>Passage, writing task, or the sentence to say / write down.</summary>
    public string Text { get; set; } = string.Empty;
    public string? Translation { get; set; }
    /// <summary>Reading comprehension questions as JSON: [{question, options[], correct, explanation}].</summary>
    public string? QuestionsJson { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public ContentSource Source { get; set; } = ContentSource.Manual;
}

/// <summary>One go at a practice exercise, with its feedback (SRS 7.3).</summary>
public class PracticeAttempt
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int LanguageId { get; set; }
    public PracticeType Type { get; set; }
    public int? PracticeItemId { get; set; }
    public PracticeItem? PracticeItem { get; set; }
    public string? PromptText { get; set; }
    public string? LearnerInput { get; set; }
    public string? AudioUrl { get; set; }
    /// <summary>Corrections, word scores or diff as JSON.</summary>
    public string? FeedbackJson { get; set; }
    public int? Score { get; set; }
    /// <summary>Produced by an offline sample service, not a real provider.</summary>
    public bool IsSample { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A word in the learner's own list, scheduled with SM-2 (FR-L94).</summary>
public class UserWord
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public string Word { get; set; } = string.Empty;
    public string Meaning { get; set; } = string.Empty;
    public string? Example { get; set; }
    public double EaseFactor { get; set; } = 2.5;
    public int IntervalDays { get; set; }
    public int Repetitions { get; set; }
    public int Lapses { get; set; }
    public DateTimeOffset NextReviewAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
}

/// <summary>Translation history (FR-L103: last 100 per learner).</summary>
public class TranslationEntry
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public bool Detected { get; set; }
    public bool Cached { get; set; }
    public bool IsSample { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Shared cache of translations, so repeated phrases don't cost an API call (FR-L104, SRS 5.4).</summary>
public class TranslationCacheEntry
{
    public int Id { get; set; }
    /// <summary>SHA-256 of from|to|normalised text.</summary>
    public string Hash { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int Hits { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
