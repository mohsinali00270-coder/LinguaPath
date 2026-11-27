using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Ai;

// ===================================================================== provider abstraction

/// <param name="Feature">Lets the offline provider answer in the right shape; also logged.</param>
/// <param name="JsonMode">Ask the provider for a JSON object (SRS 5.3).</param>
public sealed record AiChatRequest(AiFeature Feature, string System, string User, string? Model, double Temperature, bool JsonMode = true, int MaxTokens = 4000);

public sealed record AiChatResult(string Content, string Provider, string Model, int InputTokens, int OutputTokens);

/// <summary>One chat completion. Implementations: OpenAI-compatible HTTP (DeepSeek, OpenAI) and an offline generator.</summary>
public interface IAiChatClient
{
    Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default);
}

/// <summary>Picks the client for the provider in settings (FR-A110), so providers can be swapped without a deploy.</summary>
public interface IAiChatClientFactory
{
    Task<(IAiChatClient Client, string Provider, string Model)> CreateAsync(CancellationToken ct = default);
}

/// <summary>Raised by providers for network/HTTP problems; the job is marked Failed and can be retried.</summary>
public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A validated AI result plus what produced it (FR-A72: prompt and model are shown to reviewers).</summary>
public sealed record AiOutcome<T>(T Value, string Prompt, string Model, decimal Cost);

// ===================================================================== structured outputs (SRS 5.2)

public sealed class AiRoadmap
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<AiRoadmapModule> Modules { get; set; } = [];
}

public sealed class AiRoadmapModule
{
    public string Title { get; set; } = "";
    public string? Objective { get; set; }
    public string? Level { get; set; }
    public int Month { get; set; } = 1;
    public List<AiRoadmapLecture> Lectures { get; set; } = [];
}

public sealed class AiRoadmapLecture
{
    public string Title { get; set; } = "";
    public string? Objective { get; set; }
    public string? GrammarPoint { get; set; }
    public int Minutes { get; set; } = 15;
    public bool IsCore { get; set; } = true;
}

public sealed class AiLectureDraft
{
    public string? Objectives { get; set; }
    public string Html { get; set; } = "";
    public List<AiExample> Examples { get; set; } = [];
    public List<AiWord> Vocabulary { get; set; } = [];
}

public sealed class AiExample
{
    public string Target { get; set; } = "";
    public string? Translation { get; set; }
    public string? Note { get; set; }
}

public sealed class AiWord
{
    public string Word { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string? Pronunciation { get; set; }
    public string? PartOfSpeech { get; set; }
}

public sealed class AiAssistResult
{
    public string Html { get; set; } = "";
}

public sealed class AiQuestionSet
{
    public List<AiQuestionDraft> Questions { get; set; } = [];
}

public sealed class AiQuestionDraft
{
    public string Type { get; set; } = "";
    public string? Skill { get; set; }
    public int Difficulty { get; set; } = 2;
    public string Stem { get; set; } = "";
    public string? Prompt { get; set; }
    public List<AiOption> Options { get; set; } = [];
    public string? Explanation { get; set; }
    public string? SourceSentence { get; set; }
}

public sealed class AiOption
{
    public string Text { get; set; } = "";
    public bool Correct { get; set; }
    public string? Match { get; set; }
}

public sealed class AiPostSet
{
    public List<AiPostDraft> Posts { get; set; } = [];
}

public sealed class AiPostDraft
{
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed class AiWritingGrade
{
    /// <summary>0 .. 100.</summary>
    public double Score { get; set; }
    /// <summary>0 .. 1: how sure the model is (FR-L63: low confidence goes to a reviewer).</summary>
    public double Confidence { get; set; }
    public string? Feedback { get; set; }
    public List<AiCorrection> Corrections { get; set; } = [];
}

public sealed class AiCorrection
{
    public string Original { get; set; } = "";
    public string Corrected { get; set; } = "";
    public string? Explanation { get; set; }
}

public sealed class AiPracticeSet
{
    public List<AiPracticeDraft> Items { get; set; } = [];
}

public sealed class AiPracticeDraft
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Translation { get; set; }
    public List<AiReadingQuestion> Questions { get; set; } = [];
}

public sealed class AiReadingQuestion
{
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public int Correct { get; set; }
    public string? Explanation { get; set; }
}

public sealed class AiModerationResult
{
    public string Category { get; set; } = "none";
    public double Score { get; set; }
    public string? Reason { get; set; }
}

public sealed class AiConversationReply
{
    public string Reply { get; set; } = "";
    public string? Correction { get; set; }
    public string? Explanation { get; set; }
}

// ===================================================================== request parameters (stored as JSON on the request)

public sealed class RoadmapParameters
{
    public string? CourseTitle { get; set; }
    public CefrLevel StartLevel { get; set; } = CefrLevel.A1;
    public CefrLevel EndLevel { get; set; } = CefrLevel.A2;
    public int TotalHours { get; set; } = 40;
    public string? Focus { get; set; }
    public string NativeLanguage { get; set; } = "English";
    /// <summary>New-language and full-course jobs continue with lectures and questions after the roadmap is approved.</summary>
    public bool GenerateContent { get; set; }
}

public sealed class QuestionParameters
{
    public int Count { get; set; } = 5;
    public List<QuestionType> Types { get; set; } = [QuestionType.MultipleChoice, QuestionType.FillBlank, QuestionType.TrueFalse];
    /// <summary>"easy", "mixed" or "hard".</summary>
    public string Difficulty { get; set; } = "mixed";
}

public sealed class PostParameters
{
    public PostType PostType { get; set; } = PostType.WordOfTheDay;
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    /// <summary>First day of the month the posts are scheduled for.</summary>
    public DateOnly Month { get; set; }
    public int Count { get; set; } = 30;
    public string NativeLanguage { get; set; } = "English";
}

public sealed class PracticeParameters
{
    public PracticeType Type { get; set; } = PracticeType.Read;
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    public int Count { get; set; } = 5;
    public string? Topic { get; set; }
    public string NativeLanguage { get; set; } = "English";
}

public sealed class LectureParameters
{
    public string NativeLanguage { get; set; } = "English";
}
