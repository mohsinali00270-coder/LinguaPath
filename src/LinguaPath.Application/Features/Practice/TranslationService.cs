using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Translation;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Practice;

/// <summary>Online translation for learners (FR-L100 .. FR-L104, BR-15).</summary>
public interface ITranslationService
{
    Task<Result<TranslationDto>> TranslateAsync(Guid userId, TranslateRequest request, CancellationToken ct = default);

    /// <summary>Voice input (FR-L101): speech to text in <paramref name="from"/>, then translation.</summary>
    Task<Result<SpeechTranslationDto>> TranslateSpeechAsync(Guid userId, string from, string to, byte[] audio, CancellationToken ct = default);

    Task<Result<IReadOnlyList<TranslationDto>>> HistoryAsync(Guid userId, CancellationToken ct = default);
    Task<Result> ClearHistoryAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<TranslationLanguageDto>> LanguagesAsync(CancellationToken ct = default);
}

internal sealed class TranslationService(
    IApplicationDbContext db,
    ITranslator translator,
    ISpeechAssessor speech,
    ISettingsService settings,
    TimeProvider clock) : ITranslationService
{
    /// <summary>Marks a history row the learner cleared today.</summary>
    private const string Cleared = "[cleared]";

    public async Task<Result<TranslationDto>> TranslateAsync(Guid userId, TranslateRequest request, CancellationToken ct = default)
    {
        var text = PracticeRules.CleanText(request.Text ?? "");
        if (text.Length == 0) return Error.Validation("Text", "Type something to translate.");
        var max = await settings.GetIntAsync(SettingKeys.TranslationMaxCharacters, 500, ct);
        if (text.Length > max) return Error.Validation("Text", $"Translate at most {max} characters at a time.");
        if (!ValidCode(request.To)) return Error.Validation("To", "Choose the language to translate into.");
        if (!string.IsNullOrWhiteSpace(request.From) && !ValidCode(request.From)) return Error.Validation("From", "Unknown source language.");

        var limit = await LimitAsync(userId, ct);
        if (!limit.IsSuccess) return limit.Error!;
        return await TranslateCoreAsync(userId, text, request.From, request.To, limit.Value, ct);
    }

    public async Task<Result<SpeechTranslationDto>> TranslateSpeechAsync(Guid userId, string from, string to, byte[] audio,
        CancellationToken ct = default)
    {
        if (!ValidCode(from)) return Error.Validation("From", "Choose the language you will speak.");
        if (!ValidCode(to)) return Error.Validation("To", "Choose the language to translate into.");
        if (audio.Length == 0 || audio.Length > PracticeService.MaxAudioBytes) return Error.Validation("Audio", "The recording is empty or too large.");
        var wav = WavAudio.ToSpeechFormat(audio);
        if (wav is null) return Error.Validation("Audio", "Send the recording as a WAV file (PCM).");
        if ((wav.Length - 44) / 2.0 / WavAudio.TargetRate > PracticeService.MaxAudioLength.TotalSeconds)
            return Error.Validation("Audio", "Keep the recording under 60 seconds.");

        var limit = await LimitAsync(userId, ct);
        if (!limit.IsSuccess) return limit.Error!;

        SpeechTranscript transcript;
        try
        {
            transcript = await speech.RecognizeAsync(wav, await LocaleForAsync(from, ct), ct);
        }
        catch (LanguageServiceException ex)
        {
            return Error.Conflict(ex.Code, ex.Message);
        }
        var text = PracticeRules.CleanText(transcript.Text);
        if (text.Length == 0) return Error.Validation("Audio", "We couldn't hear any words. Try again a little closer to the microphone.");

        var translation = await TranslateCoreAsync(userId, text, from, to, limit.Value, ct);
        if (!translation.IsSuccess) return translation.Error!;
        return new SpeechTranslationDto(text, translation.Value);
    }

    public async Task<Result<IReadOnlyList<TranslationDto>>> HistoryAsync(Guid userId, CancellationToken ct = default)
    {
        var size = await settings.GetIntAsync(SettingKeys.TranslationHistorySize, 100, ct);
        var rows = await db.TranslationEntries.AsNoTracking().Where(t => t.UserId == userId && t.SourceText != Cleared)
            .OrderByDescending(t => t.Id).Take(Math.Clamp(size, 1, 500)).ToListAsync(ct);
        return rows.Select(t => ToDto(t, -1)).ToList();
    }

    public async Task<Result> ClearHistoryAsync(Guid userId, CancellationToken ct = default)
    {
        // Today's rows still count toward BR-15, so they are blanked rather than deleted.
        var rows = await db.TranslationEntries.Where(t => t.UserId == userId).ToListAsync(ct);
        var dayStart = PracticeRules.DayStartUtc(clock.GetUtcNow(), await TimeZoneAsync(userId, ct));
        foreach (var row in rows)
        {
            if (row.CreatedAt >= dayStart)
            {
                row.SourceText = Cleared;
                row.TranslatedText = Cleared;
                row.IsSample = true;
            }
            else db.TranslationEntries.Remove(row);
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<TranslationLanguageDto>> LanguagesAsync(CancellationToken ct = default)
    {
        var languages = await db.Languages.AsNoTracking().Where(l => l.Status != LanguageStatus.Disabled && l.IsoCode != "")
            .OrderBy(l => l.Name).Select(l => new TranslationLanguageDto(l.IsoCode, l.Name, l.SttLocale)).ToListAsync(ct);
        if (!languages.Any(l => l.Code.Equals("en", StringComparison.OrdinalIgnoreCase)))
            languages.Insert(0, new TranslationLanguageDto("en", "English", "en-US"));
        return languages.GroupBy(l => l.Code.ToLowerInvariant()).Select(g => g.First()).ToList();
    }

    // ------------------------------------------------------------------ core

    private async Task<Result<TranslationDto>> TranslateCoreAsync(Guid userId, string text, string? from, string to, int remaining,
        CancellationToken ct)
    {
        var target = PracticeRules.LanguageCode(to);
        var source = string.IsNullOrWhiteSpace(from) ? null : PracticeRules.LanguageCode(from);
        var now = clock.GetUtcNow();

        string translated, detectedFrom;
        bool detected = source is null, cached = false, sample = false;

        if (source is not null && source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            translated = text;
            detectedFrom = source;
        }
        else
        {
            // FR-L104: the same phrase is translated once and served from the cache afterwards.
            var key = PracticeRules.CacheKey(source ?? "auto", target, text);
            var hit = await db.TranslationCache.FirstOrDefaultAsync(c => c.Hash == key, ct);
            if (hit is not null)
            {
                hit.Hits++;
                translated = hit.TranslatedText;
                detectedFrom = hit.From;
                cached = true;
            }
            else
            {
                TranslatorResult result;
                try
                {
                    result = await translator.TranslateAsync(text, source, target, ct);
                }
                catch (LanguageServiceException ex)
                {
                    return Error.Conflict(ex.Code, ex.Message);
                }
                translated = result.Text;
                detectedFrom = string.IsNullOrWhiteSpace(result.From) ? source ?? "?" : PracticeRules.LanguageCode(result.From);
                sample = result.IsSample;
                if (!sample) await CacheAsync(key, detectedFrom, target, text, translated, result.Provider, now, ct);
            }
        }

        var entry = new TranslationEntry
        {
            UserId = userId, From = Clip(detectedFrom, 12), To = Clip(target, 12), SourceText = Clip(text, 2000),
            TranslatedText = Clip(translated, 4000), Detected = detected, Cached = cached, IsSample = sample, CreatedAt = now
        };
        db.TranslationEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        await TrimHistoryAsync(userId, ct);
        return ToDto(entry, remaining - 1);
    }

    private async Task CacheAsync(string key, string from, string to, string text, string translated, string provider, DateTimeOffset now,
        CancellationToken ct)
    {
        var entry = new TranslationCacheEntry
        {
            Hash = key, From = Clip(from, 12), To = Clip(to, 12), SourceText = Clip(text, 2000), TranslatedText = Clip(translated, 4000),
            Provider = Clip(provider, 50), CreatedAt = now
        };
        db.TranslationCache.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request cached the same phrase a moment ago; theirs is as good as ours.
            db.TranslationCache.Remove(entry);
        }
    }

    /// <summary>FR-L103: keep the last N (at least the daily limit, so today's count stays right).</summary>
    private async Task TrimHistoryAsync(Guid userId, CancellationToken ct)
    {
        var keep = Math.Max(await settings.GetIntAsync(SettingKeys.TranslationHistorySize, 100, ct),
            await settings.GetIntAsync(SettingKeys.DailyTranslationLimit, 100, ct));
        var old = await db.TranslationEntries.Where(t => t.UserId == userId).OrderByDescending(t => t.Id).Skip(keep).ToListAsync(ct);
        if (old.Count == 0) return;
        db.TranslationEntries.RemoveRange(old);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>BR-15: translations per learner per day, counted from the learner's local midnight.</summary>
    private async Task<Result<int>> LimitAsync(Guid userId, CancellationToken ct)
    {
        var limit = await settings.GetIntAsync(SettingKeys.DailyTranslationLimit, 100, ct);
        var dayStart = PracticeRules.DayStartUtc(clock.GetUtcNow(), await TimeZoneAsync(userId, ct));
        var used = await db.TranslationEntries.CountAsync(t => t.UserId == userId && t.CreatedAt >= dayStart, ct);
        if (used >= limit)
            return Error.TooManyRequests("translation_limit", $"You've used today's {limit} translations. You can translate again tomorrow.");
        return limit - used;
    }

    private async Task<string> TimeZoneAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";

    private async Task<string> LocaleForAsync(string code, CancellationToken ct)
    {
        if (code.Contains('-')) return code;
        var locale = await db.Languages.AsNoTracking().Where(l => l.IsoCode == code && l.SttLocale != null)
            .Select(l => l.SttLocale).FirstOrDefaultAsync(ct);
        return locale ?? code;
    }

    private static bool ValidCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && code.Length <= 12 && code.All(c => char.IsAsciiLetter(c) || c == '-' || c == '_');

    private static TranslationDto ToDto(TranslationEntry t, int remaining) =>
        new(t.Id, t.SourceText, t.TranslatedText, t.From, t.To, t.Detected, t.Cached, remaining, t.IsSample, t.CreatedAt);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
