using System.Globalization;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Moderation;

// ===================================================================== admin models

public sealed record WordSheetRow(int RowNumber, string Term, string Severity, string? Language);

public interface IWordListSpreadsheet
{
    byte[] Export(IEnumerable<WordSheetRow> rows);
    IReadOnlyList<WordSheetRow> Read(Stream stream);
}

public sealed record ModerationQueueFilter(bool OpenOnly = true, ModerationDetector? DetectedBy = null, string? Search = null, int Page = 1);

public sealed record ModerationQueueItem(
    long Id, Guid UserId, string UserName, string Email, ModerationDetector DetectedBy, string? Category, ModerationSeverity Severity,
    double? Score, string? Text, string? MatchedTerms, ModerationOutcome ActionTaken, DateTimeOffset CreatedAt, int ActiveStrikes, bool UserBlocked,
    int Reports);

public sealed record ContextMessage(long Id, string SenderName, bool IsReportedUser, string? Text, bool IsActive, DateTimeOffset SentAt, bool IsFlagged);

public sealed record StrikeItem(int Id, ModerationSeverity Severity, string Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcknowledgedAt, string? RemovedBy, string? RemovedReason, bool IsActive);

public sealed record ReportItem(int Id, string ReporterName, ReportReason Reason, string? Details, ReportStatus Status, DateTimeOffset CreatedAt);

public sealed record ModerationEventDetails(
    ModerationQueueItem Item, string? ConversationTitle, int? ConversationId, long? MessageId, bool MessageActive,
    IReadOnlyList<ContextMessage> Context, IReadOnlyList<StrikeItem> Strikes, IReadOnlyList<ReportItem> Reports,
    DateTimeOffset? SuspendedUntil, string? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote);

public sealed record LearnerModerationSummary(int ActiveStrikes, DateTimeOffset? SuspendedUntil, bool Blocked, string? BlockReason,
    IReadOnlyList<StrikeItem> Strikes, IReadOnlyList<ModerationQueueItem> RecentEvents, int ReportsAgainst);

public sealed record WordItem(int Id, string Term, ModerationSeverity Severity, int? LanguageId, string? Language, bool IsActive, DateTimeOffset CreatedAt);

public sealed record WordFilter(string? Search, ModerationSeverity? Severity, int? LanguageId, int Page = 1);

public sealed record LinkPatternItem(int Id, string Pattern, string? Description, bool IsException, bool IsActive, DateTimeOffset CreatedAt);

public sealed record WordImportResult(int Added, int Updated, IReadOnlyList<string> Errors);

public sealed record FilterTestResult(ModerationSeverity Severity, string MaskedText, IReadOnlyList<string> Words, IReadOnlyList<string> Contacts);

public sealed record DayCount(DateOnly Day, int Count);

public sealed record ModerationReport(
    int Days, IReadOnlyList<DayCount> StrikesPerDay, IReadOnlyList<(string Term, int Count)> TopWords, int BlockedAccounts, int BlockedInPeriod,
    int Reviewed, int Dismissed, double FalsePositivePercent, int OpenItems, IReadOnlyList<(ModerationDetector Detector, int Count)> ByDetector);

public interface IModerationAdminService
{
    Task<PagedResult<ModerationQueueItem>> QueueAsync(ModerationQueueFilter filter, CancellationToken ct = default);
    Task<int> OpenCountAsync(CancellationToken ct = default);
    Task<ModerationEventDetails?> EventAsync(long id, CancellationToken ct = default);
    /// <summary>FR-M22. "days" is for Suspend; "strikeId" for RemoveStrike.</summary>
    Task<Result> ActAsync(long eventId, ModeratorAction action, string? note, int? days, int? strikeId, CancellationToken ct = default);
    Task<Result> UnblockAsync(Guid userId, string reason, CancellationToken ct = default);
    Task<LearnerModerationSummary> LearnerAsync(Guid userId, CancellationToken ct = default);

    Task<PagedResult<WordItem>> WordsAsync(WordFilter filter, CancellationToken ct = default);
    Task<Result> SaveWordAsync(int? id, string term, ModerationSeverity severity, int? languageId, bool isActive, CancellationToken ct = default);
    Task<Result> DeleteWordAsync(int id, CancellationToken ct = default);
    Task<WordImportResult> ImportWordsAsync(Stream excel, CancellationToken ct = default);
    Task<byte[]> ExportWordsAsync(CancellationToken ct = default);
    Task<FilterTestResult> TestAsync(string text, int? languageId, CancellationToken ct = default);

