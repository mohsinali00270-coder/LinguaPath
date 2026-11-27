using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Social;

/// <summary>Profiles, search, friends, blocks, reports and privacy (FR-S20 .. FR-S24, BR-30, BR-31).</summary>
public interface ISocialService
{
    Task<Result<PublicProfileDto>> ProfileAsync(Guid viewerId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserCardDto>> SearchAsync(Guid viewerId, string? query, int? languageId, CancellationToken ct = default);

    Task<FriendsDto> FriendsAsync(Guid userId, CancellationToken ct = default);
    Task<Result<FriendRequestDto>> SendRequestAsync(Guid userId, Guid otherId, CancellationToken ct = default);
    Task<Result> RespondAsync(Guid userId, int requestId, bool accept, CancellationToken ct = default);
    /// <summary>Cancels my pending request or ends the friendship.</summary>
    Task<Result> RemoveFriendAsync(Guid userId, Guid otherId, CancellationToken ct = default);

    Task<Result> BlockAsync(Guid userId, Guid otherId, CancellationToken ct = default);
    Task<Result> UnblockAsync(Guid userId, Guid otherId, CancellationToken ct = default);
    Task<IReadOnlyList<BlockedUserDto>> BlockedAsync(Guid userId, CancellationToken ct = default);

    Task<Result> ReportAsync(Guid userId, ReportRequest request, CancellationToken ct = default);

    Task<PrivacyDto> PrivacyAsync(Guid userId, CancellationToken ct = default);
    Task<Result<PrivacyDto>> UpdatePrivacyAsync(Guid userId, PrivacyDto request, CancellationToken ct = default);

    /// <summary>Shared rule for chats and challenges: blocks either way, privacy setting, blocked accounts.</summary>
    Task<Result> CanContactAsync(Guid fromId, Guid toId, bool challenge, CancellationToken ct = default);
}

internal sealed class SocialService(IApplicationDbContext db, IPresenceTracker presence, TimeProvider clock) : ISocialService
{
    private const int MaxPendingRequests = 50;
    private const int MaxReportsPerDay = 20;

