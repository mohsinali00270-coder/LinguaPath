using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinguaPath.Application.Features.Moderation;

// ===================================================================== ports implemented by the hosts

/// <summary>Real-time chat events. The API implements it with SignalR; hosts without the hub use the no-op.</summary>
public interface IChatNotifier
{
    Task MessageAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default);
    Task MessageDeletedAsync(IReadOnlyCollection<Guid> userIds, MessageDeletedDto deleted, CancellationToken ct = default);
    Task ConversationChangedAsync(IReadOnlyCollection<Guid> userIds, ConversationChangedDto change, CancellationToken ct = default);
    Task SeenAsync(IReadOnlyCollection<Guid> userIds, SeenDto seen, CancellationToken ct = default);
    Task ModerationWarningAsync(Guid userId, ModerationWarningDto warning, CancellationToken ct = default);
    /// <summary>Closes the learner's live connections (permanent block).</summary>
    Task DisconnectAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Who has a live chat connection (FR-C04). In-memory per API instance.</summary>
public interface IPresenceTracker
{
    bool IsOnline(Guid userId);
    DateTimeOffset? LastSeen(Guid userId);
}

/// <summary>Drops the API's cached "can this user still sign in" state so a block takes effect on the next request.</summary>
public interface IUserSessionCache
{
    void Invalidate(Guid userId);
}

/// <summary>Messages waiting for the AI scan (FR-M03). The API runs the worker.</summary>
public interface IModerationQueue
{
    void Enqueue(long messageId);
}

public sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>Push notifications (FCM HTTP v1, or a log line when not configured).</summary>
public interface IPushSender
{
    Task SendAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default);
}

public sealed class NullChatNotifier : IChatNotifier, IPresenceTracker, IUserSessionCache, IModerationQueue
{
    public Task MessageAsync(IReadOnlyCollection<Guid> userIds, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageDeletedAsync(IReadOnlyCollection<Guid> userIds, MessageDeletedDto deleted, CancellationToken ct = default) => Task.CompletedTask;
    public Task ConversationChangedAsync(IReadOnlyCollection<Guid> userIds, ConversationChangedDto change, CancellationToken ct = default) => Task.CompletedTask;
    public Task SeenAsync(IReadOnlyCollection<Guid> userIds, SeenDto seen, CancellationToken ct = default) => Task.CompletedTask;
    public Task ModerationWarningAsync(Guid userId, ModerationWarningDto warning, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
    public bool IsOnline(Guid userId) => false;
    public DateTimeOffset? LastSeen(Guid userId) => null;
    public void Invalidate(Guid userId) { }
    public void Enqueue(long messageId) { }
}

/// <summary>Caches the compiled word list and link patterns; reloads every minute or when an admin edits them in this process.</summary>
public sealed class ContentFilterCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ContentFilter? filter;
    private DateTimeOffset loadedAt;

    public void Invalidate() => filter = null;

    public async Task<ContentFilter> GetAsync(IApplicationDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var current = filter;
        if (current is not null && now - loadedAt < TimeSpan.FromMinutes(1)) return current;
        await gate.WaitAsync(ct);
        try
        {
            if (filter is not null && now - loadedAt < TimeSpan.FromMinutes(1)) return filter;
            var words = await db.AbusiveWords.AsNoTracking().Where(w => w.IsActive)
                .Select(w => new FilterWord(w.Term, w.Severity, w.LanguageId)).ToListAsync(ct);
            var patterns = await db.BlockedLinkPatterns.AsNoTracking().Where(p => p.IsActive)
                .Select(p => new FilterPattern(p.Pattern, p.IsException)).ToListAsync(ct);
            filter = new ContentFilter(words, patterns);
            loadedAt = now;
            return filter;
        }
        finally
        {
            gate.Release();
        }
    }
}

// ===================================================================== the service

/// <summary>What happened to an outgoing message.</summary>
public sealed record ModerationDecision(bool Allowed, string Text, string? OriginalText, MessageModeration Status, Error? Error, ModerationNoticeDto? Notice)
{
    public static ModerationDecision Reject(Error error, ModerationNoticeDto? notice = null) => new(false, "", null, MessageModeration.Pending, error, notice);
}

public sealed record StrikeOutcome(LadderStep Step, int ActiveStrikes, DateTimeOffset? SuspendedUntil);

public sealed record ModerationStatusDto(int ActiveStrikes, DateTimeOffset? SuspendedUntil, bool Blocked, ModerationNoticeDto? PendingWarning);

public interface IModerationService
{
    /// <summary>Blocks, suspensions and unacknowledged warnings that stop the learner chatting (FR-M12).</summary>
    Task<ModerationNoticeDto?> ChatRestrictionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>FR-M01 .. FR-M11: runs the link/contact filter, the word filter and the spam rules on an outgoing message.</summary>
    Task<ModerationDecision> CheckMessageAsync(Guid userId, int conversationId, string text, int? languageId, CancellationToken ct = default);

