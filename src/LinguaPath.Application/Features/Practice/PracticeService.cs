using System.Text;
using System.Text.Json;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Practice;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Practice;

/// <summary>Practice tab: read, write, speak, listen and free-talk (FR-L90 .. FR-L93).</summary>
public interface IPracticeService
{
    Task<Result<PracticeSummaryDto>> GetSummaryAsync(Guid userId, int? languageId, CancellationToken ct = default);

    /// <summary>The next exercise of a type at the learner's level; unseen ones first, then the least recently done.</summary>
    Task<Result<PracticeItemDto>> NextAsync(Guid userId, PracticeType type, int? languageId, int? skipId, CancellationToken ct = default);

    Task<Result<ReadResultDto>> AnswerReadingAsync(Guid userId, int itemId, ReadAnswersRequest request, CancellationToken ct = default);
    Task<Result<WriteCheckDto>> CheckWritingAsync(Guid userId, WriteCheckRequest request, CancellationToken ct = default);

    /// <param name="referenceText">Used when there is no item: any sentence the learner wants to practise.</param>
    Task<Result<SpeakResultDto>> AssessSpeakingAsync(Guid userId, int languageId, int? itemId, string? referenceText, byte[] audio,
        CancellationToken ct = default);

    Task<Result<ListenResultDto>> CheckListeningAsync(Guid userId, int itemId, ListenCheckRequest request, CancellationToken ct = default);
    Task<Result<ConversationReplyDto>> ConverseAsync(Guid userId, ConversationRequest request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<PracticeHistoryDto>>> HistoryAsync(Guid userId, PracticeType? type, CancellationToken ct = default);
}

internal sealed class PracticeService(
    IApplicationDbContext db,
    IAiContentService ai,
    ISpeechAssessor speech,
    IFileStorage storage,
    ISettingsService settings,
    Progress.IProgressService progress,
    TimeProvider clock) : IPracticeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const int MaxWritingLength = 3000;
    public const int MaxMessageLength = 500;
    public const int MaxAudioBytes = PracticeRules.MaxAudioBytes;
    /// <summary>Short-audio speech recognition takes up to 60 seconds.</summary>
    public static readonly TimeSpan MaxAudioLength = TimeSpan.FromSeconds(60);

    public static readonly IReadOnlyList<string> Topics =
    [
        "Introduce yourself", "Family and friends", "Food and cooking", "Hobbies", "Travel", "Work and study",
        "Shopping", "Weekend plans", "Your town", "Health and sport"
    ];

    private sealed record Learner(Language Language, CefrLevel Level, string NativeLanguage, string TimeZone);

    // ================================================================== overview

