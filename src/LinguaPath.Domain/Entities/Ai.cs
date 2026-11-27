using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>Editable, versioned prompt (SRS 5.3). One active version per key.</summary>
public class AiPromptTemplate
{
    public int Id { get; set; }
    /// <summary>Feature key, e.g. "grammar_lecture".</summary>
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string SystemText { get; set; } = string.Empty;
    /// <summary>User message with placeholders such as {language}, {level}, {topic}, {nativeLanguage}.</summary>
    public string PromptText { get; set; } = string.Empty;
    /// <summary>Optional model override for this prompt; empty uses the Ai.Model setting.</summary>
    public string? Model { get; set; }
    public double Temperature { get; set; } = 0.4;
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByEmail { get; set; }
}

/// <summary>One AI call: tokens, cost and outcome (SRS 7.6, 5.4).</summary>
public class AiUsageLog
{
    public long Id { get; set; }
    public AiFeature Feature { get; set; }
    public Guid? UserId { get; set; }
    public int? RequestId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public int DurationMs { get; set; }
    public AiCallStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A queued or running AI job and its progress (FR-A21 .. FR-A24, SRS 5.3 "long jobs").</summary>
public class AiGenerationRequest
{
    public int Id { get; set; }
    public AiRequestType Type { get; set; }
    public AiRequestStatus Status { get; set; }
    public int? LanguageId { get; set; }
    public Language? Language { get; set; }
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public int? LectureId { get; set; }
    public int? AttemptId { get; set; }
    /// <summary>Request parameters as JSON (levels, hours, counts, types…).</summary>
    public string ParametersJson { get; set; } = "{}";
    /// <summary>Admin notes added to every prompt (FR-A24), e.g. "focus on Gulf Arabic, business context".</summary>
    public string? Notes { get; set; }
    /// <summary>"roadmap" or "content" for multi-stage jobs.</summary>
    public string? Stage { get; set; }

    // Estimate shown before approval (FR-A22)
    public int EstimatedModules { get; set; }
    public int EstimatedLectures { get; set; }
    public int EstimatedQuestions { get; set; }
    public decimal EstimatedCost { get; set; }

    public int ProgressDone { get; set; }
    public int ProgressTotal { get; set; }
    public decimal ActualCost { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }

    public Guid? RequestedBy { get; set; }
    public string? RequestedByEmail { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecidedByEmail { get; set; }
    public string? DecisionComment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Set while a runner works on it, so two runners never pick the same job.</summary>
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>Provenance of an AI draft: shown to reviewers with the prompt and model (FR-A72).</summary>
public class AiGeneratedItem
{
    public int Id { get; set; }
    public int? RequestId { get; set; }
    public AiGenerationRequest? Request { get; set; }
    public ReviewEntityType EntityType { get; set; }
    public int EntityId { get; set; }
    public AiFeature Feature { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Low-resource languages need a native speaker check (SRS 5.3).</summary>
    public bool NeedsNativeCheck { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