    /// <summary>Group names and descriptions, display names and bios: links and abusive words are refused (no masking).</summary>
    Task<Result> CheckTextAsync(Guid userId, ModerationTarget target, string? text, CancellationToken ct = default);

    Task<ModerationStatusDto> StatusAsync(Guid userId, CancellationToken ct = default);
    Task<Result> AcknowledgeAsync(Guid userId, CancellationToken ct = default);

    Task<StrikeOutcome> AddStrikeAsync(Guid userId, ModerationSeverity severity, string reason, long? eventId, string? by, CancellationToken ct = default);
    Task<Result> RemoveStrikeAsync(int strikeId, string reason, string by, CancellationToken ct = default);
    Task<Result> SuspendAsync(Guid userId, TimeSpan duration, string reason, string by, CancellationToken ct = default);
    Task<Result> BlockAsync(Guid userId, string reason, string? by, CancellationToken ct = default);
    Task<Result> UnblockAsync(Guid userId, string reason, string by, CancellationToken ct = default);

    /// <summary>Hides a message for everyone (moderation) and tells the chat.</summary>
    Task<Result> RemoveMessageAsync(long messageId, string by, CancellationToken ct = default);

    /// <summary>FR-M03: AI scan of a delivered message. Returns false when there was nothing to do.</summary>
    Task<bool> ScanAsync(long messageId, CancellationToken ct = default);