    public async Task<Result<PracticeSummaryDto>> GetSummaryAsync(Guid userId, int? languageId, CancellationToken ct = default)
    {
        var learner = await LearnerAsync(userId, languageId, ct);
        if (!learner.IsSuccess) return learner.Error!;
        var (language, level, _, timeZone) = learner.Value;

        var done = await db.PracticeAttempts.AsNoTracking().Where(a => a.UserId == userId && a.LanguageId == language.Id)
            .GroupBy(a => a.Type)
            .Select(g => new { Type = g.Key, Count = g.Count(), Average = g.Average(a => (double?)a.Score) })
            .ToListAsync(ct);
        var available = await db.PracticeItems.AsNoTracking()
            .Where(p => p.LanguageId == language.Id && p.Status == ContentStatus.Published)
            .GroupBy(p => p.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync(ct);

        var types = Enum.GetValues<PracticeType>().Select(t =>
        {
            var d = done.FirstOrDefault(x => x.Type == t);
            return new PracticeTypeSummaryDto(t, d?.Count ?? 0, d?.Average is { } avg ? (int)Math.Round(avg) : null,
                t is PracticeType.Conversation or PracticeType.Write ? -1 : available.FirstOrDefault(x => x.Type == t)?.Count ?? 0);
        }).ToList();

        var now = clock.GetUtcNow();
        var wordsDue = await db.UserWords.CountAsync(w => w.UserId == userId && w.LanguageId == language.Id && w.NextReviewAt <= now, ct);
        var dayStart = PracticeRules.DayStartUtc(now, timeZone);
        var limit = await settings.GetIntAsync(SettingKeys.DailyTranslationLimit, 100, ct);
        var used = await db.TranslationEntries.CountAsync(t => t.UserId == userId && t.CreatedAt >= dayStart, ct);

        return new PracticeSummaryDto(language.Id, language.Name, level, types, wordsDue, Math.Max(0, limit - used), Topics);
    }

    public async Task<Result<PracticeItemDto>> NextAsync(Guid userId, PracticeType type, int? languageId, int? skipId,
        CancellationToken ct = default)
    {
        if (type == PracticeType.Conversation) return Error.Validation("Type", "Conversation has no exercises; start a chat instead.");
        var learner = await LearnerAsync(userId, languageId, ct);
        if (!learner.IsSuccess) return learner.Error!;
        var (language, level, _, _) = learner.Value;

        var candidates = await db.PracticeItems.AsNoTracking()
            .Where(p => p.LanguageId == language.Id && p.Type == type && p.Status == ContentStatus.Published)
            .Select(p => new { p.Id, p.Level }).ToListAsync(ct);
        if (candidates.Count == 0)
            return Error.NotFound($"A {type.ToString().ToLowerInvariant()} exercise for {language.Name}");

        var lastDone = await db.PracticeAttempts.AsNoTracking()
            .Where(a => a.UserId == userId && a.PracticeItemId != null && a.Type == type)
            .GroupBy(a => a.PracticeItemId!.Value)
            .Select(g => new { Id = g.Key, Last = g.Max(a => a.Id) })
            .ToListAsync(ct);
        var seen = lastDone.ToDictionary(x => x.Id, x => x.Last);

        // The learner's level first, then the closest lower level (easier), then anything.
        var pick = candidates
            .Where(c => c.Id != skipId || candidates.Count == 1)
            .OrderBy(c => c.Level == level ? 0 : c.Level < level ? 1 + (level - c.Level) : 10 + (c.Level - level))
            .ThenBy(c => seen.TryGetValue(c.Id, out var last) ? last : 0)
            .ThenBy(c => c.Id)
            .First();

        var item = await db.PracticeItems.AsNoTracking().FirstAsync(p => p.Id == pick.Id, ct);
        return ToDto(item, language);
    }

    // ================================================================== read (FR-L90)

    public async Task<Result<ReadResultDto>> AnswerReadingAsync(Guid userId, int itemId, ReadAnswersRequest request, CancellationToken ct = default)
    {
        var item = await PublishedAsync(itemId, PracticeType.Read, ct);
        if (item is null) return Error.NotFound("Reading exercise");

        var result = ReadingQuestions.Grade(item.QuestionsJson, request.Answers ?? []);
        await RecordAsync(userId, item.LanguageId, PracticeType.Read, item.Id, item.Title, string.Join(",", request.Answers ?? []), null,
            result.Results, result.Score, false, ct);
        return result;
    }

    // ================================================================== write (FR-L91)

    public async Task<Result<WriteCheckDto>> CheckWritingAsync(Guid userId, WriteCheckRequest request, CancellationToken ct = default)
    {
        var text = request.Text?.Trim() ?? "";
        if (text.Length < 3) return Error.Validation("Text", "Write a few words first.");
        if (text.Length > MaxWritingLength) return Error.Validation("Text", $"Keep it under {MaxWritingLength} characters.");

        PracticeItem? item = null;
        if (request.ItemId is { } id)
        {
            item = await PublishedAsync(id, PracticeType.Write, ct);
            if (item is null) return Error.NotFound("Writing task");
        }
        var learner = await LearnerAsync(userId, item?.LanguageId ?? request.LanguageId, ct);
        if (!learner.IsSuccess) return learner.Error!;
        var (language, level, native, timeZone) = learner.Value;

        var remaining = await AiCallsLeftAsync(userId, timeZone, ct);
        if (remaining <= 0) return AiLimitError();

        var task = item?.Text ?? (string.IsNullOrWhiteSpace(request.Prompt) ? "Free writing on any topic." : Clip(request.Prompt, 500));
        var outcome = await ai.CheckWritingAsync(language.Name, item?.Level ?? level, task, text, native, new AiCallContext(userId, null), ct);
        if (!outcome.IsSuccess) return AiUnavailable(outcome.Error!);

        var g = outcome.Value.Value;
        var corrections = g.Corrections.Where(c => !string.IsNullOrWhiteSpace(c.Original))
            .Take(20).Select(c => new CorrectionDto(c.Original, c.Corrected, c.Explanation)).ToList();
        var score = (int)Math.Round(Math.Clamp(g.Score, 0, 100));
        var sample = outcome.Value.Model == AiProviders.OfflineModel;
        await RecordAsync(userId, language.Id, PracticeType.Write, item?.Id, task, text, null,
            new { g.Feedback, corrections }, score, sample, ct);
        return new WriteCheckDto(score, g.Feedback ?? "", corrections, remaining - 1, sample);
    }

    // ================================================================== speak (FR-L92)

    public async Task<Result<SpeakResultDto>> AssessSpeakingAsync(Guid userId, int languageId, int? itemId, string? referenceText, byte[] audio,
        CancellationToken ct = default)
    {
        PracticeItem? item = null;
        if (itemId is { } id)
        {
            item = await PublishedAsync(id, PracticeType.Speak, ct);
            if (item is null) return Error.NotFound("Speaking exercise");
        }
        var reference = (item?.Text ?? referenceText)?.Trim();
        if (string.IsNullOrWhiteSpace(reference)) return Error.Validation("ReferenceText", "Choose a sentence to say.");
        if (reference.Length > 300) return Error.Validation("ReferenceText", "Use a sentence of at most 300 characters.");

        if (audio.Length == 0 || audio.Length > MaxAudioBytes) return Error.Validation("Audio", "The recording is empty or larger than 10 MB.");
        var wav = WavAudio.ToSpeechFormat(audio);
        if (wav is null) return Error.Validation("Audio", "Send the recording as a WAV file (PCM).");
        var seconds = (wav.Length - 44) / 2.0 / WavAudio.TargetRate;
        if (seconds < 0.5) return Error.Validation("Audio", "The recording is too short.");
        if (seconds > MaxAudioLength.TotalSeconds) return Error.Validation("Audio", "Keep the recording under 60 seconds.");

        var learner = await LearnerAsync(userId, item?.LanguageId ?? languageId, ct);
        if (!learner.IsSuccess) return learner.Error!;
        var (language, _, _, timeZone) = learner.Value;

        var limit = await settings.GetIntAsync(SettingKeys.PracticeSpeechPerDay, 50, ct);
        var dayStart = PracticeRules.DayStartUtc(clock.GetUtcNow(), timeZone);
        var used = await db.PracticeAttempts.CountAsync(a => a.UserId == userId && a.Type == PracticeType.Speak && a.CreatedAt >= dayStart, ct);
        if (used >= limit)
            return Error.TooManyRequests("practice_speech_limit", $"You've done today's {limit} pronunciation checks. More tomorrow!");

        PronunciationResult r;
        try
        {
            r = await speech.AssessAsync(wav, Locale(language), reference, ct);
        }
        catch (LanguageServiceException ex)
        {
            return Error.Conflict(ex.Code, ex.Message);
        }

        var url = await storage.SaveAsync(new MemoryStream(wav), "practice-audio", ".wav", "audio/wav", ct);
        var score = PracticeRules.SpeakScore(r);
        var words = r.Words.Select(w => new WordScoreDto(w.Word, (int)Math.Round(w.Accuracy), w.ErrorType)).ToList();
        await RecordAsync(userId, language.Id, PracticeType.Speak, item?.Id, reference, r.Transcript, url,
            new { r.Accuracy, r.Fluency, r.Completeness, words }, score, r.IsSample, ct);

        return new SpeakResultDto(score, (int)Math.Round(r.Accuracy), (int)Math.Round(r.Fluency), (int)Math.Round(r.Completeness),
            r.Transcript, words, r.IsSample);
    }

    // ================================================================== listen (FR-L93)

    public async Task<Result<ListenResultDto>> CheckListeningAsync(Guid userId, int itemId, ListenCheckRequest request, CancellationToken ct = default)
    {
        var item = await PublishedAsync(itemId, PracticeType.Listen, ct);
        if (item is null) return Error.NotFound("Dictation");
        var typed = request.Text?.Trim() ?? "";
        if (typed.Length > 1000) return Error.Validation("Text", "That's longer than the sentence.");

        var diff = Dictation.Compare(item.Text, typed);
        await RecordAsync(userId, item.LanguageId, PracticeType.Listen, item.Id, item.Text, typed, null, diff.Words, diff.Score, false, ct);
        return new ListenResultDto(diff.Score, item.Text, diff.Words);
    }

    // ================================================================== free talk (FR-L92)

    public async Task<Result<ConversationReplyDto>> ConverseAsync(Guid userId, ConversationRequest request, CancellationToken ct = default)
    {
        var message = request.Message?.Trim() ?? "";
        if (message.Length == 0) return Error.Validation("Message", "Type or say something first.");
        if (message.Length > MaxMessageLength) return Error.Validation("Message", $"Keep messages under {MaxMessageLength} characters.");
        var topic = string.IsNullOrWhiteSpace(request.Topic) ? Topics[0] : Clip(request.Topic, 100);

        var learner = await LearnerAsync(userId, request.LanguageId, ct);
        if (!learner.IsSuccess) return learner.Error!;
        var (language, level, native, timeZone) = learner.Value;

        var remaining = await AiCallsLeftAsync(userId, timeZone, ct);
        if (remaining <= 0) return AiLimitError();

        // The app keeps the chat; only the last few turns go to the model to keep cost down.
        var turns = await settings.GetIntAsync(SettingKeys.PracticeConversationTurns, 12, ct);
        var history = new StringBuilder();
        foreach (var turn in (request.History ?? []).TakeLast(Math.Max(0, turns)))
            history.Append(turn.Role.Equals("learner", StringComparison.OrdinalIgnoreCase) ? "Learner: " : "Partner: ")
                .AppendLine(Clip(turn.Text, MaxMessageLength).Replace("<<<", "").Replace(">>>", ""));
        if (history.Length == 0) history.Append("(new conversation)");

        var outcome = await ai.ConverseAsync(language.Name, level, topic, history.ToString(), message.Replace("<<<", "").Replace(">>>", ""),
            native, new AiCallContext(userId, null), ct);
        if (!outcome.IsSuccess) return AiUnavailable(outcome.Error!);

        var reply = outcome.Value.Value;
        // Models sometimes "correct" a sentence by repeating it; only show real changes (capital letters count).
        var correction = string.IsNullOrWhiteSpace(reply.Correction) ||
                         string.Equals(reply.Correction.Trim().TrimEnd('.', '!', '?'), message.TrimEnd('.', '!', '?'), StringComparison.Ordinal)
            ? null : reply.Correction.Trim();
        var sample = outcome.Value.Model == AiProviders.OfflineModel;
        await RecordAsync(userId, language.Id, PracticeType.Conversation, null, topic, message, null,
            new { reply = reply.Reply, correction, reply.Explanation }, null, sample, ct);
        return new ConversationReplyDto(reply.Reply.Trim(), correction, correction is null ? null : reply.Explanation, remaining - 1, sample);
    }

    public async Task<Result<IReadOnlyList<PracticeHistoryDto>>> HistoryAsync(Guid userId, PracticeType? type, CancellationToken ct = default)
    {
        var query = db.PracticeAttempts.AsNoTracking().Where(a => a.UserId == userId);
        if (type is { } t) query = query.Where(a => a.Type == t);
        var rows = await query.OrderByDescending(a => a.Id).Take(50)
            .Select(a => new { a.Id, a.Type, Title = a.PracticeItem != null ? a.PracticeItem.Title : a.PromptText, a.Score, a.CreatedAt })
            .ToListAsync(ct);
        return rows.Select(r => new PracticeHistoryDto(r.Id, r.Type, Clip(r.Title ?? r.Type.ToString(), 80), r.Score, r.CreatedAt)).ToList();
    }

    // ================================================================== helpers

    private async Task<Result<Learner>> LearnerAsync(Guid userId, int? languageId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.TimeZone, Native = u.NativeLanguage != null ? u.NativeLanguage.Name : null })
            .FirstOrDefaultAsync(ct);
        if (user is null) return Error.NotFound("Account");

