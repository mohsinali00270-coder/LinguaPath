using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Application.Features.Social;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Chat;
using LinguaPath.Shared.Contracts.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

/// <summary>Public profiles, search, friends, blocks, reports and privacy (FR-S20 .. FR-S24).</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class SocialController(ISocialService social, IModerationService moderation) : ApiControllerBase
{
    /// <summary>FR-S20: by display name (2+ letters) and/or the language someone is learning. Never by email or phone.</summary>
    [HttpGet("users/search")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<UserCardDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int? languageId, CancellationToken ct) =>
        OkData(await social.SearchAsync(CurrentUserId, q, languageId, ct));

    [HttpGet("users/{userId:guid}/profile")]
    [ProducesResponseType<MyApiResponse<PublicProfileDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile(Guid userId, CancellationToken ct) => FromResult(await social.ProfileAsync(CurrentUserId, userId, ct));

    [HttpGet("friends")]
    [ProducesResponseType<MyApiResponse<FriendsDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Friends(CancellationToken ct) => OkData(await social.FriendsAsync(CurrentUserId, ct));

    /// <summary>Sending a request to someone who already asked you accepts theirs.</summary>
    [HttpPost("friends/requests")]
    [ProducesResponseType<MyApiResponse<FriendRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SendRequest(SendFriendRequest request, CancellationToken ct) =>
        FromResult(await social.SendRequestAsync(CurrentUserId, request.UserId, ct));

    [HttpPost("friends/requests/{id:int}/respond")]
    public async Task<IActionResult> Respond(int id, RespondFriendRequest request, CancellationToken ct) =>
        FromResult(await social.RespondAsync(CurrentUserId, id, request.Accept, ct));

    /// <summary>Unfriend, or cancel my pending request.</summary>
    [HttpDelete("friends/{userId:guid}")]
    public async Task<IActionResult> RemoveFriend(Guid userId, CancellationToken ct) => FromResult(await social.RemoveFriendAsync(CurrentUserId, userId, ct));

    [HttpGet("blocks")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<BlockedUserDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Blocked(CancellationToken ct) => OkData(await social.BlockedAsync(CurrentUserId, ct));

    /// <summary>FR-S22: they can't message, challenge or find you; the friendship ends.</summary>
    [HttpPost("blocks/{userId:guid}")]
    public async Task<IActionResult> Block(Guid userId, CancellationToken ct) => FromResult(await social.BlockAsync(CurrentUserId, userId, ct));

    [HttpDelete("blocks/{userId:guid}")]
    public async Task<IActionResult> Unblock(Guid userId, CancellationToken ct) => FromResult(await social.UnblockAsync(CurrentUserId, userId, ct));

    /// <summary>FR-S23: report a learner or a message; it goes to the moderation queue.</summary>
    [HttpPost("reports")]
    public async Task<IActionResult> Report(ReportRequest request, CancellationToken ct) =>
        FromResult(await social.ReportAsync(CurrentUserId, request, ct), "Thanks — a moderator will look at it.");

    [HttpGet("me/privacy")]
    [ProducesResponseType<MyApiResponse<PrivacyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Privacy(CancellationToken ct) => OkData(await social.PrivacyAsync(CurrentUserId, ct));

    [HttpPut("me/privacy")]
    [ProducesResponseType<MyApiResponse<PrivacyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePrivacy(PrivacyDto request, CancellationToken ct) =>
        FromResult(await social.UpdatePrivacyAsync(CurrentUserId, request, ct));

    /// <summary>FR-M12: my strikes, suspension and any warning I still have to accept.</summary>
    [HttpGet("me/moderation")]
    [ProducesResponseType<MyApiResponse<MyModerationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyModeration(CancellationToken ct)
    {
        var s = await moderation.StatusAsync(CurrentUserId, ct);
        return OkData(new MyModerationDto(s.ActiveStrikes, s.SuspendedUntil, s.Blocked, s.PendingWarning));
    }

    [HttpPost("me/moderation/acknowledge")]
    public async Task<IActionResult> Acknowledge(CancellationToken ct) => FromResult(await moderation.AcknowledgeAsync(CurrentUserId, ct));
}

/// <summary>Chats and groups (FR-C01 .. FR-C32). Live events come over the /hubs/chat SignalR hub.</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class ConversationsController(IChatService chat) : ApiControllerBase
{
    [HttpGet("conversations")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<ConversationSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool archived = false, CancellationToken ct = default) =>
        OkData(await chat.ConversationsAsync(CurrentUserId, archived, ct));

    /// <summary>Opens (or creates) the one direct chat with a learner. Their privacy setting and blocks apply.</summary>
    [HttpPost("conversations/direct")]
    [ProducesResponseType<MyApiResponse<ConversationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Direct(CreateDirectRequest request, CancellationToken ct) =>
        FromResult(await chat.OpenDirectAsync(CurrentUserId, request.UserId, ct));

    [HttpPost("conversations/groups")]
    [ProducesResponseType<MyApiResponse<ConversationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateGroup(CreateGroupRequest request, CancellationToken ct) =>
        FromResult(await chat.CreateGroupAsync(CurrentUserId, request, ct));

    [HttpGet("conversations/{id:int}")]
    [ProducesResponseType<MyApiResponse<ConversationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) => FromResult(await chat.GetAsync(CurrentUserId, id, ct));

    /// <summary>Group settings (owner/admins) and my own mute/archive; leave a field null to keep it.</summary>
    [HttpPatch("conversations/{id:int}")]
    [ProducesResponseType<MyApiResponse<ConversationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, UpdateConversationRequest request, CancellationToken ct) =>
        FromResult(await chat.UpdateAsync(CurrentUserId, id, request, ct));

    /// <summary>Newest first pages: pass the smallest id you have as "before". Returned oldest → newest.</summary>
    [HttpGet("conversations/{id:int}/messages")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<MessageDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Messages(int id, [FromQuery] long? before, [FromQuery] int take = 40, CancellationToken ct = default) =>
        FromResult(await chat.MessagesAsync(CurrentUserId, id, before, take, ct));

    [HttpPost("conversations/{id:int}/messages")]
    [ProducesResponseType<MyApiResponse<MessageDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Send(int id, SendMessageRequest request, CancellationToken ct) =>
        FromResult(await chat.SendAsync(CurrentUserId, id, request, ct));

    [HttpPost("conversations/{id:int}/seen")]
    [ProducesResponseType<MyApiResponse<SeenDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Seen(int id, [FromQuery] long upTo, CancellationToken ct) =>
        FromResult(await chat.MarkSeenAsync(CurrentUserId, id, upTo, ct));

    /// <summary>FR-C08: hides the history for me only.</summary>
    [HttpPost("conversations/{id:int}/clear")]
    public async Task<IActionResult> Clear(int id, CancellationToken ct) => FromResult(await chat.ClearAsync(CurrentUserId, id, ct));

    [HttpPost("conversations/{id:int}/leave")]
    public async Task<IActionResult> Leave(int id, CancellationToken ct) => FromResult(await chat.LeaveAsync(CurrentUserId, id, ct));

    /// <summary>Public groups: join at once, or ask when the group needs approval (409 join_pending).</summary>
    [HttpPost("conversations/{id:int}/join")]
    [ProducesResponseType<MyApiResponse<ConversationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Join(int id, CancellationToken ct) => FromResult(await chat.JoinAsync(CurrentUserId, id, ct));

    [HttpPost("conversations/{id:int}/members")]
    public async Task<IActionResult> AddMembers(int id, AddMembersRequest request, CancellationToken ct) =>
        FromResult(await chat.AddMembersAsync(CurrentUserId, id, request.UserIds ?? [], ct));

    [HttpDelete("conversations/{id:int}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(int id, Guid userId, CancellationToken ct) =>
        FromResult(await chat.RemoveMemberAsync(CurrentUserId, id, userId, ct));

    [HttpPost("conversations/{id:int}/members/{userId:guid}/approve")]
    public async Task<IActionResult> Approve(int id, Guid userId, [FromQuery] bool approve = true, CancellationToken ct = default) =>
        FromResult(await chat.ApproveAsync(CurrentUserId, id, userId, approve, ct));

    /// <summary>Owner only. Making someone the owner hands the group over.</summary>
    [HttpPut("conversations/{id:int}/members/{userId:guid}/role")]
    public async Task<IActionResult> Role(int id, Guid userId, ChangeRoleRequest request, CancellationToken ct) =>
        FromResult(await chat.ChangeRoleAsync(CurrentUserId, id, userId, request.Role, ct));

    [HttpGet("groups/public")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<PublicGroupDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PublicGroups([FromQuery] string? q, [FromQuery] int? languageId, CancellationToken ct) =>
        OkData(await chat.PublicGroupsAsync(CurrentUserId, q, languageId, ct));

    /// <summary>FR-C10 / FR-C11: for me, or for everyone (sender within the time limit, group admins any time).</summary>
    [HttpDelete("messages/{id:long}")]
    public async Task<IActionResult> Delete(long id, [FromQuery] bool forEveryone = false, CancellationToken ct = default) =>
        FromResult(await chat.DeleteMessageAsync(CurrentUserId, id, forEveryone, ct));

    [HttpPut("messages/{id:long}/reaction")]
    public async Task<IActionResult> React(long id, ReactionRequest request, CancellationToken ct) =>
        FromResult(await chat.ReactAsync(CurrentUserId, id, request.Emoji, ct));

    [HttpDelete("messages/{id:long}/reaction")]
    public async Task<IActionResult> Unreact(long id, CancellationToken ct) => FromResult(await chat.ReactAsync(CurrentUserId, id, null, ct));

    [HttpGet("messages/{id:long}/seen-by")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<SeenByDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeenBy(long id, CancellationToken ct) => FromResult(await chat.SeenByAsync(CurrentUserId, id, ct));

    /// <summary>FR-C30: into my native language unless "to" is given. Counts toward the daily translation limit.</summary>
    [HttpPost("messages/{id:long}/translate")]
    [ProducesResponseType<MyApiResponse<TranslatedMessageDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Translate(long id, [FromQuery] string? to, CancellationToken ct) =>
        FromResult(await chat.TranslateAsync(CurrentUserId, id, to, ct));

    [HttpGet("messages/search")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<MessageSearchHitDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct) => OkData(await chat.SearchAsync(CurrentUserId, q, ct));
}