    /// <summary>Messages the queue missed (restart, AI outage): ids of pending messages older than a minute.</summary>
    Task<IReadOnlyList<long>> PendingScansAsync(int take, CancellationToken ct = default);
}

internal sealed class ModerationService(
    IApplicationDbContext db,
    ISettingsService settings,
    IAiContentService ai,
    ContentFilterCache filters,
    IChatNotifier notifier,
    IUserSessionCache sessions,
    IAuditLogger audit,
    Progress.IPointsService points,
    TimeProvider clock,
    ILogger<ModerationService> logger) : IModerationService
{
    public async Task<ModerationNoticeDto?> ChatRestrictionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.IsBlocked, u.IsActive, u.ChatSuspendedUntil }).FirstOrDefaultAsync(ct);
        if (user is null) return null;
        var now = clock.GetUtcNow();
        var strikes = await ActiveStrikesAsync(userId, now, ct);
        if (user.IsBlocked)
            return new ModerationNoticeDto("account_blocked", ModerationRules.Explain(LadderStep.Block, 0), strikes, null, true, false);
        if (user.ChatSuspendedUntil is { } until && until > now)
            return new ModerationNoticeDto("chat_suspended",
                $"Your chat is suspended until {until:u}. You can still learn, practise and take tests.", strikes, until, false, false);
        var warning = await PendingWarningAsync(userId, now, ct);
        return warning;
    }

    public async Task<ModerationDecision> CheckMessageAsync(Guid userId, int conversationId, string text, int? languageId, CancellationToken ct = default)
    {
        var restriction = await ChatRestrictionAsync(userId, ct);
        if (restriction is not null)
            return ModerationDecision.Reject(Error.Conflict(restriction.Code, restriction.Message), restriction);

        var now = clock.GetUtcNow();

        // FR-M04: flood control.
        var perMinute = await settings.GetIntAsync(SettingKeys.ChatMessagesPerMinute, 30, ct);
        var lastMinute = await db.Messages.CountAsync(m => m.SenderId == userId && m.SentAt >= now.AddMinutes(-1), ct);
        if (perMinute > 0 && lastMinute >= perMinute)
        {
            if (lastMinute == perMinute)
                await LogEventAsync(userId, conversationId, text, ModerationDetector.Spam, "flood", ModerationSeverity.Mild, null,
                    $"{lastMinute + 1} messages in a minute", ModerationOutcome.Rejected, ct);
            return ModerationDecision.Reject(Error.TooManyRequests("chat_rate_limited",
                "You're sending messages too fast. Wait a moment and try again."));
        }

        var filter = await filters.GetAsync(db, now, ct);
        var result = filter.Check(text, languageId);

        // BR-25: links and contact details are never delivered; every Nth attempt in a day is a strike.
        if (result.HasContacts)
        {
            var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var attempts = 1 + await db.ModerationEvents.CountAsync(e => e.UserId == userId && e.DetectedBy == ModerationDetector.LinkFilter
                                                                      && e.CreatedAt >= dayStart, ct);
            var perStrike = await settings.GetIntAsync(SettingKeys.ModerationLinkAttemptsPerStrike, 5, ct);
            var strikeDue = ModerationRules.LinkStrikeDue(attempts, perStrike);
            var ev = await LogEventAsync(userId, conversationId, text, ModerationDetector.LinkFilter, "contact", ModerationSeverity.Mild, null,
                string.Join(", ", result.Contacts.Select(c => c.Value).Take(5)), strikeDue ? ModerationOutcome.StrikeAdded : ModerationOutcome.Rejected, ct);
            StrikeOutcome? strike = strikeDue
                ? await AddStrikeAsync(userId, ModerationSeverity.Abusive, $"Shared links or contact details {attempts} times today", ev.Id, null, ct)
                : null;
            var left = perStrike - attempts % Math.Max(1, perStrike);
            var message = strike is not null
                ? "Links and contact details aren't allowed in chat. You've tried too many times today, so a strike was added."
                : $"Links, emails, phone numbers and social handles aren't allowed in chat (attempt {attempts}; a strike is added after {left} more).";
            return ModerationDecision.Reject(Error.Validation("Text", message), await NoticeAsync("link_blocked", message, userId, strike, ct));
        }

        if (result.Severity >= ModerationSeverity.Abusive)
        {
            var ev = await LogEventAsync(userId, conversationId, text, ModerationDetector.WordFilter, "word_list", result.Severity, null,
                result.MatchedTerms, result.Severity == ModerationSeverity.Severe ? ModerationOutcome.Blocked : ModerationOutcome.StrikeAdded, ct);
            var strike = await AddStrikeAsync(userId, result.Severity, $"Used blocked words: {result.MatchedTerms}", ev.Id, null, ct);
            var message = "This message wasn't sent because it contains words that aren't allowed. " +
                          ModerationRules.Explain(strike.Step, await SuspensionHoursAsync(ct));
            return ModerationDecision.Reject(Error.Validation("Text", message), await NoticeAsync("abusive_blocked", message, userId, strike, ct));
        }

        // FR-M04: the same text pushed into many chats.
        var spamRecipients = await settings.GetIntAsync(SettingKeys.ModerationSpamRecipients, 5, ct);
        var window = await settings.GetIntAsync(SettingKeys.ModerationSpamWindowMinutes, 10, ct);
        var normalized = ContentFilter.NormalizeText(text);
        if (normalized.Length >= 12)
        {
            var since = now.AddMinutes(-Math.Max(1, window));
            var recent = await db.Messages.AsNoTracking()
                .Where(m => m.SenderId == userId && m.SentAt >= since && m.MessageType == MessageType.Text)
                .OrderByDescending(m => m.Id).Take(200)
                .Select(m => new { m.ConversationId, m.Body }).ToListAsync(ct);
            if (ModerationRules.IsBroadcastSpam(recent.Select(r => (r.ConversationId, ContentFilter.NormalizeText(r.Body))), conversationId, normalized, spamRecipients))
            {
                var already = await db.ModerationEvents.AnyAsync(e => e.UserId == userId && e.DetectedBy == ModerationDetector.Spam
                                                                   && e.Category == "broadcast" && e.CreatedAt >= since, ct);
                var ev = await LogEventAsync(userId, conversationId, text, ModerationDetector.Spam, "broadcast", ModerationSeverity.Abusive, null,
                    null, already ? ModerationOutcome.Rejected : ModerationOutcome.StrikeAdded, ct);
                var strike = already ? null : await AddStrikeAsync(userId, ModerationSeverity.Abusive, "Sent the same message to many chats (spam)", ev.Id, null, ct);
                const string message = "This looks like spam: the same message was sent to many chats in a few minutes.";
                return ModerationDecision.Reject(Error.Validation("Text", message), await NoticeAsync("spam_blocked", message, userId, strike, ct));
            }
        }

        // FR-M10: mild words are masked and the message goes through.
        if (result.Severity == ModerationSeverity.Mild)
        {
            await LogEventAsync(userId, conversationId, text, ModerationDetector.WordFilter, "word_list", ModerationSeverity.Mild, null,
                result.MatchedTerms, ModerationOutcome.Masked, ct);
            return new ModerationDecision(true, result.MaskedText, text, MessageModeration.Masked, null, null);
        }

        return new ModerationDecision(true, text, null, MessageModeration.Pending, null, null);
    }

    public async Task<Result> CheckTextAsync(Guid userId, ModerationTarget target, string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Result.Success();
        var now = clock.GetUtcNow();
        var filter = await filters.GetAsync(db, now, ct);
        var result = filter.Check(text);
        var field = target switch { ModerationTarget.GroupName => "Name", ModerationTarget.DisplayName => "DisplayName", ModerationTarget.Bio => "Bio", _ => "Text" };
        if (result.HasContacts)
        {
            await LogEventAsync(userId, null, text, ModerationDetector.LinkFilter, "contact", ModerationSeverity.Mild, null,
                string.Join(", ", result.Contacts.Select(c => c.Value).Take(5)), ModerationOutcome.Rejected, ct, target);
            return Error.Validation(field, "Links, emails, phone numbers and social handles aren't allowed here.");
        }
        if (result.Severity > ModerationSeverity.None)
        {
            await LogEventAsync(userId, null, text, ModerationDetector.WordFilter, "word_list", result.Severity, null, result.MatchedTerms,
                ModerationOutcome.Rejected, ct, target);
            return Error.Validation(field, "This contains words that aren't allowed.");
        }
        return Result.Success();
    }

    public async Task<ModerationStatusDto> StatusAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.IsBlocked, u.ChatSuspendedUntil }).FirstAsync(ct);
        return new ModerationStatusDto(await ActiveStrikesAsync(userId, now, ct),
            user.ChatSuspendedUntil > now ? user.ChatSuspendedUntil : null, user.IsBlocked, await PendingWarningAsync(userId, now, ct));
    }

    public async Task<Result> AcknowledgeAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var pending = await db.ModerationStrikes.Where(s => s.UserId == userId && s.AcknowledgedAt == null && s.RemovedAt == null).ToListAsync(ct);
        foreach (var s in pending) s.AcknowledgedAt = now;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<StrikeOutcome> AddStrikeAsync(Guid userId, ModerationSeverity severity, string reason, long? eventId, string? by,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var windowDays = await settings.GetIntAsync(SettingKeys.ModerationStrikeWindowDays, 90, ct);
        db.ModerationStrikes.Add(new ModerationStrike
        {
            UserId = userId, ModerationEventId = eventId, Severity = severity, Reason = Trim(reason, 300), CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, windowDays))
        });
        await db.SaveChangesAsync(ct);

        var active = await ActiveStrikesAsync(userId, now, ct);
        var step = ModerationRules.StepFor(active, severity);
        var hours = await SuspensionHoursAsync(ct);
        DateTimeOffset? until = null;

        switch (step)
        {
            case LadderStep.Block:
                await BlockAsync(userId, severity == ModerationSeverity.Severe ? $"Severe content: {reason}" : $"Third strike: {reason}", by, ct);
                break;
            case LadderStep.Suspension:
                until = now.AddHours(hours);
                await SetSuspensionAsync(userId, until.Value, ct);
                await LogEventAsync(userId, null, null, by is null ? ModerationDetector.WordFilter : ModerationDetector.Moderator, "strike_ladder",
                    severity, null, null, ModerationOutcome.Suspended, ct, ModerationTarget.User);
                break;
        }

        if (step != LadderStep.Block)
            await notifier.ModerationWarningAsync(userId, new ModerationWarningDto(step == LadderStep.Warning ? "strike_warning" : "chat_suspended",
                ModerationRules.Explain(step, hours), active, until, false), ct);
        if (by is not null)
            await audit.LogAsync("AddStrike", "Learner", userId.ToString(), null, new { severity, reason, active, step = step.ToString() }, ct);
        return new StrikeOutcome(step, active, until);
    }

    public async Task<Result> RemoveStrikeAsync(int strikeId, string reason, string by, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required.");
        var strike = await db.ModerationStrikes.FirstOrDefaultAsync(s => s.Id == strikeId, ct);
        if (strike is null) return Error.NotFound("Strike");
        if (strike.RemovedAt is not null) return Result.Success();
        strike.RemovedAt = clock.GetUtcNow();
        strike.RemovedBy = by;
        strike.RemovedReason = Trim(reason, 500);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("RemoveStrike", "Learner", strike.UserId.ToString(), new { strike.Id, strike.Reason }, new { reason }, ct);
        return Result.Success();
    }

    public async Task<Result> SuspendAsync(Guid userId, TimeSpan duration, string reason, string by, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required.");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(365)) return Error.Validation("Days", "Suspend for 1 to 365 days.");
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return Error.NotFound("Learner");
        var until = clock.GetUtcNow() + duration;
        await SetSuspensionAsync(userId, until, ct);
        await LogEventAsync(userId, null, reason, ModerationDetector.Moderator, "manual", ModerationSeverity.Abusive, null, null,
            ModerationOutcome.Suspended, ct, ModerationTarget.User, by);
        await notifier.ModerationWarningAsync(userId, new ModerationWarningDto("chat_suspended",
            $"A moderator suspended your chat until {until:u}. Reason: {reason}", await ActiveStrikesAsync(userId, clock.GetUtcNow(), ct), until, false), ct);
        await audit.LogAsync("SuspendChat", "Learner", userId.ToString(), null, new { until, reason }, ct);
        return Result.Success();
    }

    public async Task<Result> BlockAsync(Guid userId, string reason, string? by, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("Learner");
        if (user.UserType != UserType.Learner) return Error.Validation("UserId", "Only learner accounts can be blocked here.");
        if (user.IsBlocked) return Result.Success();

        var now = clock.GetUtcNow();
        user.IsBlocked = true;
        user.BlockedAt = now;
        user.BlockReason = Trim(reason, 500);
        user.ChatSuspendedUntil = null;
        // A new stamp makes every access token fail at the next request (SecurityStampCheck).
        user.SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant();

        foreach (var t in await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct))
        {
            t.RevokedAt = now;
            t.RevokedReason = "account blocked";
        }

        // BR-27: the same email, Google/Apple account or device can't come back with a new account.
        var logins = await db.UserLogins.Where(l => l.UserId == userId).Select(l => l.LoginProvider + ":" + l.ProviderKey).ToListAsync(ct);
        var devices = await db.UserDevices.Where(d => d.UserId == userId).Select(d => d.DeviceId).ToListAsync(ct);
        db.BlockedIdentities.Add(new BlockedIdentity { UserId = userId, NormalizedEmail = user.NormalizedEmail, Reason = user.BlockReason, CreatedAt = now });
        foreach (var key in logins.Distinct())
            db.BlockedIdentities.Add(new BlockedIdentity { UserId = userId, ExternalLoginKey = key.ToLowerInvariant(), Reason = user.BlockReason, CreatedAt = now });
        foreach (var device in devices.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
            db.BlockedIdentities.Add(new BlockedIdentity { UserId = userId, DeviceId = device, Reason = user.BlockReason, CreatedAt = now });
        await db.SaveChangesAsync(ct);

        // FR-M14: out of every competition still going, and competition points taken back.
        PointAction[] competitionActions = [PointAction.CompetitionWin, PointAction.CompetitionDraw, PointAction.CompetitionLoss,
            PointAction.TournamentPrize, PointAction.GroupBattleBonus];
        foreach (var tx in await db.PointTransactions.Where(t => t.UserId == userId && !t.IsRevoked && competitionActions.Contains(t.Action)).ToListAsync(ct))
        {
            tx.IsRevoked = true;
            tx.RevokedAt = now;
            tx.RevokedReason = "Account blocked (FR-M14)";
        }
        CompetitionStatus[] open = [CompetitionStatus.Pending, CompetitionStatus.Waiting, CompetitionStatus.Scheduled, CompetitionStatus.Running];
        foreach (var p in await db.CompetitionPlayers.Include(p => p.Competition)
                     .Where(p => p.UserId == userId && open.Contains(p.Competition.Status)).ToListAsync(ct))
        {
            p.State = PlayerState.Left;
            p.LeftEarly = true;
            p.Competition.NextEventAt = now;
            p.Competition.ConcurrencyStamp = Guid.NewGuid();
        }
        await db.SaveChangesAsync(ct);
        await points.RecalculateAsync(userId, ct);

        await LogEventAsync(userId, null, reason, by is null ? ModerationDetector.WordFilter : ModerationDetector.Moderator, "block",
            ModerationSeverity.Severe, null, null, ModerationOutcome.Blocked, ct, ModerationTarget.User, by);
        await audit.LogAsync("Block", "Learner", userId.ToString(), new { IsBlocked = false }, new { IsBlocked = true, reason, by = by ?? "automatic" }, ct);

        sessions.Invalidate(userId);
        try
        {
            await notifier.ModerationWarningAsync(userId, new ModerationWarningDto("account_blocked", ModerationRules.Explain(LadderStep.Block, 0), 3, null, true), ct);
            await notifier.DisconnectAsync(userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not close live connections of blocked user {UserId}", userId);
        }
        return Result.Success();
    }

    public async Task<Result> UnblockAsync(Guid userId, string reason, string by, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required to unblock an account.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Error.NotFound("Learner");
        if (!user.IsBlocked) return Result.Success();

        var now = clock.GetUtcNow();
        var old = new { user.BlockReason, user.BlockedAt };
        user.IsBlocked = false;
        user.BlockedAt = null;
        user.BlockReason = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant();
        db.BlockedIdentities.RemoveRange(await db.BlockedIdentities.Where(b => b.UserId == userId).ToListAsync(ct));
        // A fresh start: the strikes that led to the block no longer count.
        foreach (var s in await db.ModerationStrikes.Where(s => s.UserId == userId && s.RemovedAt == null && s.ExpiresAt > now).ToListAsync(ct))
        {
            s.RemovedAt = now;
            s.RemovedBy = by;
            s.RemovedReason = "Account unblocked: " + Trim(reason, 450);
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Unblock", "Learner", userId.ToString(), old, new { reason, by }, ct);
        sessions.Invalidate(userId);
        return Result.Success();
    }

    public async Task<Result> RemoveMessageAsync(long messageId, string by, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return Error.NotFound("Message");
        if (message.ModerationStatus == MessageModeration.Removed && !message.IsActive) return Result.Success();
        message.IsActive = false;
        message.ModerationStatus = MessageModeration.Removed;
        message.DeletedForEveryoneAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await TellMembersDeletedAsync(message, ct);
        await audit.LogAsync("RemoveMessage", "Message", messageId.ToString(), null, new { by }, ct);
        return Result.Success();
    }

    public async Task<bool> ScanAsync(long messageId, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.AiScannedAt is not null || !message.IsActive || message.SenderId is null
            || message.MessageType != MessageType.Text) return false;

        var now = clock.GetUtcNow();
        if (!await settings.GetBoolAsync(SettingKeys.ModerationAiEnabled, true, ct))
        {
            message.AiScannedAt = now;
            if (message.ModerationStatus == MessageModeration.Pending) message.ModerationStatus = MessageModeration.Clean;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var outcome = await ai.ModerateAsync(message.OriginalBody ?? message.Body, new AiCallContext(message.SenderId, null), ct);
        message.AiScannedAt = clock.GetUtcNow();
        if (!outcome.IsSuccess)
        {
            // Budget used up or provider down: leave it clean (the word filter already ran) and note it.
            logger.LogInformation("AI moderation skipped for message {MessageId}: {Error}", messageId, outcome.Error?.Message);
            if (message.ModerationStatus == MessageModeration.Pending) message.ModerationStatus = MessageModeration.Clean;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var verdict = outcome.Value.Value;
        var abusive = await settings.GetIntAsync(SettingKeys.ModerationAiAbusivePercent, 70, ct);
        var severe = await settings.GetIntAsync(SettingKeys.ModerationAiSeverePercent, 90, ct);
        var severity = ModerationRules.AiSeverity(verdict, abusive, severe);
        var senderId = message.SenderId.Value;

        switch (severity)
        {
            case ModerationSeverity.None:
                if (message.ModerationStatus == MessageModeration.Pending) message.ModerationStatus = MessageModeration.Clean;
                await db.SaveChangesAsync(ct);
                break;

            case ModerationSeverity.Mild:
                // Borderline: a moderator decides (FR-M21).
                if (message.ModerationStatus == MessageModeration.Pending) message.ModerationStatus = MessageModeration.Clean;
                await db.SaveChangesAsync(ct);
                await LogEventAsync(senderId, message.ConversationId, message.OriginalBody ?? message.Body, ModerationDetector.Ai, verdict.Category,
                    severity, verdict.Score, verdict.Reason, ModerationOutcome.Open, ct, ModerationTarget.Message, null, message.Id);
                break;

            default:
                message.IsActive = false;
                message.ModerationStatus = MessageModeration.Removed;
                message.DeletedForEveryoneAt = message.AiScannedAt;
                await db.SaveChangesAsync(ct);
                await TellMembersDeletedAsync(message, ct);
                var ev = await LogEventAsync(senderId, message.ConversationId, message.OriginalBody ?? message.Body, ModerationDetector.Ai, verdict.Category,
                    severity, verdict.Score, verdict.Reason, severity == ModerationSeverity.Severe ? ModerationOutcome.Blocked : ModerationOutcome.StrikeAdded,
                    ct, ModerationTarget.Message, null, message.Id);
                await AddStrikeAsync(senderId, severity, $"Message removed by the AI check ({verdict.Category})", ev.Id, null, ct);
                break;
        }
        return true;
    }

    public async Task<IReadOnlyList<long>> PendingScansAsync(int take, CancellationToken ct = default)
    {
        var before = clock.GetUtcNow().AddMinutes(-1);
        var since = clock.GetUtcNow().AddDays(-2);
        return await db.Messages.AsNoTracking()
            .Where(m => m.AiScannedAt == null && m.IsActive && m.MessageType == MessageType.Text && m.SenderId != null
                        && m.SentAt < before && m.SentAt > since)
            .OrderBy(m => m.Id).Select(m => m.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<int> ActiveStrikesAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.ModerationStrikes.CountAsync(s => s.UserId == userId && s.RemovedAt == null && s.ExpiresAt > now, ct);

    private async Task<ModerationNoticeDto?> PendingWarningAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.ModerationStrikes.AsNoTracking()
            .Where(s => s.UserId == userId && s.AcknowledgedAt == null && s.RemovedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.Reason).FirstOrDefaultAsync(ct);
        if (pending is null) return null;
        var strikes = await ActiveStrikesAsync(userId, now, ct);
        var step = ModerationRules.StepFor(strikes, ModerationSeverity.Abusive);
        return new ModerationNoticeDto("warning_pending",
            $"{pending}. {ModerationRules.Explain(step, await SuspensionHoursAsync(ct))} Read and accept the community rules to keep chatting.",
            strikes, null, false, true);
    }

    private async Task<ModerationNoticeDto> NoticeAsync(string code, string message, Guid userId, StrikeOutcome? strike, CancellationToken ct)
    {
        if (strike is null)
            return new ModerationNoticeDto(code, message, await ActiveStrikesAsync(userId, clock.GetUtcNow(), ct), null, false, false);
        return new ModerationNoticeDto(code, message, strike.ActiveStrikes, strike.SuspendedUntil, strike.Step == LadderStep.Block,
            strike.Step == LadderStep.Warning);
    }

    private Task<int> SuspensionHoursAsync(CancellationToken ct) => settings.GetIntAsync(SettingKeys.ModerationSuspensionHours, 24, ct);

    private async Task SetSuspensionAsync(Guid userId, DateTimeOffset until, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (user.ChatSuspendedUntil is null || user.ChatSuspendedUntil < until) user.ChatSuspendedUntil = until;
        await db.SaveChangesAsync(ct);
    }

    private async Task TellMembersDeletedAsync(Message message, CancellationToken ct)
    {
        var members = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == message.ConversationId && m.IsActive && !m.IsPending).Select(m => m.UserId).ToListAsync(ct);
        try
        {
            await notifier.MessageDeletedAsync(members, new MessageDeletedDto(message.ConversationId, message.Id), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not announce removal of message {MessageId}", message.Id);
        }
    }

    private async Task<ModerationEvent> LogEventAsync(Guid userId, int? conversationId, string? text, ModerationDetector detector, string? category,
        ModerationSeverity severity, double? score, string? matched, ModerationOutcome outcome, CancellationToken ct,
        ModerationTarget target = ModerationTarget.Message, string? reviewer = null, long? messageId = null)
    {
        var ev = new ModerationEvent
        {
            UserId = userId, EntityType = target, EntityId = messageId?.ToString(), ConversationId = conversationId, Text = Trim(text, 2000),
            DetectedBy = detector, Category = Trim(category, 60), Severity = severity, Score = score, MatchedTerms = Trim(matched, 500),
            ActionTaken = outcome, CreatedAt = clock.GetUtcNow(),
            ReviewedByEmail = reviewer, ReviewedAt = reviewer is null ? null : clock.GetUtcNow()
        };
        db.ModerationEvents.Add(ev);
        await db.SaveChangesAsync(ct);
        return ev;
    }

    private static string Trim(string? s, int max) => s is null ? null! : s.Length <= max ? s : s[..max];
}