    public async Task<Result<PublicProfileDto>> ProfileAsync(Guid viewerId, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted && u.UserType == UserType.Learner)
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.FullName, u.PhotoUrl, u.Bio, u.IsBlocked, u.LastLoginAt })
            .FirstOrDefaultAsync(ct);
        // FR-S22: someone who blocked me is invisible to me.
        if (user is null || await db.UserBlocks.AnyAsync(b => b.BlockerId == userId && b.BlockedId == viewerId, ct))
            return Error.NotFound("Learner");

        var relation = await RelationAsync(viewerId, userId, ct);
        var privacy = await SettingsOfAsync(userId, ct);

        var languages = await db.LearnerCourses.AsNoTracking()
            .Where(c => c.UserId == userId && c.Status != LearnerCourseStatus.Cancelled)
            .Select(c => new { c.Course.Language.Name, c.CurrentLevel })
            .ToListAsync(ct);
        var points = await db.UserPointsSummaries.AsNoTracking().Where(p => p.UserId == userId)
            .Select(p => new { p.TotalPoints, p.Tier }).FirstOrDefaultAsync(ct);
        var badges = await db.UserBadges.AsNoTracking().Where(b => b.UserId == userId).OrderBy(b => b.EarnedAt)
            .Select(b => new { b.Badge, b.EarnedAt }).ToListAsync(ct);

        var canMessage = relation != RelationState.Self && (await CanContactAsync(viewerId, userId, false, ct)).IsSuccess;
        var showOnline = privacy.ShowOnline && !user.IsBlocked && relation != RelationState.Blocked;
        return new PublicProfileDto(
            user.Id, user.IsBlocked ? "Blocked account" : user.Name, user.IsBlocked ? null : user.PhotoUrl, user.IsBlocked ? null : user.Bio,
            languages.GroupBy(l => l.Name).Select(g => new ProfileLanguageDto(g.Key, g.Max(x => x.CurrentLevel))).ToList(),
            points?.TotalPoints ?? 0, points?.Tier ?? PointTier.Bronze,
            badges.Select(b => Badges.ToDto(b.Badge, b.EarnedAt)).ToList(),
            relation, canMessage, user.IsBlocked,
            showOnline || relation == RelationState.Self ? presence.IsOnline(userId) : null,
            showOnline || relation == RelationState.Self ? presence.LastSeen(userId) ?? user.LastLoginAt : null);
    }

    public async Task<IReadOnlyList<UserCardDto>> SearchAsync(Guid viewerId, string? query, int? languageId, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2 && languageId is null) return [];

        var blockedEither = db.UserBlocks.Where(b => b.BlockerId == viewerId || b.BlockedId == viewerId)
            .Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId);
        var users = db.Users.AsNoTracking().Where(u => u.UserType == UserType.Learner && u.IsActive && !u.IsBlocked && !u.IsDeleted
                                                        && u.EmailConfirmed && u.Id != viewerId && !blockedEither.Contains(u.Id));
        // FR-S20: by display name only, never by email or phone.
        if (q.Length >= 2) users = users.Where(u => (u.DisplayName ?? u.FullName).Contains(q));
        if (languageId is { } lang)
            users = users.Where(u => db.LearnerCourses.Any(c => c.UserId == u.Id && c.LanguageId == lang && c.Status == LearnerCourseStatus.Active));

        var rows = await users.OrderBy(u => u.DisplayName ?? u.FullName).Take(30)
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.FullName, u.PhotoUrl }).ToListAsync(ct);
        return await CardsAsync(viewerId, rows.Select(r => (r.Id, r.Name, r.PhotoUrl)).ToList(), ct);
    }

    public async Task<FriendsDto> FriendsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Friendships.AsNoTracking()
            .Where(f => (f.RequesterId == userId || f.AddresseeId == userId) && f.Status != FriendshipStatus.Rejected)
            .Select(f => new
            {
                f.Id, f.Status, f.CreatedAt, Incoming = f.AddresseeId == userId,
                OtherId = f.RequesterId == userId ? f.AddresseeId : f.RequesterId,
                OtherName = f.RequesterId == userId ? (f.Addressee.DisplayName ?? f.Addressee.FullName) : (f.Requester.DisplayName ?? f.Requester.FullName),
                OtherPhoto = f.RequesterId == userId ? f.Addressee.PhotoUrl : f.Requester.PhotoUrl,
                OtherOk = f.RequesterId == userId ? !f.Addressee.IsBlocked && !f.Addressee.IsDeleted : !f.Requester.IsBlocked && !f.Requester.IsDeleted
            })
            .ToListAsync(ct);
        var usable = rows.Where(r => r.OtherOk).ToList();
        var cards = (await CardsAsync(userId, usable.Select(r => (r.OtherId, r.OtherName, r.OtherPhoto)).ToList(), ct)).ToDictionary(c => c.UserId);
        var friends = usable.Where(r => r.Status == FriendshipStatus.Accepted).Select(r => cards[r.OtherId]).OrderBy(c => c.DisplayName).ToList();
        var requests = usable.Where(r => r.Status == FriendshipStatus.Pending)
            .OrderByDescending(r => r.CreatedAt).Select(r => new FriendRequestDto(r.Id, cards[r.OtherId], r.Incoming, r.CreatedAt)).ToList();
        return new FriendsDto(friends, requests);
    }

    public async Task<Result<FriendRequestDto>> SendRequestAsync(Guid userId, Guid otherId, CancellationToken ct = default)
    {
        if (userId == otherId) return Error.Validation("UserId", "You can't add yourself.");
        var other = await db.Users.AsNoTracking().Where(u => u.Id == otherId && u.UserType == UserType.Learner && !u.IsDeleted && !u.IsBlocked)
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.FullName, u.PhotoUrl }).FirstOrDefaultAsync(ct);
        if (other is null || await db.UserBlocks.AnyAsync(b => b.BlockerId == otherId && b.BlockedId == userId, ct))
            return Error.NotFound("Learner");
        if (await db.UserBlocks.AnyAsync(b => b.BlockerId == userId && b.BlockedId == otherId, ct))
            return Error.Conflict("user_blocked", "Unblock this learner first.");

        var now = clock.GetUtcNow();
        var existing = await db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == userId && f.AddresseeId == otherId) || (f.RequesterId == otherId && f.AddresseeId == userId), ct);
        if (existing is not null)
        {
            switch (existing.Status)
            {
                case FriendshipStatus.Accepted:
                    return Error.Conflict("already_friends", "You're already friends.");
                case FriendshipStatus.Pending when existing.RequesterId == otherId:
                    // They asked me first: sending a request back accepts it.
                    existing.Status = FriendshipStatus.Accepted;
                    existing.RespondedAt = now;
                    await db.SaveChangesAsync(ct);
                    return new FriendRequestDto(existing.Id, new UserCardDto(other.Id, other.Name, other.PhotoUrl, null, RelationState.Friends), false, existing.CreatedAt);
                case FriendshipStatus.Pending:
                    return Error.Conflict("request_pending", "Your request is waiting for an answer.");
                default:
                    // Rejected earlier: a new request starts over, sent from me.
                    db.Friendships.Remove(existing);
                    await db.SaveChangesAsync(ct);
                    break;
            }
        }

        var pending = await db.Friendships.CountAsync(f => f.RequesterId == userId && f.Status == FriendshipStatus.Pending, ct);
        if (pending >= MaxPendingRequests)
            return Error.TooManyRequests("too_many_requests", $"You have {pending} requests waiting. Wait for some answers first.");

        var row = new Friendship { RequesterId = userId, AddresseeId = otherId, Status = FriendshipStatus.Pending, CreatedAt = now };
        db.Friendships.Add(row);
        await db.SaveChangesAsync(ct);
        return new FriendRequestDto(row.Id, new UserCardDto(other.Id, other.Name, other.PhotoUrl, null, RelationState.RequestSent), false, now);
    }

    public async Task<Result> RespondAsync(Guid userId, int requestId, bool accept, CancellationToken ct = default)
    {
        var row = await db.Friendships.FirstOrDefaultAsync(f => f.Id == requestId && f.AddresseeId == userId, ct);
        if (row is null) return Error.NotFound("Friend request");
        if (row.Status != FriendshipStatus.Pending) return Error.Conflict("request_answered", "This request was already answered.");
        row.Status = accept ? FriendshipStatus.Accepted : FriendshipStatus.Rejected;
        row.RespondedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RemoveFriendAsync(Guid userId, Guid otherId, CancellationToken ct = default)
    {
        var row = await db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == userId && f.AddresseeId == otherId) || (f.RequesterId == otherId && f.AddresseeId == userId), ct);
        if (row is null) return Error.NotFound("Friend");
        db.Friendships.Remove(row);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> BlockAsync(Guid userId, Guid otherId, CancellationToken ct = default)
    {
        if (userId == otherId) return Error.Validation("UserId", "You can't block yourself.");
        if (!await db.Users.AnyAsync(u => u.Id == otherId && u.UserType == UserType.Learner, ct)) return Error.NotFound("Learner");
        if (!await db.UserBlocks.AnyAsync(b => b.BlockerId == userId && b.BlockedId == otherId, ct))
            db.UserBlocks.Add(new UserBlock { BlockerId = userId, BlockedId = otherId, CreatedAt = clock.GetUtcNow() });
        // FR-S22: blocking also ends the friendship and any pending request.
        db.Friendships.RemoveRange(await db.Friendships.Where(f =>
            (f.RequesterId == userId && f.AddresseeId == otherId) || (f.RequesterId == otherId && f.AddresseeId == userId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> UnblockAsync(Guid userId, Guid otherId, CancellationToken ct = default)
    {
        var row = await db.UserBlocks.FirstOrDefaultAsync(b => b.BlockerId == userId && b.BlockedId == otherId, ct);
        if (row is null) return Result.Success();
        db.UserBlocks.Remove(row);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<BlockedUserDto>> BlockedAsync(Guid userId, CancellationToken ct = default) =>
        await db.UserBlocks.AsNoTracking().Where(b => b.BlockerId == userId)
            .Join(db.Users, b => b.BlockedId, u => u.Id, (b, u) => new { b.BlockedId, Name = u.DisplayName ?? u.FullName, b.CreatedAt })
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new BlockedUserDto(x.BlockedId, x.Name, x.CreatedAt))
            .ToListAsync(ct);

    public async Task<Result> ReportAsync(Guid userId, ReportRequest request, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Reason)) return Error.Validation("Reason", "Choose a reason.");
        if (request.Details is { Length: > 1000 }) return Error.Validation("Details", "Keep the details under 1,000 characters.");

        Guid reportedId;
        int? conversationId = null;
        string? text = null;
        if (request.MessageId is { } messageId)
        {
            var message = await db.Messages.AsNoTracking().Where(m => m.Id == messageId)
                .Select(m => new { m.SenderId, m.ConversationId, Text = m.OriginalBody ?? m.Body }).FirstOrDefaultAsync(ct);
            // Only messages from a chat I'm in.
            if (message?.SenderId is null || !await db.ConversationMembers.AnyAsync(cm => cm.ConversationId == message.ConversationId
                                                                                        && cm.UserId == userId, ct))
                return Error.NotFound("Message");
            reportedId = message.SenderId.Value;
            conversationId = message.ConversationId;
            text = message.Text;
        }
        else if (request.UserId is { } uid)
        {
            if (!await db.Users.AnyAsync(u => u.Id == uid && u.UserType == UserType.Learner, ct)) return Error.NotFound("Learner");
            reportedId = uid;
        }
        else return Error.Validation("UserId", "Say who or which message you're reporting.");

        if (reportedId == userId) return Error.Validation("UserId", "You can't report yourself.");

        var now = clock.GetUtcNow();
        var today = await db.UserReports.CountAsync(r => r.ReporterId == userId && r.CreatedAt >= now.AddDays(-1), ct);
        if (today >= MaxReportsPerDay) return Error.TooManyRequests("too_many_reports", "You've sent a lot of reports today. Our moderators will look at them.");
        if (await db.UserReports.AnyAsync(r => r.ReporterId == userId && r.ReportedUserId == reportedId && r.MessageId == request.MessageId
                                               && r.Status == ReportStatus.Open, ct))
            return Result.Success();

        var ev = new ModerationEvent
        {
            UserId = reportedId, EntityType = request.MessageId is null ? ModerationTarget.User : ModerationTarget.Message,
            EntityId = request.MessageId?.ToString(), ConversationId = conversationId, Text = text,
            DetectedBy = ModerationDetector.Report, Category = request.Reason.ToString().ToLowerInvariant(),
            Severity = ModerationSeverity.Mild, MatchedTerms = request.Details is null ? null : request.Details[..Math.Min(500, request.Details.Length)],
            ActionTaken = ModerationOutcome.Open, CreatedAt = now
        };
        db.ModerationEvents.Add(ev);
        await db.SaveChangesAsync(ct);
        db.UserReports.Add(new UserReport
        {
            ReporterId = userId, ReportedUserId = reportedId, MessageId = request.MessageId, Reason = request.Reason,
            Details = request.Details?.Trim(), CreatedAt = now, ModerationEventId = ev.Id
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<PrivacyDto> PrivacyAsync(Guid userId, CancellationToken ct = default)
    {
        var p = await SettingsOfAsync(userId, ct);
        return new PrivacyDto(p.WhoCanMessage, p.WhoCanChallenge, p.ShowOnLeaderboard, p.ShowOnline, p.ReadReceipts);
    }

    public async Task<Result<PrivacyDto>> UpdatePrivacyAsync(Guid userId, PrivacyDto request, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.WhoCanMessage) || !Enum.IsDefined(request.WhoCanChallenge))
            return Error.Validation("WhoCanMessage", "Choose Everyone, Friends only or Nobody.");
        var row = await db.UserPrivacySettings.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (row is null)
        {
            row = new UserPrivacySettings { UserId = userId };
            db.UserPrivacySettings.Add(row);
        }
        row.WhoCanMessage = request.WhoCanMessage;
        row.WhoCanChallenge = request.WhoCanChallenge;
        row.ShowOnLeaderboard = request.ShowOnLeaderboard;
        row.ShowOnline = request.ShowOnline;
        row.ReadReceipts = request.ReadReceipts;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<Result> CanContactAsync(Guid fromId, Guid toId, bool challenge, CancellationToken ct = default)
    {
        if (fromId == toId) return Error.Validation("UserId", "That's you.");
        var target = await db.Users.AsNoTracking().Where(u => u.Id == toId && u.UserType == UserType.Learner && !u.IsDeleted)
            .Select(u => new { u.IsBlocked, u.IsActive, u.DateOfBirth }).FirstOrDefaultAsync(ct);
        if (target is null) return Error.NotFound("Learner");
        if (target.IsBlocked || !target.IsActive) return Error.Conflict("user_unavailable", "This learner can't receive messages.");
        if (await db.UserBlocks.AnyAsync(b => b.BlockerId == toId && b.BlockedId == fromId, ct))
            return Error.NotFound("Learner");
        if (await db.UserBlocks.AnyAsync(b => b.BlockerId == fromId && b.BlockedId == toId, ct))
            return Error.Conflict("user_blocked", "You blocked this learner. Unblock them to chat.");
        var privacy = await SettingsOfAsync(toId, ct);
        var friends = await AreFriendsAsync(fromId, toId, ct);

        // BR-32: when either learner is under 18, only friends can message or challenge each other.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var senderDob = await db.Users.AsNoTracking().Where(u => u.Id == fromId).Select(u => u.DateOfBirth).FirstOrDefaultAsync(ct);
        if (!friends && (AgeRules.IsMinor(target.DateOfBirth, today) || AgeRules.IsMinor(senderDob, today)))
            return Error.Forbidden("minor_friends_only", $"Learners under {AgeRules.AdultAge} can only {(challenge ? "challenge" : "message")} friends.");
        var setting = challenge ? privacy.WhoCanChallenge : privacy.WhoCanMessage;
        if (!ModerationRules.AudienceAllows(setting, friends))
            return Error.Forbidden("privacy_restricted", setting == AudienceSetting.FriendsOnly
                ? $"This learner only accepts {(challenge ? "challenges" : "messages")} from friends."
                : $"This learner doesn't accept {(challenge ? "challenges" : "messages")}.");
        return Result.Success();
    }

    // ------------------------------------------------------------------ helpers

    private Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct) =>
        db.Friendships.AnyAsync(f => f.Status == FriendshipStatus.Accepted &&
                                     ((f.RequesterId == a && f.AddresseeId == b) || (f.RequesterId == b && f.AddresseeId == a)), ct);

    private async Task<UserPrivacySettings> SettingsOfAsync(Guid userId, CancellationToken ct) =>
        await db.UserPrivacySettings.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct) ?? new UserPrivacySettings { UserId = userId };

    private async Task<RelationState> RelationAsync(Guid viewerId, Guid userId, CancellationToken ct)
    {
        if (viewerId == userId) return RelationState.Self;
        if (await db.UserBlocks.AnyAsync(b => b.BlockerId == viewerId && b.BlockedId == userId, ct)) return RelationState.Blocked;
        var f = await db.Friendships.AsNoTracking().Where(x =>
                (x.RequesterId == viewerId && x.AddresseeId == userId) || (x.RequesterId == userId && x.AddresseeId == viewerId))
            .Select(x => new { x.Status, x.RequesterId }).FirstOrDefaultAsync(ct);
        return f switch
        {
            null => RelationState.None,
            { Status: FriendshipStatus.Accepted } => RelationState.Friends,
            { Status: FriendshipStatus.Pending } when f.RequesterId == viewerId => RelationState.RequestSent,
            { Status: FriendshipStatus.Pending } => RelationState.RequestReceived,
            _ => RelationState.None
        };
    }

    private async Task<IReadOnlyList<UserCardDto>> CardsAsync(Guid viewerId, IReadOnlyList<(Guid Id, string Name, string? Photo)> users, CancellationToken ct)
    {
        if (users.Count == 0) return [];
        var ids = users.Select(u => u.Id).ToList();
        var langs = await db.LearnerCourses.AsNoTracking().Where(c => ids.Contains(c.UserId) && c.Status == LearnerCourseStatus.Active)
            .Select(c => new { c.UserId, c.Course.Language.Name }).ToListAsync(ct);
        var friendships = await db.Friendships.AsNoTracking()
            .Where(f => (f.RequesterId == viewerId && ids.Contains(f.AddresseeId)) || (f.AddresseeId == viewerId && ids.Contains(f.RequesterId)))
            .Select(f => new { f.RequesterId, f.AddresseeId, f.Status }).ToListAsync(ct);
        var blocked = (await db.UserBlocks.AsNoTracking().Where(b => b.BlockerId == viewerId && ids.Contains(b.BlockedId))
            .Select(b => b.BlockedId).ToListAsync(ct)).ToHashSet();

        return users.Select(u =>
        {
            var f = friendships.FirstOrDefault(x => x.RequesterId == u.Id || x.AddresseeId == u.Id);
            var relation = blocked.Contains(u.Id) ? RelationState.Blocked
                : f is null || f.Status == FriendshipStatus.Rejected ? RelationState.None
                : f.Status == FriendshipStatus.Accepted ? RelationState.Friends
                : f.RequesterId == viewerId ? RelationState.RequestSent : RelationState.RequestReceived;
            var l = langs.Where(x => x.UserId == u.Id).Select(x => x.Name).Distinct().ToList();
            return new UserCardDto(u.Id, u.Name, u.Photo, l.Count == 0 ? null : string.Join(", ", l), relation);
        }).ToList();
    }
}