    Task<IReadOnlyList<LinkPatternItem>> PatternsAsync(CancellationToken ct = default);
    Task<Result> SavePatternAsync(int? id, string pattern, string? description, bool isException, bool isActive, CancellationToken ct = default);
    Task<Result> DeletePatternAsync(int id, CancellationToken ct = default);

    Task<ModerationReport> ReportAsync(int days, CancellationToken ct = default);
}

internal sealed class ModerationAdminService(
    IApplicationDbContext db,
    IModerationService moderation,
    ContentFilterCache filters,
    IWordListSpreadsheet spreadsheet,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider clock) : IModerationAdminService
{
    private const int PageSize = 50;
    private string Me => currentUser.Email ?? "moderator";

    public async Task<PagedResult<ModerationQueueItem>> QueueAsync(ModerationQueueFilter filter, CancellationToken ct = default)
    {
        var q = db.ModerationEvents.AsNoTracking().AsQueryable();
        if (filter.OpenOnly) q = q.Where(e => e.ActionTaken == ModerationOutcome.Open);
        else q = q.Where(e => e.ActionTaken != ModerationOutcome.Logged);
        if (filter.DetectedBy is { } d) q = q.Where(e => e.DetectedBy == d);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            var users = db.Users.Where(u => u.Email!.Contains(s) || u.FullName.Contains(s) || (u.DisplayName != null && u.DisplayName.Contains(s))).Select(u => u.Id);
            q = q.Where(e => users.Contains(e.UserId) || (e.Text != null && e.Text.Contains(s)));
        }
        var page = Math.Max(1, filter.Page);
        var total = await q.CountAsync(ct);
        var ids = await q.OrderByDescending(e => e.Id).Skip((page - 1) * PageSize).Take(PageSize).Select(e => e.Id).ToListAsync(ct);
        return new PagedResult<ModerationQueueItem> { Items = await ItemsAsync(ids, ct), Page = page, PageSize = PageSize, TotalCount = total };
    }

    public Task<int> OpenCountAsync(CancellationToken ct = default) => db.ModerationEvents.CountAsync(e => e.ActionTaken == ModerationOutcome.Open, ct);

    public async Task<ModerationEventDetails?> EventAsync(long id, CancellationToken ct = default)
    {
        var ev = await db.ModerationEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return null;
        var item = (await ItemsAsync([id], ct))[0];

        long? messageId = long.TryParse(ev.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid) ? mid : null;
        var context = new List<ContextMessage>();
        string? title = null;
        var messageActive = false;
        if (ev.ConversationId is { } cid)
        {
            var c = await db.Conversations.AsNoTracking().Where(x => x.Id == cid).Select(x => new { x.Type, x.Name }).FirstOrDefaultAsync(ct);
            title = c is null ? null : c.Type == ConversationType.Group ? $"Group: {c.Name}" : "Direct chat";

            // FR-M21: five messages before and after (moderators see the original text of masked or removed messages).
            var anchor = messageId ?? await db.Messages.Where(m => m.ConversationId == cid && m.SentAt <= ev.CreatedAt)
                .MaxAsync(m => (long?)m.Id, ct) ?? 0;
            var before = await db.Messages.AsNoTracking().Where(m => m.ConversationId == cid && m.Id <= anchor).OrderByDescending(m => m.Id).Take(6)
                .Select(Ctx(ev.UserId, messageId)).ToListAsync(ct);
            var after = await db.Messages.AsNoTracking().Where(m => m.ConversationId == cid && m.Id > anchor).OrderBy(m => m.Id).Take(5)
                .Select(Ctx(ev.UserId, messageId)).ToListAsync(ct);
            context = before.OrderBy(m => m.Id).Concat(after).ToList();
            if (messageId is { } m0) messageActive = await db.Messages.AnyAsync(m => m.Id == m0 && m.IsActive, ct);
        }

        var strikes = await StrikesAsync(ev.UserId, ct);
        var reports = await db.UserReports.AsNoTracking()
            .Where(r => r.ModerationEventId == id || (messageId != null && r.MessageId == messageId))
            .Join(db.Users, r => r.ReporterId, u => u.Id, (r, u) => new ReportItem(r.Id, u.DisplayName ?? u.FullName, r.Reason, r.Details, r.Status, r.CreatedAt))
            .ToListAsync(ct);
        var suspended = await db.Users.Where(u => u.Id == ev.UserId).Select(u => u.ChatSuspendedUntil).FirstOrDefaultAsync(ct);
        return new ModerationEventDetails(item, title, ev.ConversationId, messageId, messageActive, context, strikes, reports,
            suspended > clock.GetUtcNow() ? suspended : null, ev.ReviewedByEmail, ev.ReviewedAt, ev.ReviewNote);
    }

    private static System.Linq.Expressions.Expression<Func<Message, ContextMessage>> Ctx(Guid userId, long? flagged) => m =>
        new ContextMessage(m.Id, m.Sender == null ? "System" : m.Sender.DisplayName ?? m.Sender.FullName, m.SenderId == userId,
            m.OriginalBody ?? m.Body, m.IsActive, m.SentAt, m.Id == flagged);

    public async Task<Result> ActAsync(long eventId, ModeratorAction action, string? note, int? days, int? strikeId, CancellationToken ct = default)
    {
        var ev = await db.ModerationEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return Error.NotFound("Moderation item");
        var reason = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        long? messageId = long.TryParse(ev.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid) ? mid : null;

        Result result;
        ModerationOutcome outcome;
        switch (action)
        {
            case ModeratorAction.Dismiss:
                result = Result.Success();
                outcome = ModerationOutcome.Dismissed;
                break;
            case ModeratorAction.AddStrike:
                await moderation.AddStrikeAsync(ev.UserId, ev.Severity == ModerationSeverity.Severe ? ModerationSeverity.Severe : ModerationSeverity.Abusive,
                    reason ?? $"Moderator decision ({ev.Category ?? ev.DetectedBy.ToString()})", ev.Id, Me, ct);
                result = Result.Success();
                outcome = ModerationOutcome.StrikeAdded;
                break;
            case ModeratorAction.RemoveStrike:
                if (strikeId is null) return Error.Validation("StrikeId", "Choose the strike to remove.");
                if (!await db.ModerationStrikes.AnyAsync(s => s.Id == strikeId && s.UserId == ev.UserId, ct)) return Error.NotFound("Strike");
                result = await moderation.RemoveStrikeAsync(strikeId.Value, reason ?? "", Me, ct);
                outcome = ev.ActionTaken == ModerationOutcome.Open ? ModerationOutcome.Dismissed : ev.ActionTaken;
                break;
            case ModeratorAction.Suspend:
                result = await moderation.SuspendAsync(ev.UserId, TimeSpan.FromDays(days ?? 0), reason ?? "", Me, ct);
                outcome = ModerationOutcome.Suspended;
                break;
            case ModeratorAction.Block:
                if (reason is null) return Error.Validation("Note", "A reason is required to block an account.");
                result = await moderation.BlockAsync(ev.UserId, reason, Me, ct);
                outcome = ModerationOutcome.Blocked;
                break;
            case ModeratorAction.RemoveMessage:
                if (messageId is null) return Error.Validation("Action", "This item isn't about a message.");
                result = await moderation.RemoveMessageAsync(messageId.Value, Me, ct);
                outcome = ModerationOutcome.Removed;
                break;
            default:
                return Error.Validation("Action", "Unknown action.");
        }
        if (result.IsFailure) return result;

        ev.ActionTaken = outcome;
        ev.ReviewedByEmail = Me;
        ev.ReviewedAt = clock.GetUtcNow();
        ev.ReviewNote = reason is { Length: > 1000 } ? reason[..1000] : reason;
        foreach (var r in await db.UserReports.Where(r => r.ModerationEventId == eventId && r.Status == ReportStatus.Open).ToListAsync(ct))
        {
            r.Status = action == ModeratorAction.Dismiss ? ReportStatus.Dismissed : ReportStatus.Resolved;
            r.ReviewedAt = ev.ReviewedAt;
            r.ReviewedByEmail = Me;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Moderate", "ModerationEvent", eventId.ToString(CultureInfo.InvariantCulture), null,
            new { action = action.ToString(), note = reason, days, strikeId }, ct);
        return Result.Success();
    }

    public Task<Result> UnblockAsync(Guid userId, string reason, CancellationToken ct = default) =>
        currentUser.IsInRole(Domain.Constants.Roles.SuperAdmin)
            ? moderation.UnblockAsync(userId, reason, Me, ct)
            : Task.FromResult<Result>(Error.Forbidden("super_admin_only", "Only a Super Admin can unblock an account."));

    public async Task<LearnerModerationSummary> LearnerAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.IsBlocked, u.BlockReason, u.ChatSuspendedUntil }).FirstOrDefaultAsync(ct);
        var strikes = await StrikesAsync(userId, ct);
        var recent = await db.ModerationEvents.AsNoTracking().Where(e => e.UserId == userId && e.ActionTaken != ModerationOutcome.Logged)
            .OrderByDescending(e => e.Id).Take(10).Select(e => e.Id).ToListAsync(ct);
        return new LearnerModerationSummary(strikes.Count(s => s.IsActive), user?.ChatSuspendedUntil > now ? user.ChatSuspendedUntil : null,
            user?.IsBlocked ?? false, user?.BlockReason, strikes, await ItemsAsync(recent, ct),
            await db.UserReports.CountAsync(r => r.ReportedUserId == userId, ct));
    }

    // ------------------------------------------------------------------ word list (FR-M24)

    public async Task<PagedResult<WordItem>> WordsAsync(WordFilter filter, CancellationToken ct = default)
    {
        var q = db.AbusiveWords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search)) q = q.Where(w => w.Term.Contains(filter.Search.Trim()));
        if (filter.Severity is { } s) q = q.Where(w => w.Severity == s);
        if (filter.LanguageId is { } l) q = l == 0 ? q.Where(w => w.LanguageId == null) : q.Where(w => w.LanguageId == l);
        var page = Math.Max(1, filter.Page);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(w => w.Term).Skip((page - 1) * 100).Take(100)
            .Select(w => new WordItem(w.Id, w.Term, w.Severity, w.LanguageId, w.Language == null ? null : w.Language.Name, w.IsActive, w.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<WordItem> { Items = items, Page = page, PageSize = 100, TotalCount = total };
    }

    public async Task<Result> SaveWordAsync(int? id, string term, ModerationSeverity severity, int? languageId, bool isActive, CancellationToken ct = default)
    {
        term = (term ?? "").Trim();
        if (term.Length is < 2 or > 200) return Error.Validation("Term", "Words are 2 to 200 characters.");
        if (severity is not (ModerationSeverity.Mild or ModerationSeverity.Abusive or ModerationSeverity.Severe))
            return Error.Validation("Severity", "Choose Mild, Abusive or Severe.");
        if (languageId is <= 0) languageId = null;
        var normalized = ContentFilter.NormalizeText(term);
        if (normalized.Length == 0) return Error.Validation("Term", "Use letters or numbers.");
        if (await db.AbusiveWords.AnyAsync(w => w.Id != (id ?? 0) && w.LanguageId == languageId && w.NormalizedTerm == normalized, ct))
            return Error.Validation("Term", "This word is already on the list for that language.");

        AbusiveWord word;
        if (id is { } wid)
        {
            word = await db.AbusiveWords.FirstOrDefaultAsync(w => w.Id == wid, ct) ?? throw new KeyNotFoundException();
        }
        else
        {
            word = new AbusiveWord { CreatedAt = clock.GetUtcNow() };
            db.AbusiveWords.Add(word);
        }
        var old = id is null ? null : new { word.Term, word.Severity, word.LanguageId, word.IsActive };
        word.Term = term;
        word.NormalizedTerm = normalized;
        word.Severity = severity;
        word.LanguageId = languageId;
        word.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        filters.Invalidate();
        await audit.LogAsync(id is null ? "Create" : "Update", "AbusiveWord", word.Id.ToString(CultureInfo.InvariantCulture), old,
            new { term, severity, languageId, isActive }, ct);
        return Result.Success();
    }

    public async Task<Result> DeleteWordAsync(int id, CancellationToken ct = default)
    {
        var word = await db.AbusiveWords.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (word is null) return Error.NotFound("Word");
        db.AbusiveWords.Remove(word);
        await db.SaveChangesAsync(ct);
        filters.Invalidate();
        await audit.LogAsync("Delete", "AbusiveWord", id.ToString(CultureInfo.InvariantCulture), new { word.Term, word.Severity }, null, ct);
        return Result.Success();
    }

    public async Task<WordImportResult> ImportWordsAsync(Stream excel, CancellationToken ct = default)
    {
        IReadOnlyList<WordSheetRow> rows;
        try
        {
            rows = spreadsheet.Read(excel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WordImportResult(0, 0, ["The file couldn't be read as an Excel workbook (.xlsx)."]);
        }

        var languages = await db.Languages.AsNoTracking().Select(l => new { l.Id, l.Name, l.IsoCode }).ToListAsync(ct);
        var existing = await db.AbusiveWords.ToListAsync(ct);
        int added = 0, updated = 0;
        var errors = new List<string>();
        foreach (var row in rows.Take(10_000))
        {
            if (!Enum.TryParse<ModerationSeverity>(row.Severity, true, out var severity) || severity == ModerationSeverity.None || !Enum.IsDefined(severity))
            {
                errors.Add($"Row {row.RowNumber}: severity \"{row.Severity}\" should be Mild, Abusive or Severe.");
                continue;
            }
            int? languageId = null;
            if (row.Language is { } lang)
            {
                var match = languages.FirstOrDefault(l => string.Equals(l.IsoCode, lang, StringComparison.OrdinalIgnoreCase)
                                                          || string.Equals(l.Name, lang, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    errors.Add($"Row {row.RowNumber}: unknown language \"{lang}\".");
                    continue;
                }
                languageId = match.Id;
            }
            var normalized = ContentFilter.NormalizeText(row.Term);
            if (normalized.Length < 2 || row.Term.Length > 200)
            {
                errors.Add($"Row {row.RowNumber}: \"{row.Term}\" is too short or too long.");
                continue;
            }
            var word = existing.FirstOrDefault(w => w.LanguageId == languageId && w.NormalizedTerm == normalized);
            if (word is null)
            {
                word = new AbusiveWord { Term = row.Term, NormalizedTerm = normalized, Severity = severity, LanguageId = languageId, CreatedAt = clock.GetUtcNow() };
                db.AbusiveWords.Add(word);
                existing.Add(word);
                added++;
            }
            else if (word.Severity != severity || !word.IsActive)
            {
                word.Severity = severity;
                word.IsActive = true;
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
        filters.Invalidate();
        await audit.LogAsync("Import", "AbusiveWord", null, null, new { added, updated, errors = errors.Count }, ct);
        return new WordImportResult(added, updated, errors);
    }

    public async Task<byte[]> ExportWordsAsync(CancellationToken ct = default)
    {
        var rows = await db.AbusiveWords.AsNoTracking().OrderBy(w => w.Term)
            .Select(w => new { w.Term, w.Severity, Language = w.Language == null ? null : w.Language.IsoCode }).ToListAsync(ct);
        return spreadsheet.Export(rows.Select((r, i) => new WordSheetRow(i + 2, r.Term, r.Severity.ToString(), r.Language)));
    }

    public async Task<FilterTestResult> TestAsync(string text, int? languageId, CancellationToken ct = default)
    {
        filters.Invalidate();
        var filter = await filters.GetAsync(db, clock.GetUtcNow(), ct);
        var r = filter.Check(text ?? "", languageId);
        return new FilterTestResult(r.Severity, r.MaskedText, r.Words.Select(w => $"{w.Term} ({w.Severity})").Distinct().ToList(),
            r.Contacts.Select(c => $"{c.Kind}: {c.Value}").ToList());
    }

    // ------------------------------------------------------------------ link rules (FR-M25)

    public async Task<IReadOnlyList<LinkPatternItem>> PatternsAsync(CancellationToken ct = default) =>
        await db.BlockedLinkPatterns.AsNoTracking().OrderBy(p => p.IsException).ThenBy(p => p.Pattern)
            .Select(p => new LinkPatternItem(p.Id, p.Pattern, p.Description, p.IsException, p.IsActive, p.CreatedAt)).ToListAsync(ct);

    public async Task<Result> SavePatternAsync(int? id, string pattern, string? description, bool isException, bool isActive, CancellationToken ct = default)
    {
        pattern = (pattern ?? "").Trim();
        if (pattern.Length is 0 or > 500) return Error.Validation("Pattern", "Enter a pattern of up to 500 characters.");
        if (ContentFilter.TryRegex(pattern) is null) return Error.Validation("Pattern", "This isn't a valid regular expression.");
        BlockedLinkPattern row;
        if (id is { } pid)
        {
            row = await db.BlockedLinkPatterns.FirstOrDefaultAsync(p => p.Id == pid, ct) ?? throw new KeyNotFoundException();
        }
        else
        {
            row = new BlockedLinkPattern { CreatedAt = clock.GetUtcNow() };
            db.BlockedLinkPatterns.Add(row);
        }
        row.Pattern = pattern;
        row.Description = description?.Trim();
        row.IsException = isException;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        filters.Invalidate();
        await audit.LogAsync(id is null ? "Create" : "Update", "BlockedLinkPattern", row.Id.ToString(CultureInfo.InvariantCulture), null,
            new { pattern, isException, isActive }, ct);
        return Result.Success();
    }

    public async Task<Result> DeletePatternAsync(int id, CancellationToken ct = default)
    {
        var row = await db.BlockedLinkPatterns.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return Error.NotFound("Pattern");
        db.BlockedLinkPatterns.Remove(row);
        await db.SaveChangesAsync(ct);
        filters.Invalidate();
        await audit.LogAsync("Delete", "BlockedLinkPattern", id.ToString(CultureInfo.InvariantCulture), new { row.Pattern }, null, ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ report (FR-M26)

    public async Task<ModerationReport> ReportAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 7, 365);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-days);

        var strikeDates = await db.ModerationStrikes.AsNoTracking().Where(s => s.CreatedAt >= since).Select(s => s.CreatedAt).ToListAsync(ct);
        var perDay = Enumerable.Range(0, days + 1).Select(i => DateOnly.FromDateTime(since.UtcDateTime.Date.AddDays(i)))
            .Select(d => new DayCount(d, strikeDates.Count(x => DateOnly.FromDateTime(x.UtcDateTime) == d))).ToList();

        var matched = await db.ModerationEvents.AsNoTracking()
            .Where(e => e.CreatedAt >= since && e.DetectedBy == ModerationDetector.WordFilter && e.MatchedTerms != null)
            .Select(e => e.MatchedTerms!).ToListAsync(ct);
        var top = matched.SelectMany(m => m.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(t => t.ToLowerInvariant()).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).Take(15).ToList();

        var reviewedRows = await db.ModerationEvents.AsNoTracking()
            .Where(e => e.CreatedAt >= since && e.ReviewedAt != null && e.DetectedBy != ModerationDetector.Moderator)
            .Select(e => e.ActionTaken).ToListAsync(ct);
        var dismissed = reviewedRows.Count(a => a == ModerationOutcome.Dismissed);
        var byDetector = await db.ModerationEvents.AsNoTracking().Where(e => e.CreatedAt >= since && e.ActionTaken != ModerationOutcome.Logged)
            .GroupBy(e => e.DetectedBy).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        return new ModerationReport(days, perDay, top,
            await db.Users.CountAsync(u => u.IsBlocked, ct),
            await db.Users.CountAsync(u => u.IsBlocked && u.BlockedAt >= since, ct),
            reviewedRows.Count, dismissed, reviewedRows.Count == 0 ? 0 : Math.Round(100.0 * dismissed / reviewedRows.Count, 1),
            await OpenCountAsync(ct), byDetector.Select(b => (b.Key, b.Count)).OrderByDescending(b => b.Count).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<IReadOnlyList<ModerationQueueItem>> ItemsAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var now = clock.GetUtcNow();
        var rows = await db.ModerationEvents.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Join(db.Users, e => e.UserId, u => u.Id, (e, u) => new
            {
                e, Name = u.DisplayName ?? u.FullName, Email = u.Email ?? "", u.IsBlocked,
                Strikes = db.ModerationStrikes.Count(s => s.UserId == u.Id && s.RemovedAt == null && s.ExpiresAt > now),
                Reports = db.UserReports.Count(r => r.ModerationEventId == e.Id)
            })
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.e.Id).Select(r => new ModerationQueueItem(r.e.Id, r.e.UserId, r.Name, r.Email, r.e.DetectedBy, r.e.Category,
            r.e.Severity, r.e.Score, r.e.Text, r.e.MatchedTerms, r.e.ActionTaken, r.e.CreatedAt, r.Strikes, r.IsBlocked, r.Reports)).ToList();
    }

    private async Task<IReadOnlyList<StrikeItem>> StrikesAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.ModerationStrikes.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.CreatedAt).Take(50)
            .Select(s => new StrikeItem(s.Id, s.Severity, s.Reason, s.CreatedAt, s.ExpiresAt, s.AcknowledgedAt, s.RemovedBy, s.RemovedReason,
                s.RemovedAt == null && s.ExpiresAt > now))
            .ToListAsync(ct);
    }
}
