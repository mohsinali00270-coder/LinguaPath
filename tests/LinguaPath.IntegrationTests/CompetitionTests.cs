using System.Net;
using System.Net.Http.Json;
using LinguaPath.Application.Features.Competitions;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Chat;
using LinguaPath.Shared.Contracts.Competitions;
using LinguaPath.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 9 acceptance: challenges (live and own time), quick match and bot, group battles, tournaments, leagues, limits and sharing.</summary>
public sealed class CompetitionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Learner(HttpClient Client, Guid UserId, string Name, string Email);

    /// <summary>A language of its own per test (so quick matches never cross tests) with 20 published multiple-choice questions.</summary>
    private async Task<int> LanguageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "c" + string.Concat(Guid.NewGuid().ToByteArray().Take(5).Select(b => (char)('a' + b % 26)));
        var language = new Language { Name = "Quiz " + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Published, MaxLevel = CefrLevel.B1 };
        db.Languages.Add(language);
        for (var i = 1; i <= 20; i++)
        {
            var q = new Question
            {
                Language = language, Type = QuestionType.MultipleChoice, Level = CefrLevel.A1, Stem = $"Question {i}?", Status = ContentStatus.Published
            };
            q.Options.Add(new QuestionOption { Text = $"Right {i}", IsCorrect = true, SortOrder = 1 });
            q.Options.Add(new QuestionOption { Text = $"Wrong {i}", IsCorrect = false, SortOrder = 2 });
            db.Questions.Add(q);
        }
        await db.SaveChangesAsync();
        return language.Id;
    }

    private async Task<Learner> LearnerAsync(string? name = null)
    {
        var email = $"compete-{Guid.NewGuid():N}@test.local";
        name ??= "Player " + email[8..13];
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest(name, email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        using var scope = factory.Services.CreateScope();
        var userId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return new Learner(factory.CreateClient().WithBearer(auth.AccessToken), userId, name, email);
    }

    private async Task<(int Right, int Wrong)> OptionsAsync(int competitionId, int index)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var qid = await db.CompetitionQuestions.Where(q => q.CompetitionId == competitionId && q.SortOrder == index).Select(q => q.QuestionId).SingleAsync();
        var options = await db.QuestionOptions.Where(o => o.QuestionId == qid).ToListAsync();
        return (options.Single(o => o.IsCorrect).Id, options.First(o => !o.IsCorrect).Id);
    }

    private static async Task<CompetitionDto> GetAsync(Learner l, int id) =>
        await (await l.Client.GetAsync($"/api/v1/competitions/{id}")).ReadDataAsync<CompetitionDto>();

    private async Task<AnswerAckDto> AnswerAsync(Learner l, int id, int index, bool correct)
    {
        var (right, wrong) = await OptionsAsync(id, index);
        var r = await l.Client.PostJsonAsync($"/api/v1/competitions/{id}/answers", new SubmitAnswerRequest(index, correct ? right : wrong, null));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.ReadDataAsync<AnswerAckDto>();
    }

    /// <summary>Plays a live match to the end: for each question every player answers in turn.</summary>
    private async Task PlayLiveAsync(int id, int count, params (Learner Player, Func<int, bool> Correct)[] players)
    {
        for (var i = 0; i < count; i++)
            foreach (var (player, correct) in players)
            {
                var ack = await AnswerAsync(player, id, i, correct(i));
                Assert.True(ack.Accepted, ack.Message);
            }
    }

    private static async Task<CompetitionDto> ChallengeAsync(Learner from, Learner to, int languageId, bool live = true, int count = 5)
    {
        var r = await from.Client.PostJsonAsync("/api/v1/competitions/challenge", new ChallengeRequest(to.UserId, languageId, CefrLevel.A1, null, count, 10, live));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.ReadDataAsync<CompetitionDto>();
    }

    [Fact]
    public async Task Live_challenge_same_questions_faster_wins_and_points()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var c = await ChallengeAsync(a, b, lang);
        Assert.Equal(CompetitionStatus.Pending, c.Summary.Status);

        // The opponent sees the challenge waiting for them.
        var waiting = await (await b.Client.GetAsync("/api/v1/competitions/active")).ReadDataAsync<List<CompetitionSummaryDto>>();
        Assert.Contains(waiting, w => w.Id == c.Summary.Id && w.NeedsMyAction);

        var accepted = await (await b.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/accept", null)).ReadDataAsync<CompetitionDto>();
        Assert.Equal(CompetitionStatus.Running, accepted.Summary.Status);
        var qa = (await GetAsync(a, c.Summary.Id)).CurrentQuestion!;
        var qb = (await GetAsync(b, c.Summary.Id)).CurrentQuestion!;
        // FR-Q02: same question, same order; no correct answer in what players get (FR-Q05).
        Assert.Equal(qa.Stem, qb.Stem);
        Assert.Equal(0, qa.Index);

        // Both always right; A answers first each time, so A is faster.
        await PlayLiveAsync(c.Summary.Id, 5, (a, _ => true), (b, _ => true));

        var result = await (await a.Client.GetAsync($"/api/v1/competitions/{c.Summary.Id}/result")).ReadDataAsync<CompetitionResultDto>();
        Assert.Equal(CompetitionStatus.Finished, result.Competition.Summary.Status);
        var me = result.Competition.Players.Single(p => p.IsMe);
        var other = result.Competition.Players.Single(p => !p.IsMe);
        Assert.Equal(CompetitionResult.Win, me.Result);
        Assert.Equal(CompetitionResult.Loss, other.Result);
        Assert.True(me.Score >= other.Score);
        Assert.True(me.TotalAnswerMs <= other.TotalAnswerMs);
        Assert.Equal(30, me.PointsAwarded);
        Assert.Equal(5, other.PointsAwarded);
        Assert.Equal(5, result.Review.Count);
        Assert.True(result.CanRematch);

        var stats = await (await a.Client.GetAsync("/api/v1/competitions/stats")).ReadDataAsync<CompetitionStatsDto>();
        Assert.Equal(1, stats.Won);
        Assert.Equal(1016, stats.Ratings.Single(r => r.LanguageId == lang).Rating);
    }

    [Fact]
    public async Task Own_time_challenge_hides_scores_until_both_played()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var c = await ChallengeAsync(a, b, lang, live: false);
        Assert.True(c.CanPlay);

        // A plays now: each answer comes back with its result and the next question.
        var playing = await (await a.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/play", null)).ReadDataAsync<CompetitionDto>();
        Assert.Equal(0, playing.CurrentQuestion!.Index);
        for (var i = 0; i < 5; i++)
        {
            var ack = await AnswerAsync(a, c.Summary.Id, i, correct: i < 3);
            Assert.NotNull(ack.Result);
            Assert.Equal(i < 4, ack.Next is not null);
            Assert.Equal(i == 4, ack.Finished);
        }

        await b.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/accept", null);
        var forB = await GetAsync(b, c.Summary.Id);
        Assert.True(forB.CanPlay);
        Assert.Equal(0, forB.Players.Single(p => !p.IsMe).Score);   // hidden until the end

        await b.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/play", null);
        for (var i = 0; i < 5; i++) await AnswerAsync(b, c.Summary.Id, i, correct: true);

        var done = await GetAsync(b, c.Summary.Id);
        Assert.Equal(CompetitionStatus.Finished, done.Summary.Status);
        Assert.Equal(CompetitionResult.Win, done.Players.Single(p => p.IsMe).Result);
        Assert.True(done.Players.Single(p => !p.IsMe).Score > 0);
    }

    [Fact]
    public async Task Late_and_duplicate_answers_are_refused()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var c = await ChallengeAsync(a, b, lang);
        await b.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/accept", null);

        Assert.True((await AnswerAsync(a, c.Summary.Id, 0, true)).Accepted);
        var again = await AnswerAsync(a, c.Summary.Id, 0, false);
        Assert.False(again.Accepted);
        Assert.Equal("already_answered", again.Code);
        var ahead = await AnswerAsync(a, c.Summary.Id, 3, true);
        Assert.Equal("wrong_question", ahead.Code);
    }

    [Fact]
    public async Task Quick_match_pairs_learners_and_leaving_is_a_loss()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var first = await (await a.Client.PostJsonAsync("/api/v1/competitions/quick-match", new QuickMatchRequest(lang, CefrLevel.A1, 5, 10)))
            .ReadDataAsync<CompetitionDto>();
        Assert.Equal(CompetitionStatus.Waiting, first.Summary.Status);
        // ± 1 level matches.
        var second = await (await b.Client.PostJsonAsync("/api/v1/competitions/quick-match", new QuickMatchRequest(lang, CefrLevel.A2, 5, 10)))
            .ReadDataAsync<CompetitionDto>();
        Assert.Equal(first.Summary.Id, second.Summary.Id);
        Assert.Equal(CompetitionStatus.Running, second.Summary.Status);
        Assert.Equal(2, second.Players.Count);

        Assert.Equal(HttpStatusCode.OK, (await a.Client.PostAsync($"/api/v1/competitions/{first.Summary.Id}/leave", null)).StatusCode);
        for (var i = 0; i < 5; i++) await AnswerAsync(b, first.Summary.Id, i, correct: false);

        var done = await GetAsync(a, first.Summary.Id);
        Assert.Equal(CompetitionStatus.Finished, done.Summary.Status);
        var me = done.Players.Single(p => p.IsMe);
        Assert.Equal(CompetitionResult.Loss, me.Result);
        Assert.Equal(0, me.PointsAwarded);
        Assert.Equal(CompetitionResult.Win, done.Players.Single(p => !p.IsMe).Result);
    }

    [Fact]
    public async Task Bot_match_is_practice_only()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var search = await (await a.Client.PostJsonAsync("/api/v1/competitions/quick-match", new QuickMatchRequest(lang, CefrLevel.A1, 5, 10)))
            .ReadDataAsync<CompetitionDto>();
        var bot = await (await a.Client.PostAsync($"/api/v1/competitions/{search.Summary.Id}/bot", null)).ReadDataAsync<CompetitionDto>();
        Assert.False(bot.Summary.IsRanked);
        Assert.Contains(bot.Players, p => p.IsBot);
        for (var i = 0; i < 5; i++) await AnswerAsync(a, search.Summary.Id, i, correct: true);

        var done = await GetAsync(a, search.Summary.Id);
        Assert.Equal(CompetitionStatus.Finished, done.Summary.Status);
        Assert.Equal(5, done.Players.Single(p => p.IsMe).PointsAwarded); // practice points, not 30
        var stats = await (await a.Client.GetAsync("/api/v1/competitions/stats")).ReadDataAsync<CompetitionStatsDto>();
        Assert.Equal(0, stats.Played);
    }

    [Fact]
    public async Task Win_points_vs_the_same_opponent_stop_after_three_a_day()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var awarded = new List<int>();
        for (var round = 0; round < 4; round++)
        {
            var c = await ChallengeAsync(a, b, lang);
            await b.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/accept", null);
            await PlayLiveAsync(c.Summary.Id, 5, (a, _ => true), (b, _ => false));
            awarded.Add((await GetAsync(a, c.Summary.Id)).Players.Single(p => p.IsMe).PointsAwarded);
        }
        Assert.Equal(new[] { 30, 30, 30, 0 }, awarded.ToArray());
    }

    [Fact]
    public async Task Challenges_respect_privacy()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        await b.Client.PutAsJsonAsync("/api/v1/me/privacy", new PrivacyDto(AudienceSetting.Everyone, AudienceSetting.Nobody, true, true, true), ApiFactory.Json);
        var r = await a.Client.PostJsonAsync("/api/v1/competitions/challenge", new ChallengeRequest(b.UserId, lang, CefrLevel.A1, null, 5, 10, true));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("privacy_restricted", (await r.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Group_battle_with_live_leaderboard_and_top_three_bonus()
    {
        var lang = await LanguageAsync();
        var owner = await LearnerAsync();
        var members = new List<Learner>();
        for (var i = 0; i < 3; i++) members.Add(await LearnerAsync());
        var group = await (await owner.Client.PostJsonAsync("/api/v1/conversations/groups",
            new CreateGroupRequest("Quiz club", null, "trophy", lang, false, members.Select(m => m.UserId).ToList()))).ReadDataAsync<ConversationDto>();

        // Members can't start battles.
        var refused = await members[0].Client.PostJsonAsync($"/api/v1/conversations/{group.Id}/competitions", new GroupBattleRequest(null, CefrLevel.A1, null, 5, 10, 60));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var lobby = await (await owner.Client.PostJsonAsync($"/api/v1/conversations/{group.Id}/competitions",
            new GroupBattleRequest(null, CefrLevel.A1, null, 5, 10, 60))).ReadDataAsync<CompetitionDto>();
        Assert.Equal(CompetitionStatus.Waiting, lobby.Summary.Status);

        // A card in the group invites members.
        var messages = await (await members[0].Client.GetAsync($"/api/v1/conversations/{group.Id}/messages")).ReadDataAsync<List<MessageDto>>();
        Assert.Contains(messages, m => m.Type == MessageType.CompetitionCard && m.CompetitionId == lobby.Summary.Id);

        var forMember = await GetAsync(members[0], lobby.Summary.Id);
        Assert.True(forMember.CanJoin);
        foreach (var m in members) Assert.Equal(HttpStatusCode.OK, (await m.Client.PostAsync($"/api/v1/competitions/{lobby.Summary.Id}/join", null)).StatusCode);
        var started = await (await owner.Client.PostAsync($"/api/v1/competitions/{lobby.Summary.Id}/start", null)).ReadDataAsync<CompetitionDto>();
        Assert.Equal(CompetitionStatus.Running, started.Summary.Status);

        // Owner gets everything right, member 0 four, member 1 two, member 2 none.
        AnswerAckDto? last = null;
        for (var i = 0; i < 5; i++)
        {
            await AnswerAsync(owner, lobby.Summary.Id, i, true);
            await AnswerAsync(members[0], lobby.Summary.Id, i, i < 4);
            await AnswerAsync(members[1], lobby.Summary.Id, i, i < 2);
            last = await AnswerAsync(members[2], lobby.Summary.Id, i, false);
            // FR-Q10: the leaderboard comes with every question's result.
            Assert.Equal(4, last.Result!.Leaderboard.Count);
        }
        Assert.True(last!.Finished);

        var done = await GetAsync(owner, lobby.Summary.Id);
        var byUser = done.Players.ToDictionary(p => p.UserId!.Value);
        Assert.Equal(30, byUser[owner.UserId].PointsAwarded);
        Assert.Equal(20, byUser[members[0].UserId].PointsAwarded);
        Assert.Equal(10, byUser[members[1].UserId].PointsAwarded);
        Assert.Equal(5, byUser[members[2].UserId].PointsAwarded);
    }

    [Fact]
    public async Task Tournament_rounds_prizes_and_disqualification()
    {
        var lang = await LanguageAsync();
        var players = new List<Learner>();
        for (var i = 0; i < 4; i++) players.Add(await LearnerAsync());

        int id;
        using (var scope = factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<ITournamentAdminService>();
            var now = DateTimeOffset.UtcNow;
            var saved = await admin.SaveAsync(null, new TournamentForm("Friday Cup", null, lang, CefrLevel.A1, null, now.AddMinutes(-1), now.AddHours(1),
                2, 5, 10, null, 300, 200, 100));
            Assert.True(saved.IsSuccess);
            id = saved.Value;
            Assert.True((await admin.PublishAsync(id)).IsSuccess);
        }

        foreach (var p in players) Assert.Equal(HttpStatusCode.OK, (await p.Client.PostAsync($"/api/v1/tournaments/{id}/join", null)).StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<ITournamentAdminService>().StartNowAsync(id)).IsSuccess);

        // Round 1: player i gets i+1 answers right, so players 3 and 2 go through.
        var t = await (await players[0].Client.GetAsync($"/api/v1/tournaments/{id}")).ReadDataAsync<TournamentDto>();
        Assert.Equal(TournamentStatus.Running, t.Status);
        var round1 = t.MyCompetitionId!.Value;
        for (var q = 0; q < 5; q++)
            for (var p = 0; p < 4; p++)
                await AnswerAsync(players[p], round1, q, q <= p);

        var after = await (await players[3].Client.GetAsync($"/api/v1/tournaments/{id}")).ReadDataAsync<TournamentDto>();
        Assert.Equal(2, after.CurrentRound);
        var round2 = after.MyCompetitionId!.Value;
        Assert.NotEqual(round1, round2);
        var out1 = await (await players[0].Client.GetAsync($"/api/v1/tournaments/{id}")).ReadDataAsync<TournamentDto>();
        Assert.Null(out1.MyCompetitionId);

        // The final: player 2 beats player 3.
        for (var q = 0; q < 5; q++)
        {
            await AnswerAsync(players[2], round2, q, true);
            await AnswerAsync(players[3], round2, q, false);
        }
        var final = await (await players[2].Client.GetAsync($"/api/v1/tournaments/{id}")).ReadDataAsync<TournamentDto>();
        Assert.Equal(TournamentStatus.Finished, final.Status);
        Assert.Equal(1, final.MyPlace);
        Assert.Equal(300, final.Standings.Single(s => s.UserId == players[2].UserId).PrizePoints);
        Assert.Equal(200, final.Standings.Single(s => s.UserId == players[3].UserId).PrizePoints);
        Assert.Equal(100, final.Standings.Single(s => s.UserId == players[1].UserId).PrizePoints);

        // FR-Q11: disqualify the winner → their prize is revoked and everyone moves up.
        using (var scope = factory.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<ITournamentAdminService>().DisqualifyAsync(id, players[2].UserId, "Cheating")).IsSuccess);
        var fixedUp = await (await players[3].Client.GetAsync($"/api/v1/tournaments/{id}")).ReadDataAsync<TournamentDto>();
        Assert.Equal(1, fixedUp.MyPlace);
        Assert.Equal(300, fixedUp.Standings.Single(s => s.UserId == players[3].UserId).PrizePoints);
        Assert.Equal(100, fixedUp.Standings.Single(s => s.UserId == players[0].UserId).PrizePoints);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.PointTransactions.AnyAsync(x => x.UserId == players[2].UserId && x.Action == PointAction.TournamentPrize && !x.IsRevoked));
            Assert.Equal(300, await db.PointTransactions.Where(x => x.UserId == players[3].UserId && x.Action == PointAction.TournamentPrize && !x.IsRevoked)
                .SumAsync(x => x.Points));
        }
    }

    [Fact]
    public async Task Share_result_into_chat_and_league_counts_the_points()
    {
        var lang = await LanguageAsync();
        var a = await LearnerAsync();
        var b = await LearnerAsync();
        var c = await ChallengeAsync(a, b, lang);
        await b.Client.PostAsync($"/api/v1/competitions/{c.Summary.Id}/accept", null);
        await PlayLiveAsync(c.Summary.Id, 5, (a, _ => true), (b, _ => false));

        var chat = await (await a.Client.PostJsonAsync("/api/v1/conversations/direct", new CreateDirectRequest(b.UserId))).ReadDataAsync<ConversationDto>();
        Assert.Equal(HttpStatusCode.OK, (await a.Client.PostJsonAsync($"/api/v1/competitions/{c.Summary.Id}/share", new ShareRequest(chat.Id))).StatusCode);
        var messages = await (await b.Client.GetAsync($"/api/v1/conversations/{chat.Id}/messages")).ReadDataAsync<List<MessageDto>>();
        var card = messages.Single(m => m.Type == MessageType.CompetitionCard);
        Assert.Equal(c.Summary.Id, card.CompetitionId);
        Assert.Contains(a.Name, card.Text);

        // FR-S08: the 30 win points count in this week's league.
        var league = await (await a.Client.GetAsync("/api/v1/leagues/current")).ReadDataAsync<LeagueDto>();
        Assert.Equal(LeagueTier.Bronze, league.Tier);
        Assert.True(league.MyPoints >= 30);
        Assert.Contains(league.Members, m => m.IsMe);
    }
}
