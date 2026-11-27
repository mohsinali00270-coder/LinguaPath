using System.ComponentModel.DataAnnotations;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Ai;

public record AiRequestListItem(
    int Id, AiRequestType Type, AiRequestStatus Status, string Title, string? LanguageName, string? CourseTitle, int? CourseId,
    int ProgressDone, int ProgressTotal, decimal EstimatedCost, decimal ActualCost, string? RequestedBy, DateTimeOffset CreatedAt, string? Error)
{
    public int ProgressPercent => ProgressTotal <= 0 ? (Status == AiRequestStatus.Completed ? 100 : 0) : 100 * ProgressDone / ProgressTotal;
}

public record AiItemRow(ReviewEntityType EntityType, int EntityId, string Title, ContentStatus? Status, bool NeedsNativeCheck, string Model,
    DateTimeOffset CreatedAt);

public record AiRequestDetail(
    AiRequestListItem Item, string ParametersJson, string? Notes, string? Stage, int EstimatedModules, int EstimatedLectures,
    int EstimatedQuestions, string? Summary, string? DecisionComment, string? DecidedBy, DateTimeOffset? DecidedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int? LectureId, string? LectureTitle, int? AttemptId,
    IReadOnlyList<AiItemRow> Items, int Calls, int InputTokens, int OutputTokens);

/// <summary>Shown to reviewers next to an AI draft (FR-A72).</summary>
public record AiProvenance(int? RequestId, AiFeature Feature, string Model, string Prompt, bool NeedsNativeCheck, DateTimeOffset CreatedAt);

public class RoadmapAiInput
{
    [Range(1, int.MaxValue, ErrorMessage = "Choose a language."), Display(Name = "Language")] public int LanguageId { get; set; }
    [StringLength(150), Display(Name = "Course title (optional)")] public string? CourseTitle { get; set; }
    [Display(Name = "From level")] public CefrLevel StartLevel { get; set; } = CefrLevel.A1;
    [Display(Name = "To level")] public CefrLevel EndLevel { get; set; } = CefrLevel.A2;
    [Range(4, 500), Display(Name = "Total study hours")] public int TotalHours { get; set; } = 40;
    [StringLength(200), Display(Name = "Focus (e.g. travel, business)")] public string? Focus { get; set; }
    [Required, StringLength(50), Display(Name = "Learners' native language (for explanations)")] public string NativeLanguage { get; set; } = "English";
    [StringLength(1000), Display(Name = "Notes for the AI")] public string? Notes { get; set; }
    [Display(Name = "Also write the lectures and questions after I approve the roadmap")] public bool GenerateContent { get; set; }
}

public class QuestionsAiInput
{
    [Range(1, int.MaxValue, ErrorMessage = "Choose a lecture.")] public int LectureId { get; set; }
    [Range(1, 30), Display(Name = "How many questions")] public int Count { get; set; } = 8;
    [Display(Name = "Types")] public List<QuestionType> Types { get; set; } =
        [QuestionType.MultipleChoice, QuestionType.FillBlank, QuestionType.TrueFalse, QuestionType.Translate];
    [Display(Name = "Difficulty mix")] public string Difficulty { get; set; } = "mixed";
    [StringLength(1000), Display(Name = "Notes for the AI")] public string? Notes { get; set; }
}

public class PostsAiInput
{
    [Range(1, int.MaxValue, ErrorMessage = "Choose a language."), Display(Name = "Language")] public int LanguageId { get; set; }
    [Display(Name = "Post type")] public PostType PostType { get; set; } = PostType.WordOfTheDay;
    [Display(Name = "Level")] public CefrLevel Level { get; set; } = CefrLevel.A1;
    [Display(Name = "Month")] public DateTime Month { get; set; } = DateTime.UtcNow.AddMonths(1);
    [Range(1, 31), Display(Name = "How many (one per day)")] public int Count { get; set; } = 30;
    [Required, StringLength(50), Display(Name = "Learners' native language")] public string NativeLanguage { get; set; } = "English";
    [StringLength(1000), Display(Name = "Notes for the AI")] public string? Notes { get; set; }
}

public class PracticeAiInput
{
    [Range(1, int.MaxValue, ErrorMessage = "Choose a language."), Display(Name = "Language")] public int LanguageId { get; set; }
    [Display(Name = "Exercise type")] public PracticeType Type { get; set; } = PracticeType.Read;
    [Display(Name = "Level")] public CefrLevel Level { get; set; } = CefrLevel.A1;
    [Range(1, 20), Display(Name = "How many")] public int Count { get; set; } = 5;
    [StringLength(100), Display(Name = "Topic")] public string? Topic { get; set; }
    [Required, StringLength(50), Display(Name = "Learners' native language")] public string NativeLanguage { get; set; } = "English";
    [StringLength(1000), Display(Name = "Notes for the AI")] public string? Notes { get; set; }
}

public record AiFeatureUsage(AiFeature Feature, int Calls, int Failed, long InputTokens, long OutputTokens, decimal Cost);
public record AiDayUsage(DateOnly Day, decimal Cost, int Calls);
public record AiUsageRow(DateTimeOffset At, AiFeature Feature, string Provider, string Model, int InputTokens, int OutputTokens, decimal Cost,
    int DurationMs, AiCallStatus Status, string? Error, int? RequestId);

public record AiUsageReport(AiBudgetStatus Budget, string Provider, string Model, IReadOnlyList<AiFeatureUsage> ByFeature,
    IReadOnlyList<AiDayUsage> ByDay, IReadOnlyList<AiUsageRow> Recent);

public record PromptTemplateRow(string Key, int ActiveVersion, int Versions, bool IsBuiltIn, string? Notes, DateTimeOffset? UpdatedAt);
public record PromptVersionRow(int Id, int Version, bool IsActive, string? Model, double Temperature, string? Notes, string? CreatedBy, DateTimeOffset CreatedAt);

public class PromptTemplateInput
{
    [Required] public string Key { get; set; } = string.Empty;
    [Required, StringLength(8000), Display(Name = "System message")] public string SystemText { get; set; } = string.Empty;
    [Required, StringLength(16000), Display(Name = "Prompt")] public string PromptText { get; set; } = string.Empty;
    [StringLength(100), Display(Name = "Model override (optional)")] public string? Model { get; set; }
    [Range(0, 2)] public double Temperature { get; set; } = 0.4;
    [StringLength(500), Display(Name = "What changed?")] public string? Notes { get; set; }
}

public record PromptEditorModel(PromptTemplateInput Input, int ActiveVersion, IReadOnlyList<PromptVersionRow> Versions, IReadOnlyList<string> Placeholders);
