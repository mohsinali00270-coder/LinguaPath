using System.Net;
using System.Net.Http.Json;
using LinguaPath.Application.Features.Moderation;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Chat;
using LinguaPath.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 8 acceptance: friends, blocks, privacy, chat, deletes, groups, the link/word filter, AI scan and the strike ladder.</summary>
public sealed class SocialChatTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Learner(HttpClient Client, Guid UserId, string Name, string Email);

    private async Task<Learner> LearnerAsync(string? name = null)
    {
        var email = $"social-{Guid.NewGuid():N}@test.local";
        name ??= "Chatter " + email[7..12];
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest(name, email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        using var scope = factory.Services.CreateScope();
        var userId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return new Learner(factory.CreateClient().WithBearer(auth.AccessToken), userId, name, email);
    }

    private static async Task<ConversationDto> DirectAsync(Learner from, Learner to)
    {
        var r = await from.Client.PostJsonAsync("/api/v1/conversations/direct", new CreateDirectRequest(to.UserId));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.ReadDataAsync<ConversationDto>();
    }

    private static Task<HttpResponseMessage> SendAsync(Learner from, int conversationId, string text, long? replyTo = null) =>
        from.Client.PostJsonAsync($"/api/v1/conversations/{conversationId}/messages", new SendMessageRequest(text, replyTo, Guid.NewGuid().ToString("N")));

    private static async Task<List<MessageDto>> MessagesAsync(Learner who, int conversationId) =>
        await (await who.Client.GetAsync($"/api/v1/conversations/{conversationId}/messages")).ReadDataAsync<List<MessageDto>>();

    [Fact]
    public async Task Friend_requests_blocks_and_profiles()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();

        var search = await (await a.Client.GetAsync($"/api/v1/users/search?q={Uri.EscapeDataString(b.Name)}")).ReadDataAsync<List<UserCardDto>>();
        Assert.Contains(search, u => u.UserId == b.UserId);
        // FR-S20: never searchable by email.
        Assert.Empty(await (await a.Client.GetAsync($"/api/v1/users/search?q={Uri.EscapeDataString(b.Email)}")).ReadDataAsync<List<UserCardDto>>());

        var request = await (await a.Client.PostJsonAsync("/api/v1/friends/requests", new SendFriendRequest(b.UserId))).ReadDataAsync<FriendRequestDto>();
        var incoming = await (await b.Client.GetAsync("/api/v1/friends")).ReadDataAsync<FriendsDto>();
        Assert.Contains(incoming.Requests, r => r.Id == request.Id && r.Incoming);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PostJsonAsync($"/api/v1/friends/requests/{request.Id}/respond", new RespondFriendRequest(true))).StatusCode);

        var friends = await (await a.Client.GetAsync("/api/v1/friends")).ReadDataAsync<FriendsDto>();
        Assert.Contains(friends.Friends, f => f.UserId == b.UserId);
        var profile = await (await a.Client.GetAsync($"/api/v1/users/{b.UserId}/profile")).ReadDataAsync<PublicProfileDto>();
        Assert.Equal(RelationState.Friends, profile.Relation);
        Assert.True(profile.CanMessage);

        // FR-S22: blocking ends the friendship, hides me from them and stops messages.
        Assert.Equal(HttpStatusCode.OK, (await a.Client.PostAsync($"/api/v1/blocks/{b.UserId}", null)).StatusCode);
        Assert.Empty((await (await a.Client.GetAsync("/api/v1/friends")).ReadDataAsync<FriendsDto>()).Friends);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/users/{a.UserId}/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostJsonAsync("/api/v1/conversations/direct", new CreateDirectRequest(a.UserId))).StatusCode);
        Assert.Contains(await (await a.Client.GetAsync("/api/v1/blocks")).ReadDataAsync<List<BlockedUserDto>>(), x => x.UserId == b.UserId);
    }

    [Fact]
    public async Task Privacy_friends_only_messages()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var privacy = new PrivacyDto(AudienceSetting.FriendsOnly, AudienceSetting.Everyone, true, true, true);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PutAsJsonAsync("/api/v1/me/privacy", privacy, ApiFactory.Json)).StatusCode);

        var refused = await a.Client.PostJsonAsync("/api/v1/conversations/direct", new CreateDirectRequest(b.UserId));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("privacy_restricted", (await refused.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Direct_chat_delivery_seen_and_deletes()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);
        Assert.Equal(chat.Id, (await DirectAsync(b, a)).Id); // one chat per pair

        var first = await (await SendAsync(a, chat.Id, "Hola, ¿qué tal?")).ReadDataAsync<MessageDto>();
        Assert.Equal(DeliveryState.Sent, first.State);
        var reply = await (await SendAsync(b, chat.Id, "Muy bien, gracias", first.Id)).ReadDataAsync<MessageDto>();
        Assert.Equal(first.Id, reply.ReplyTo!.MessageId);

        var list = await (await b.Client.GetAsync("/api/v1/conversations")).ReadDataAsync<List<ConversationSummaryDto>>();
        Assert.Equal(a.Name, list.Single(c => c.Id == chat.Id).Title);

        // FR-C03: seen once B reads it.
        await b.Client.PostAsync($"/api/v1/conversations/{chat.Id}/seen?upTo={reply.Id}", null);
        var forA = await MessagesAsync(a, chat.Id);
        Assert.Equal(DeliveryState.Seen, forA.Single(m => m.Id == first.Id).State);

        // FR-C10: delete for me hides it only for me.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.DeleteAsync($"/api/v1/messages/{first.Id}")).StatusCode);
        Assert.DoesNotContain(await MessagesAsync(b, chat.Id), m => m.Id == first.Id);
        Assert.Contains(await MessagesAsync(a, chat.Id), m => m.Id == first.Id && m.Text != null);

        // FR-C11/12: delete for everyone; the text never comes back from the API.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Client.DeleteAsync($"/api/v1/messages/{reply.Id}?forEveryone=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.DeleteAsync($"/api/v1/messages/{reply.Id}?forEveryone=true")).StatusCode);
        var gone = (await MessagesAsync(a, chat.Id)).Single(m => m.Id == reply.Id);
        Assert.True(gone.IsDeleted);
        Assert.Null(gone.Text);

        // Reactions.
        var third = await (await SendAsync(a, chat.Id, "¡Hasta luego!")).ReadDataAsync<MessageDto>();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PutAsJsonAsync($"/api/v1/messages/{third.Id}/reaction", new ReactionRequest("👍"), ApiFactory.Json)).StatusCode);
        Assert.Equal(1, (await MessagesAsync(a, chat.Id)).Single(m => m.Id == third.Id).Reactions.Single(r => r.Emoji == "👍").Count);
    }

    [Fact]
    public async Task Mild_words_are_masked_for_everyone()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);
        var sent = await (await SendAsync(a, chat.Id, "Damn, this grammar is hard")).ReadDataAsync<MessageDto>();
        Assert.True(sent.WasMasked);
        var seen = (await MessagesAsync(b, chat.Id)).Single(m => m.Id == sent.Id);
        Assert.StartsWith("D***", seen.Text);
    }

    [Fact]
    public async Task Links_are_rejected_and_five_attempts_add_a_strike_warning()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);
        string[] attempts = ["see example.com", "mail me ali@mail.com", "call +92 300 1234567", "add me on snapchat", "example dot com"];
        foreach (var text in attempts)
        {
            var r = await SendAsync(a, chat.Id, text);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Empty(await MessagesAsync(b, chat.Id));

        var status = await (await a.Client.GetAsync("/api/v1/me/moderation")).ReadDataAsync<MyModerationDto>();
        Assert.Equal(1, status.ActiveStrikes);
        Assert.NotNull(status.PendingWarning);

        // FR-M12: chatting waits until the warning is accepted.
        var blocked = await SendAsync(a, chat.Id, "Sorry!");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("warning_pending", (await blocked.ReadProblemAsync()).Code);
        await a.Client.PostAsync("/api/v1/me/moderation/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, chat.Id, "Sorry!")).StatusCode);
    }

    [Fact]
    public async Task Strike_ladder_suspends_then_blocks()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);

        // 1st strike: abusive word → refused, warning.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(a, chat.Id, "you are so stupid")).StatusCode);
        await a.Client.PostAsync("/api/v1/me/moderation/acknowledge", null);

        // 2nd strike: chat suspended.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(a, chat.Id, "what an idiot")).StatusCode);
        var suspended = await SendAsync(a, chat.Id, "hello again");
        Assert.Equal(HttpStatusCode.Conflict, suspended.StatusCode);
        Assert.Equal("chat_suspended", (await suspended.ReadProblemAsync()).Code);
        // Learning still works while suspended.
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync("/api/v1/points")).StatusCode);

        // 3rd strike (a moderator, since chat is suspended) → permanent block; the token stops working at once.
        using (var scope = factory.Services.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<IModerationService>()
                .AddStrikeAsync(a.UserId, ModerationSeverity.Abusive, "Third strike", null, "moderator@test.local");
            Assert.Equal(LadderStep.Block, outcome.Step);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Client.GetAsync("/api/v1/points")).StatusCode);
        var login = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login", new LoginRequest(a.Email, Password, null));
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);

        // BR-27: the same email can't sign up again.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.BlockedIdentities.AnyAsync(x => x.UserId == a.UserId && x.NormalizedEmail != null));
        }
    }

    [Fact]
    public async Task Severe_words_block_at_once()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(a, chat.Id, "just kill yourself")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Client.GetAsync("/api/v1/conversations")).StatusCode);
    }

    [Fact]
    public async Task Ai_scan_removes_abuse_the_word_list_missed()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);
        var sent = await (await SendAsync(a, chat.Id, "honestly nobody likes you")).ReadDataAsync<MessageDto>();
        var fine = await (await SendAsync(a, chat.Id, "see you in class tomorrow")).ReadDataAsync<MessageDto>();

        using (var scope = factory.Services.CreateScope())
        {
            var moderation = scope.ServiceProvider.GetRequiredService<IModerationService>();
            Assert.True(await moderation.ScanAsync(sent.Id));
            Assert.True(await moderation.ScanAsync(fine.Id));
            Assert.False(await moderation.ScanAsync(fine.Id)); // already scanned
        }

        var forB = await MessagesAsync(b, chat.Id);
        Assert.True(forB.Single(m => m.Id == sent.Id).IsDeleted);
        Assert.Null(forB.Single(m => m.Id == sent.Id).Text);
        Assert.False(forB.Single(m => m.Id == fine.Id).IsDeleted);
        Assert.Equal(1, (await (await a.Client.GetAsync("/api/v1/me/moderation")).ReadDataAsync<MyModerationDto>()).ActiveStrikes);
    }

    [Fact]
    public async Task Reports_go_to_the_moderation_queue()
    {
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var chat = await DirectAsync(a, b);
        var m = await (await SendAsync(a, chat.Id, "this is borderline rude")).ReadDataAsync<MessageDto>();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PostJsonAsync("/api/v1/reports", new ReportRequest(null, m.Id, ReportReason.Abuse, "rude"))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ev = await db.ModerationEvents.SingleAsync(e => e.UserId == a.UserId && e.DetectedBy == ModerationDetector.Report);
        Assert.Equal(ModerationOutcome.Open, ev.ActionTaken);
        Assert.Equal("this is borderline rude", ev.Text);
    }

    [Fact]
    public async Task Groups_roles_announcement_mode_and_owner_transfer()
    {
        var owner = await LearnerAsync();
        var member = await LearnerAsync();
        var group = await (await owner.Client.PostJsonAsync("/api/v1/conversations/groups",
            new CreateGroupRequest("Spanish practice", "Let's talk", "book", null, true, [member.UserId]))).ReadDataAsync<ConversationDto>();
        Assert.Equal(MemberRole.Owner, group.MyRole);
        Assert.Equal(2, group.Members.Count);

        // Links in group names are refused.
        var bad = await owner.Client.PostJsonAsync("/api/v1/conversations/groups", new CreateGroupRequest("join example.com", null, null, null, false, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Members can't change settings.
        var patch = new UpdateConversationRequest(null, null, null, null, null, true, null, null, null, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PatchAsJsonAsync($"/api/v1/conversations/{group.Id}", patch, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PatchAsJsonAsync($"/api/v1/conversations/{group.Id}", patch, ApiFactory.Json)).StatusCode);

        // FR-C21 announcement mode.
        var refused = await SendAsync(member, group.Id, "Can I talk?");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(owner, group.Id, "Welcome @" + member.Name)).StatusCode);
        var messages = await MessagesAsync(member, group.Id);
        Assert.Contains(messages, m => m.Type == MessageType.System);
        Assert.Contains(messages, m => m.Mentions.Contains(member.UserId));

        // Public group search and join.
        var third = await LearnerAsync();
        var found = await (await third.Client.GetAsync("/api/v1/groups/public?q=Spanish%20practice")).ReadDataAsync<List<PublicGroupDto>>();
        Assert.Contains(found, g => g.Id == group.Id);
        Assert.Equal(HttpStatusCode.OK, (await third.Client.PostAsync($"/api/v1/conversations/{group.Id}/join", null)).StatusCode);

        // FR-C24: the owner leaves → the next member becomes owner.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PostAsync($"/api/v1/conversations/{group.Id}/leave", null)).StatusCode);
        var after = await (await member.Client.GetAsync($"/api/v1/conversations/{group.Id}")).ReadDataAsync<ConversationDto>();
        Assert.Equal(MemberRole.Owner, after.MyRole);
    }
}
