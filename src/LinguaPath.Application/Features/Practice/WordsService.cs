using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Words;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Practice;

/// <summary>"My words": saved from lectures and translations, reviewed as SM-2 flashcards (FR-L94, FR-L103).</summary>
public interface IWordsService
{
    Task<Result<IReadOnlyList<UserWordDto>>> ListAsync(Guid userId, int? languageId, bool dueOnly, string? search, CancellationToken ct = default);
    Task<Result<WordsSummaryDto>> SummaryAsync(Guid userId, int? languageId, CancellationToken ct = default);

    /// <summary>Adds a word, or updates the meaning of one already saved (same language and spelling).</summary>
    Task<Result<UserWordDto>> SaveAsync(Guid userId, SaveWordRequest request, CancellationToken ct = default);

    Task<Result<UserWordDto>> ReviewAsync(Guid userId, int id, ReviewWordRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid userId, int id, CancellationToken ct = default);
}

internal sealed class WordsService(IApplicationDbContext db, TimeProvider clock) : IWordsService
{
    public const int MaxWords = 5000;

    public async Task<Result<IReadOnlyList<UserWordDto>>> ListAsync(Guid userId, int? languageId, bool dueOnly, string? search,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var query = db.UserWords.AsNoTracking().Where(w => w.UserId == userId);
        if (languageId is { } l) query = query.Where(w => w.LanguageId == l);
        if (dueOnly) query = query.Where(w => w.NextReviewAt <= now);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(w => w.Word.Contains(s) || w.Meaning.Contains(s));
        }
        // Due cards in the order they fell due; the full list newest first.
        query = dueOnly ? query.OrderBy(w => w.NextReviewAt).ThenBy(w => w.Id) : query.OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id);
        var rows = await query.Take(dueOnly ? 50 : 500)
            .Select(w => new { Word = w, Language = w.Language.Name }).ToListAsync(ct);
        return rows.Select(r => ToDto(r.Word, r.Language, now)).ToList();
    }

    public async Task<Result<WordsSummaryDto>> SummaryAsync(Guid userId, int? languageId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var query = db.UserWords.AsNoTracking().Where(w => w.UserId == userId);
        if (languageId is { } l) query = query.Where(w => w.LanguageId == l);
        var total = await query.CountAsync(ct);
        var due = await query.CountAsync(w => w.NextReviewAt <= now, ct);
        var weekAgo = now.AddDays(-7);
        // "Learned" = remembered at least twice in a row, last seen this week.
        var learned = await query.CountAsync(w => w.Repetitions >= 2 && w.LastReviewedAt >= weekAgo, ct);
        return new WordsSummaryDto(total, due, learned);
    }

    public async Task<Result<UserWordDto>> SaveAsync(Guid userId, SaveWordRequest request, CancellationToken ct = default)
    {
        var word = request.Word?.Trim() ?? "";
        var meaning = request.Meaning?.Trim() ?? "";
        if (word.Length == 0 || word.Length > 200) return Error.Validation("Word", "Enter a word or phrase of at most 200 characters.");
        if (meaning.Length == 0 || meaning.Length > 500) return Error.Validation("Meaning", "Enter a meaning of at most 500 characters.");
        var example = string.IsNullOrWhiteSpace(request.Example) ? null : request.Example.Trim();
        if (example is { Length: > 500 }) example = example[..500];

        var language = await db.Languages.AsNoTracking().Where(l => l.Id == request.LanguageId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        if (language is null) return Error.NotFound("Language");

        var now = clock.GetUtcNow();
        var existing = await db.UserWords.FirstOrDefaultAsync(w => w.UserId == userId && w.LanguageId == request.LanguageId && w.Word == word, ct);
        if (existing is not null)
        {
            existing.Meaning = meaning;
            existing.Example = example ?? existing.Example;
            await db.SaveChangesAsync(ct);
            return ToDto(existing, language, now);
        }

        if (await db.UserWords.CountAsync(w => w.UserId == userId, ct) >= MaxWords)
            return Error.Conflict("words_full", $"Your list already has {MaxWords} words. Delete some you know well to add more.");

        var entity = new UserWord
        {
            UserId = userId, LanguageId = request.LanguageId, Word = word, Meaning = meaning, Example = example,
            CreatedAt = now, NextReviewAt = now
        };
        db.UserWords.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity, language, now);
    }

    public async Task<Result<UserWordDto>> ReviewAsync(Guid userId, int id, ReviewWordRequest request, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Grade)) return Error.Validation("Grade", "Choose Again, Hard, Good or Easy.");
        var word = await db.UserWords.Include(w => w.Language).FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        if (word is null) return Error.NotFound("Word");
        var now = clock.GetUtcNow();
        Sm2.Review(word, request.Grade, now);
        await db.SaveChangesAsync(ct);
        return ToDto(word, word.Language.Name, now);
    }

    public async Task<Result> DeleteAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var word = await db.UserWords.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        if (word is null) return Error.NotFound("Word");
        db.UserWords.Remove(word);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static UserWordDto ToDto(UserWord w, string language, DateTimeOffset now) =>
        new(w.Id, w.LanguageId, language, w.Word, w.Meaning, w.Example, w.Repetitions, w.IntervalDays, w.EaseFactor, w.NextReviewAt,
            w.NextReviewAt <= now);
}