        var courses = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.UserId == userId && c.Status != LearnerCourseStatus.Cancelled)
            .OrderByDescending(c => c.Status == LearnerCourseStatus.Active).ThenByDescending(c => c.CreatedAt)
            .Select(c => new { c.LanguageId, c.CurrentLevel })
            .ToListAsync(ct);

        var id = languageId ?? courses.Select(c => (int?)c.LanguageId).FirstOrDefault();
        if (id is null) return Error.Conflict("no_language", "Pick a language to practise first.");

        var language = await db.Languages.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id && l.Status == LanguageStatus.Published, ct);
        if (language is null) return Error.NotFound("Language");

        var level = courses.Where(c => c.LanguageId == language.Id).Select(c => (CefrLevel?)c.CurrentLevel).FirstOrDefault() ?? language.MinLevel;
        return new Learner(language, level, user.Native ?? "English", user.TimeZone);
    }

    private Task<PracticeItem?> PublishedAsync(int id, PracticeType type, CancellationToken ct) =>
        db.PracticeItems.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Type == type && p.Status == ContentStatus.Published, ct);

    private async Task<int> AiCallsLeftAsync(Guid userId, string timeZone, CancellationToken ct)
    {
        var limit = await settings.GetIntAsync(SettingKeys.PracticeAiCallsPerDay, 30, ct);
        var dayStart = PracticeRules.DayStartUtc(clock.GetUtcNow(), timeZone);
        var used = await db.PracticeAttempts.CountAsync(a => a.UserId == userId && a.CreatedAt >= dayStart &&
                                                             (a.Type == PracticeType.Write || a.Type == PracticeType.Conversation), ct);
        return limit - used;
    }

    private static Error AiLimitError() =>
        Error.TooManyRequests("practice_ai_limit", "You've used today's AI tutor messages and checks. More tomorrow!");

    /// <summary>Learners don't need provider or budget details; admins see those in AI usage.</summary>
    private static Error AiUnavailable(Error error) =>
        error.Code.StartsWith("ai_", StringComparison.Ordinal)
            ? Error.Conflict("ai_unavailable", "The AI tutor isn't available right now. Please try again later.")
            : error;

    private async Task RecordAsync(Guid userId, int languageId, PracticeType type, int? itemId, string? prompt, string? input, string? audioUrl,
        object? feedback, int? score, bool sample, CancellationToken ct)
    {
        var attempt = new PracticeAttempt
        {
            UserId = userId, LanguageId = languageId, Type = type, PracticeItemId = itemId, PromptText = prompt is null ? null : Clip(prompt, 6000),
            LearnerInput = input is null ? null : Clip(input, 6000), AudioUrl = audioUrl, Score = score, IsSample = sample,
            FeedbackJson = feedback is null ? null : Clip(JsonSerializer.Serialize(feedback, Json), 16000), CreatedAt = clock.GetUtcNow()
        };
        db.PracticeAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        // FR-S03: 5 points per practice exercise, a few times a day (PointRules.DailyCap); chat messages count as one exercise each.
        await progress.OnPracticeAsync(userId, languageId, attempt.Id, ct);
    }

    internal static PracticeItemDto ToDto(PracticeItem item, Language language) => new(
        item.Id, item.Type, language.Id, language.Name, item.Level, item.Title, item.Text, item.Translation,
        item.Type == PracticeType.Read ? ReadingQuestions.ForLearner(item.QuestionsJson) : [],
        Locale(language), language.IsoCode, language.Direction);

    internal static string Locale(Language language) =>
        !string.IsNullOrWhiteSpace(language.SttLocale) ? language.SttLocale! : language.IsoCode;

    private static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];
}
