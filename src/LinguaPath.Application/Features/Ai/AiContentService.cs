using System.Diagnostics;
using System.Globalization;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinguaPath.Application.Features.Ai;

/// <summary>Who an AI call is for, so usage is logged against the user and the job.</summary>
public sealed record AiCallContext(Guid? UserId, int? RequestId);

public sealed record AiBudgetStatus(decimal Spent, decimal Budget, int Percent, int AlertPercent)
{
    public bool IsExceeded => Budget > 0 && Spent >= Budget;
    public bool IsAlert => Budget > 0 && Percent >= AlertPercent;
}

/// <summary>
/// The one entry point for AI (SRS 5): every call goes through a stored prompt template, is checked against the monthly
/// budget, validated as JSON (retried once), and logged with tokens and cost. Providers are swapped in settings.
/// </summary>
public interface IAiContentService
{
    Task<AiBudgetStatus> BudgetAsync(CancellationToken ct = default);

    Task<Result<AiOutcome<AiRoadmap>>> RoadmapAsync(string language, RoadmapParameters p, string? notes, AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiLectureDraft>>> LectureAsync(string language, CefrLevel level, string title, string? topic, string? objectives,
        string nativeLanguage, string? notes, AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiAssistResult>>> AssistAsync(string language, CefrLevel level, AssistAction action, string html, string nativeLanguage,
        AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiQuestionSet>>> QuestionsAsync(string language, CefrLevel level, string lectureText, QuestionParameters p,
        AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiPostSet>>> PostsAsync(string language, PostParameters p, string? notes, AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiWritingGrade>>> GradeWritingAsync(string language, CefrLevel level, string task, string answer, string nativeLanguage,
        AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiPracticeSet>>> PracticeItemsAsync(string language, PracticeParameters p, string? notes, AiCallContext ctx,
        CancellationToken ct = default);

    /// <summary>Learner writing practice (FR-L91): same shape as exam grading, friendlier prompt.</summary>
    Task<Result<AiOutcome<AiWritingGrade>>> CheckWritingAsync(string language, CefrLevel level, string task, string text, string nativeLanguage,
        AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiModerationResult>>> ModerateAsync(string message, AiCallContext ctx, CancellationToken ct = default);

    Task<Result<AiOutcome<AiConversationReply>>> ConverseAsync(string language, CefrLevel level, string topic, string history, string message,
        string nativeLanguage, AiCallContext ctx, CancellationToken ct = default);
}

internal sealed class AiContentService(
    IApplicationDbContext db,
    IAiChatClientFactory clients,
    ISettingsService settings,
    IAuditLogger audit,
    TimeProvider clock,
    ILogger<AiContentService> logger) : IAiContentService
{
    public async Task<AiBudgetStatus> BudgetAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        // Sum in memory: decimal SUM isn't supported by every provider (SQLite in tests).
        var costs = await db.AiUsageLogs.AsNoTracking().Where(l => l.CreatedAt >= monthStart).Select(l => l.Cost).ToListAsync(ct);
        var spent = costs.Sum();
        var budget = await DecimalAsync(SettingKeys.AiMonthlyBudgetUsd, 100m, ct);
        var alert = await settings.GetIntAsync(SettingKeys.AiBudgetAlertPercent, 80, ct);
        var percent = budget <= 0 ? 0 : (int)Math.Floor(100m * spent / budget);
        return new AiBudgetStatus(spent, budget, percent, alert);
    }

    // ------------------------------------------------------------------ features

    public Task<Result<AiOutcome<AiRoadmap>>> RoadmapAsync(string language, RoadmapParameters p, string? notes, AiCallContext ctx,
        CancellationToken ct = default)
    {
        var months = Math.Clamp((int)Math.Ceiling(p.TotalHours / 15.0), 1, 24);
        return RunAsync<AiRoadmap>(AiFeature.Roadmap, new Dictionary<string, string?>
        {
            ["language"] = language, ["startLevel"] = p.StartLevel.ToString(), ["endLevel"] = p.EndLevel.ToString(),
            ["hours"] = p.TotalHours.ToString(CultureInfo.InvariantCulture), ["months"] = months.ToString(CultureInfo.InvariantCulture),
            ["focus"] = p.Focus, ["nativeLanguage"] = p.NativeLanguage, ["notes"] = notes
        }, AiOutputParser.ValidateRoadmap, ctx, 6000, ct);
    }

    public Task<Result<AiOutcome<AiLectureDraft>>> LectureAsync(string language, CefrLevel level, string title, string? topic, string? objectives,
        string nativeLanguage, string? notes, AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiLectureDraft>(AiFeature.GrammarLecture, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = level.ToString(), ["title"] = title, ["topic"] = topic ?? title,
            ["objectives"] = objectives, ["nativeLanguage"] = nativeLanguage, ["notes"] = notes
        }, AiOutputParser.ValidateLecture, ctx, 6000, ct);

    public Task<Result<AiOutcome<AiAssistResult>>> AssistAsync(string language, CefrLevel level, AssistAction action, string html,
        string nativeLanguage, AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiAssistResult>(AiFeature.LectureAssist, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = level.ToString(), ["instruction"] = AiPrompts.AssistInstruction(action),
            ["action"] = action.ToString(), ["text"] = html, ["nativeLanguage"] = nativeLanguage
        }, AiOutputParser.ValidateAssist, ctx, 4000, ct);

    public Task<Result<AiOutcome<AiQuestionSet>>> QuestionsAsync(string language, CefrLevel level, string lectureText, QuestionParameters p,
        AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiQuestionSet>(AiFeature.QuizFromLecture, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = level.ToString(), ["count"] = p.Count.ToString(CultureInfo.InvariantCulture),
            ["types"] = string.Join(", ", p.Types.Distinct()), ["difficulty"] = p.Difficulty, ["lectureText"] = lectureText
        }, AiOutputParser.ValidateQuestions, ctx, 4000, ct);

    public Task<Result<AiOutcome<AiPostSet>>> PostsAsync(string language, PostParameters p, string? notes, AiCallContext ctx,
        CancellationToken ct = default) =>
        RunAsync<AiPostSet>(AiFeature.Posts, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = p.Level.ToString(), ["count"] = p.Count.ToString(CultureInfo.InvariantCulture),
            ["postType"] = p.PostType switch { PostType.WordOfTheDay => "word of the day", PostType.CulturalNote => "cultural note", _ => "tip" },
            ["month"] = p.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), ["nativeLanguage"] = p.NativeLanguage, ["notes"] = notes
        }, AiOutputParser.ValidatePosts, ctx, 6000, ct);

    public Task<Result<AiOutcome<AiWritingGrade>>> GradeWritingAsync(string language, CefrLevel level, string task, string answer,
        string nativeLanguage, AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiWritingGrade>(AiFeature.WritingGrading, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = level.ToString(), ["prompt"] = task, ["answer"] = answer, ["nativeLanguage"] = nativeLanguage
        }, AiOutputParser.ValidateWritingGrade, ctx, 1500, ct);

    public Task<Result<AiOutcome<AiPracticeSet>>> PracticeItemsAsync(string language, PracticeParameters p, string? notes, AiCallContext ctx,
        CancellationToken ct = default)
    {
        var (kind, instruction) = AiPrompts.PracticeInstruction(p.Type);
        return RunAsync<AiPracticeSet>(AiFeature.PracticeItems, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = p.Level.ToString(), ["count"] = p.Count.ToString(CultureInfo.InvariantCulture),
            ["kind"] = kind, ["instruction"] = instruction, ["topic"] = string.IsNullOrWhiteSpace(p.Topic) ? "everyday life" : p.Topic,
            ["type"] = p.Type.ToString(), ["nativeLanguage"] = p.NativeLanguage, ["notes"] = notes
        }, AiOutputParser.ValidatePractice, ctx, 6000, ct);
    }

    public Task<Result<AiOutcome<AiWritingGrade>>> CheckWritingAsync(string language, CefrLevel level, string task, string text,
        string nativeLanguage, AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiWritingGrade>(AiFeature.WritingCheck, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = level.ToString(), ["prompt"] = task, ["answer"] = text, ["nativeLanguage"] = nativeLanguage
        }, AiOutputParser.ValidateWritingCheck, ctx, 1500, ct);

    public Task<Result<AiOutcome<AiConversationReply>>> ConverseAsync(string language, CefrLevel level, string topic, string history,
        string message, string nativeLanguage, AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiConversationReply>(AiFeature.Conversation, new Dictionary<string, string?>
        {
            ["language"] = language, ["level"] = level.ToString(), ["topic"] = topic, ["history"] = history,
            ["message"] = message, ["nativeLanguage"] = nativeLanguage
        }, AiOutputParser.ValidateConversation, ctx, 600, ct);

    public Task<Result<AiOutcome<AiModerationResult>>> ModerateAsync(string message, AiCallContext ctx, CancellationToken ct = default) =>
        RunAsync<AiModerationResult>(AiFeature.Moderation, new Dictionary<string, string?>
        {
            ["message"] = message.Replace("<<<", "").Replace(">>>", "")
        }, AiOutputParser.ValidateModeration, ctx, 200, ct);

    // ------------------------------------------------------------------ the pipeline

    private async Task<Result<AiOutcome<T>>> RunAsync<T>(AiFeature feature, IReadOnlyDictionary<string, string?> values,
        Func<T, string?> validate, AiCallContext ctx, int maxTokens, CancellationToken ct) where T : class
    {
        // 5.4: generation pauses at 100% of the monthly budget.
        var budget = await BudgetAsync(ct);
        if (budget.IsExceeded)
        {
            await LogAsync(feature, ctx, "-", "-", 0, 0, 0, 0, AiCallStatus.BudgetExceeded, "Monthly AI budget used up.", ct);
            return Error.Conflict("ai_budget_exceeded",
                $"The monthly AI budget (${budget.Budget:0.##}) is used up. Raise Ai.MonthlyBudgetUsd in Settings or wait for next month.");
        }

        var template = await TemplateAsync(AiPrompts.KeyFor(feature), ct);
        var system = AiPrompts.Render(template.System, values);
        var user = AiPrompts.Render(template.Prompt, values);

        IAiChatClient client;
        string provider, model;
        try
        {
            (client, provider, model) = await clients.CreateAsync(ct);
        }
        catch (AiProviderException ex)
        {
            return Error.Conflict("ai_not_configured", ex.Message);
        }
        if (!string.IsNullOrWhiteSpace(template.Model)) model = template.Model!;

        var prompt = $"[system]\n{system}\n\n[user]\n{user}";
        decimal totalCost = 0;
        string? lastError = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var message = attempt == 1 ? user
                : $"{user}\n\nYour previous answer was rejected: {lastError} Reply again with one valid JSON object that matches the schema.";
            var watch = Stopwatch.StartNew();
            AiChatResult result;
            try
            {
                result = await client.CompleteAsync(new AiChatRequest(feature, system, message, model, template.Temperature, true, maxTokens), ct);
            }
            catch (Exception ex) when (ex is AiProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await LogAsync(feature, ctx, provider, model, 0, 0, 0, (int)watch.ElapsedMilliseconds, AiCallStatus.Failed, ex.Message, ct);
                logger.LogWarning(ex, "AI call for {Feature} failed", feature);
                return Error.Conflict("ai_provider_error", "The AI provider didn't answer: " + ex.Message);
            }

            // The offline generator (development and tests) costs nothing.
            var cost = result.Provider == AiProviders.Offline ? 0m : await CostAsync(result.InputTokens, result.OutputTokens, ct);
            totalCost += cost;
            var (value, error) = AiOutputParser.Parse(result.Content, validate);
            await LogAsync(feature, ctx, result.Provider, result.Model, result.InputTokens, result.OutputTokens, cost,
                (int)watch.ElapsedMilliseconds, error is null ? AiCallStatus.Success : AiCallStatus.InvalidOutput, error, ct);

            if (value is not null)
            {
                await AlertIfNeededAsync(budget, ct);
                return new AiOutcome<T>(value, prompt, result.Model, totalCost);
            }
            lastError = error;
        }

        return Error.Conflict("ai_invalid_output", "The AI answer didn't match the expected format twice: " + lastError);
    }

    private async Task<AiPrompts.Default> TemplateAsync(string key, CancellationToken ct)
    {
        var stored = await db.AiPromptTemplates.AsNoTracking().Where(t => t.Key == key && t.IsActive)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync(ct);
        return stored is null
            ? AiPrompts.DefaultFor(key)
            : new AiPrompts.Default(stored.Key, stored.SystemText, stored.PromptText, stored.Temperature, stored.Notes ?? "") { Model = stored.Model };
    }

    private async Task<decimal> CostAsync(int input, int output, CancellationToken ct)
    {
        var inPrice = await DecimalAsync(SettingKeys.AiInputPricePerMillion, 0.27m, ct);
        var outPrice = await DecimalAsync(SettingKeys.AiOutputPricePerMillion, 1.10m, ct);
        return AiCost.For(input, output, inPrice, outPrice);
    }

    private async Task<decimal> DecimalAsync(string key, decimal fallback, CancellationToken ct) =>
        decimal.TryParse(await settings.GetAsync(key, ct), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private async Task LogAsync(AiFeature feature, AiCallContext ctx, string provider, string model, int input, int output, decimal cost,
        int ms, AiCallStatus status, string? error, CancellationToken ct)
    {
        db.AiUsageLogs.Add(new AiUsageLog
        {
            Feature = feature, UserId = ctx.UserId, RequestId = ctx.RequestId, Provider = provider, Model = model,
            InputTokens = input, OutputTokens = output, Cost = cost, DurationMs = ms, Status = status,
            Error = error is { Length: > 1000 } ? error[..1000] : error, CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>5.4: alert once when spending crosses the alert threshold.</summary>
    private async Task AlertIfNeededAsync(AiBudgetStatus before, CancellationToken ct)
    {
        if (before.IsAlert) return;
        var after = await BudgetAsync(ct);
        if (!after.IsAlert) return;
        logger.LogWarning("AI budget alert: {Percent}% of ${Budget} used this month", after.Percent, after.Budget);
        await audit.LogAsync("AiBudgetAlert", "AiBudget", DateTime.UtcNow.ToString("yyyy-MM"), null,
            new { after.Spent, after.Budget, after.Percent }, ct);
    }
}

public static class AiProviders
{
    public const string Offline = "Offline";
    public const string OfflineModel = "offline-sample";
    public const string DeepSeek = "DeepSeek";
    public const string OpenAI = "OpenAI";
}

public static class AiCost
{
    public static decimal For(int inputTokens, int outputTokens, decimal inputPerMillion, decimal outputPerMillion) =>
        Math.Round(inputTokens * inputPerMillion / 1_000_000m + outputTokens * outputPerMillion / 1_000_000m, 6);
}

/// <summary>FR-A22: scope and cost shown before a Super Admin approves a whole-language job.</summary>
public static class AiEstimator
{
    public const int AverageLectureMinutes = 20;
    public const int LecturesPerModule = 6;

    public sealed record Estimate(int Modules, int Lectures, int Questions, decimal Cost, int InputTokens, int OutputTokens);

    public static Estimate ForCourse(int totalHours, bool withContent, int questionsPerLecture, decimal inPrice, decimal outPrice)
    {
        var lectures = Math.Max(4, (int)Math.Round(totalHours * 60.0 / AverageLectureMinutes));
        var modules = (int)Math.Ceiling(lectures / (double)LecturesPerModule);
        var questions = withContent ? lectures * questionsPerLecture : 0;

        // Rough token budgets per call, from typical prompt and answer sizes.
        var input = 900 + (withContent ? lectures * (900 + 2600) : 0);
        var output = 400 + lectures * 120 + (withContent ? lectures * (3000 + 300 * questionsPerLecture) : 0);
        return new Estimate(modules, lectures, questions, AiCost.For(input, output, inPrice, outPrice), input, output);
    }
}
