using System.Net;
using System.Net.Http.Headers;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Learning;
using LinguaPath.Shared.Contracts.Practice;
using LinguaPath.Shared.Contracts.Translation;
using LinguaPath.Shared.Contracts.Words;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 6 acceptance: practice exercises, free talk, translation with limits and cache, My words, speaking pre-grading.</summary>
public sealed class PracticeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Seeded(int LanguageId, string Iso, int CourseId, int ReadId, int WriteId, int SpeakId, int ListenId, int DraftId);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "p" + Guid.NewGuid().ToString("N")[..5];
        var language = new Language
        {
            Name = "Lang " + iso, NativeName = iso, IsoCode = iso, SttLocale = iso + "-XX", Status = LanguageStatus.Published, MaxLevel = CefrLevel.A2
        };
        var course = new Course { Language = language, Title = "Practice course", StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A2, Status = ContentStatus.Published };
        var module = new CourseModule { Title = "Month one", Level = CefrLevel.A1, MonthNo = 1, SortOrder = 1 };
        module.Lectures.Add(new Lecture { Title = "L1", EstimatedMinutes = 20, IsCore = true, SortOrder = 1, Level = CefrLevel.A1, Status = ContentStatus.Published, BodyHtml = "<p>x</p>" });
        course.Modules.Add(module);
        db.Courses.Add(course);

        PracticeItem Item(PracticeType type, string text, ContentStatus status = ContentStatus.Published, CefrLevel level = CefrLevel.A1) => new()
        {
            Language = language, Type = type, Level = level, Title = $"{type} item", Text = text, Translation = "translation", Status = status,
            QuestionsJson = type != PracticeType.Read ? null : ReadingQuestions.Serialize(
            [
                new ReadingQuestions.Stored { Question = "Where?", Options = ["Home", "Market"], Correct = 1, Explanation = "Market." },
                new ReadingQuestions.Stored { Question = "Who?", Options = ["Ana", "Luis", "Eva"], Correct = 0 }
            ])
        };
        var read = Item(PracticeType.Read, "Ana walks to the market every day.");
        var write = Item(PracticeType.Write, "Write about your family.");
        var speak = Item(PracticeType.Speak, "Buenos días, ¿cómo estás?");
        var listen = Item(PracticeType.Listen, "El tren sale del andén número cuatro.");
        var draft = Item(PracticeType.Listen, "Not published yet.", ContentStatus.Draft);
        var harder = Item(PracticeType.Listen, "A harder sentence.", level: CefrLevel.B1);
        db.PracticeItems.AddRange(read, write, speak, listen, draft, harder);
        await db.SaveChangesAsync();
        return new Seeded(language.Id, iso, course.Id, read.Id, write.Id, speak.Id, listen.Id, draft.Id);
    }

    private async Task<(HttpClient Client, Guid UserId)> LearnerAsync(int? courseId = null)
    {
        var email = $"practice-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Practice Learner", email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        var authed = factory.CreateClient().WithBearer(auth.AccessToken);
        if (courseId is { } id)
        {
            var plan = new LearningPlanRequest(id, CefrLevel.A1, CefrLevel.A2, LearningGoal.Travel, null, 3, null, 10,
                StudyDays.Everyday, new TimeOnly(19, 0), FocusSkill.All);
            Assert.Equal(HttpStatusCode.OK, (await authed.PostJsonAsync("/api/v1/learner-courses", plan)).StatusCode);
        }
        using var scope = factory.Services.CreateScope();
        var userId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (authed, userId);
    }

    private async Task SetAsync(string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(key, value)).IsSuccess);
    }

    private static MultipartFormDataContent Audio(byte[] wav, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "audio", "answer.wav");
        return form;
    }

    private static byte[] Tone(double seconds, int rate = 44100) =>
        WavAudio.Write(Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)Math.Sin(2 * Math.PI * 300 * i / rate) * 0.3f).ToArray(), rate);

    // ------------------------------------------------------------------ practice

    [Fact]
    public async Task Summary_and_next_use_the_learners_course_language_and_level_and_hide_reading_answers()
    {
        var seeded = await SeedAsync();
        var (client, _) = await LearnerAsync(seeded.CourseId);

        var summary = await (await client.GetAsync("/api/v1/practice/summary")).ReadDataAsync<PracticeSummaryDto>();
        Assert.Equal(seeded.LanguageId, summary.LanguageId);
        Assert.Equal(CefrLevel.A1, summary.Level);
        Assert.Equal(2, summary.Types.Single(t => t.Type == PracticeType.Listen).Available); // the draft isn't counted
        Assert.NotEmpty(summary.Topics);
        Assert.Equal(100, summary.TranslationsLeftToday);

        var read = await (await client.GetAsync("/api/v1/practice/read/next")).ReadDataAsync<PracticeItemDto>();
        Assert.Equal(seeded.ReadId, read.Id);
        Assert.Equal(2, read.Questions.Count);
        Assert.Equal(seeded.Iso + "-XX", read.SpeechLocale);

        // Listening: the A1 sentence comes before the B1 one and drafts never appear.
        var listen = await (await client.GetAsync("/api/v1/practice/listen/next")).ReadDataAsync<PracticeItemDto>();
        Assert.Equal(seeded.ListenId, listen.Id);
        var skipped = await (await client.GetAsync($"/api/v1/practice/listen/next?skip={listen.Id}")).ReadDataAsync<PracticeItemDto>();
        Assert.NotEqual(seeded.DraftId, skipped.Id);
        Assert.Equal(CefrLevel.B1, skipped.Level);

        // Without a course there is no default language.
        var (newcomer, _) = await LearnerAsync();
        var none = await newcomer.GetAsync("/api/v1/practice/summary");
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Equal("no_language", (await none.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.OK, (await newcomer.GetAsync($"/api/v1/practice/summary?languageId={seeded.LanguageId}")).StatusCode);
    }

    [Fact]
    public async Task Reading_is_graded_listening_returns_a_diff_and_both_go_to_history()
    {
        var seeded = await SeedAsync();
        var (client, _) = await LearnerAsync(seeded.CourseId);

        var read = await (await client.PostJsonAsync($"/api/v1/practice/read/{seeded.ReadId}/answers", new ReadAnswersRequest([1, 2])))
            .ReadDataAsync<ReadResultDto>();
        Assert.Equal(50, read.Score);
        Assert.True(read.Results[0].Correct);
        Assert.Equal(0, read.Results[1].CorrectIndex);

        var listen = await (await client.PostJsonAsync($"/api/v1/practice/listen/{seeded.ListenId}/check",
            new ListenCheckRequest("el tren sale del anden numero cuatro"))).ReadDataAsync<ListenResultDto>();
        Assert.Equal("El tren sale del andén número cuatro.", listen.Sentence);
        Assert.Equal(2, listen.Words.Count(w => w.Kind == DiffKind.Wrong));
        Assert.Equal(71, listen.Score);

        // Drafts can't be answered.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostJsonAsync($"/api/v1/practice/listen/{seeded.DraftId}/check", new ListenCheckRequest("x"))).StatusCode);

        var history = await (await client.GetAsync("/api/v1/practice/history")).ReadDataAsync<List<PracticeHistoryDto>>();
        Assert.Equal(new[] { PracticeType.Listen, PracticeType.Read }, history.Select(h => h.Type).ToArray());

        // Done items move to the back of the queue.
        var next = await (await client.GetAsync("/api/v1/practice/summary")).ReadDataAsync<PracticeSummaryDto>();
        Assert.Equal(1, next.Types.Single(t => t.Type == PracticeType.Read).Done);
        Assert.Equal(50, next.Types.Single(t => t.Type == PracticeType.Read).AverageScore);
    }

    [Fact]
    public async Task Writing_check_and_conversation_use_the_ai_and_share_a_daily_limit()
    {
        var seeded = await SeedAsync();
        var (client, userId) = await LearnerAsync(seeded.CourseId);
        await SetAsync(SettingKeys.PracticeAiCallsPerDay, "2");
        try
        {
            var check = await (await client.PostJsonAsync("/api/v1/practice/write/check",
                new WriteCheckRequest(seeded.LanguageId, seeded.WriteId, null, "mi familia es grande y simpática."))).ReadDataAsync<WriteCheckDto>();
            Assert.True(check.IsSample);
            Assert.Equal("Mi", Assert.Single(check.Corrections).Corrected);
            Assert.Equal(1, check.RemainingToday);

            var reply = await (await client.PostJsonAsync("/api/v1/practice/conversation", new ConversationRequest(seeded.LanguageId, "Travel",
                [new ChatTurnDto("partner", "¡Hola! ¿Adónde quieres viajar?")], "quiero ir a México"))).ReadDataAsync<ConversationReplyDto>();
            Assert.Contains("Travel", reply.Reply);
            Assert.Equal("Quiero ir a México", reply.Correction);
            Assert.Equal(0, reply.RemainingToday);

            var limited = await client.PostJsonAsync("/api/v1/practice/write/check", new WriteCheckRequest(seeded.LanguageId, null, "Free", "Hola amigos."));
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal("practice_ai_limit", (await limited.ReadProblemAsync()).Code);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.AiUsageLogs.CountAsync(l => l.UserId == userId));
            Assert.Equal(2, await db.PracticeAttempts.CountAsync(a => a.UserId == userId && a.IsSample));
        }
        finally
        {
            await SetAsync(SettingKeys.PracticeAiCallsPerDay, "30");
        }

        var tooLong = await client.PostJsonAsync("/api/v1/practice/conversation",
            new ConversationRequest(seeded.LanguageId, "Travel", [], new string('a', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Speaking_takes_a_phone_wav_returns_word_scores_and_rejects_other_formats()
    {
        var seeded = await SeedAsync();
        var (client, userId) = await LearnerAsync(seeded.CourseId);

        var response = await client.PostAsync("/api/v1/practice/speak/assess",
            Audio(Tone(2), ("languageId", seeded.LanguageId.ToString()), ("itemId", seeded.SpeakId.ToString()), ("referenceText", "")));
        var result = await response.ReadDataAsync<SpeakResultDto>();
        Assert.True(result.IsSample);
        Assert.Equal(new[] { "Buenos", "días", "cómo", "estás" }, result.Words.Select(w => w.Word).ToArray());
        Assert.InRange(result.Score, 1, 100);

        // Any sentence can be practised without a library item.
        var free = await client.PostAsync("/api/v1/practice/speak/assess",
            Audio(Tone(1.5), ("languageId", seeded.LanguageId.ToString()), ("itemId", ""), ("referenceText", "Hola amigo")));
        Assert.Equal(2, (await free.ReadDataAsync<SpeakResultDto>()).Words.Count);

        var m4a = await client.PostAsync("/api/v1/practice/speak/assess",
            Audio("ftypM4A not a wav"u8.ToArray(), ("languageId", seeded.LanguageId.ToString()), ("itemId", seeded.SpeakId.ToString()), ("referenceText", "")));
        Assert.Equal(HttpStatusCode.BadRequest, m4a.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await db.PracticeAttempts.Where(a => a.UserId == userId && a.Type == PracticeType.Speak).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.EndsWith(".wav", a.AudioUrl));
    }

    // ------------------------------------------------------------------ translation

    [Fact]
    public async Task Translation_detects_the_language_keeps_history_uses_the_cache_and_enforces_the_daily_limit()
    {
        var (client, userId) = await LearnerAsync();

        var first = await (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest("Hello friend", null, "es"))).ReadDataAsync<TranslationDto>();
        Assert.Equal("en", first.From);
        Assert.True(first.Detected);
        Assert.True(first.IsSample);
        Assert.False(first.Cached);
        Assert.StartsWith("hola amigo", first.TranslatedText);
        Assert.Equal(99, first.RemainingToday);

        // A phrase someone already translated comes from the shared cache (FR-L104).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TranslationCache.Add(new TranslationCacheEntry
            {
                Hash = PracticeRules.CacheKey("en", "fr", "Good morning"), From = "en", To = "fr", SourceText = "Good morning",
                TranslatedText = "Bonjour", Provider = "AzureTranslator", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var cached = await (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest("  Good   morning ", "en", "fr"))).ReadDataAsync<TranslationDto>();
        Assert.True(cached.Cached);
        Assert.Equal("Bonjour", cached.TranslatedText);

        // Same language in and out: no provider call, still counted.
        var same = await (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest("Hola", "es", "es-MX"))).ReadDataAsync<TranslationDto>();
        Assert.Equal("Hola", same.TranslatedText);

        var history = await (await client.GetAsync("/api/v1/translate/history")).ReadDataAsync<List<TranslationDto>>();
        Assert.Equal(new[] { "Hola", "Good morning", "Hello friend" }, history.Select(h => h.SourceText).ToArray());

        // BR-15: limit per day; clearing history doesn't reset it.
        await SetAsync(SettingKeys.DailyTranslationLimit, "4");
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/v1/translate/history")).StatusCode);
            Assert.Empty(await (await client.GetAsync("/api/v1/translate/history")).ReadDataAsync<List<TranslationDto>>());
            Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest("dog", "en", "de"))).StatusCode);
            var blocked = await client.PostJsonAsync("/api/v1/translate", new TranslateRequest("cat", "en", "de"));
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.Equal("translation_limit", (await blocked.ReadProblemAsync()).Code);
        }
        finally
        {
            await SetAsync(SettingKeys.DailyTranslationLimit, "100");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(4, await db.TranslationEntries.CountAsync(t => t.UserId == userId));
        }

        // Validation: empty text, unknown target, too long.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest(" ", null, "es"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest("x", null, "e s"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/api/v1/translate", new TranslateRequest(new string('a', 501), null, "es"))).StatusCode);
    }

    [Fact]
    public async Task Voice_translation_transcribes_then_translates()
    {
        var (client, _) = await LearnerAsync();
        var response = await client.PostAsync("/api/v1/translate/speech", Audio(Tone(1.5), ("from", "en-US"), ("to", "es")));
        var result = await response.ReadDataAsync<SpeechTranslationDto>();
        Assert.Equal("hello, how are you?", result.Transcript);
        Assert.Equal("en", result.Translation.From);
        Assert.StartsWith("hola", result.Translation.TranslatedText);

        var languages = await (await client.GetAsync("/api/v1/translate/languages")).ReadDataAsync<List<TranslationLanguageDto>>();
        Assert.Contains(languages, l => l.Code == "en");
    }

    // ------------------------------------------------------------------ My words

    [Fact]
    public async Task Words_are_saved_once_reviewed_with_sm2_and_deleted()
    {
        var seeded = await SeedAsync();
        var (client, _) = await LearnerAsync(seeded.CourseId);

        var saved = await (await client.PostJsonAsync("/api/v1/words", new SaveWordRequest(seeded.LanguageId, "andén", "platform", "El tren sale del andén.")))
            .ReadDataAsync<UserWordDto>();
        Assert.True(saved.IsDue);
        var again = await (await client.PostJsonAsync("/api/v1/words", new SaveWordRequest(seeded.LanguageId, "andén", "railway platform", null)))
            .ReadDataAsync<UserWordDto>();
        Assert.Equal(saved.Id, again.Id);
        Assert.Equal("railway platform", again.Meaning);
        Assert.Equal("El tren sale del andén.", again.Example);

        await client.PostJsonAsync("/api/v1/words", new SaveWordRequest(seeded.LanguageId, "tren", "train", null));
        var due = await (await client.GetAsync($"/api/v1/words?languageId={seeded.LanguageId}&due=true")).ReadDataAsync<List<UserWordDto>>();
        Assert.Equal(2, due.Count);

        var reviewed = await (await client.PostJsonAsync($"/api/v1/words/{saved.Id}/review", new ReviewWordRequest(RecallGrade.Good)))
            .ReadDataAsync<UserWordDto>();
        Assert.False(reviewed.IsDue);
        Assert.Equal(1, reviewed.IntervalDays);

        var summary = await (await client.GetAsync($"/api/v1/words/summary?languageId={seeded.LanguageId}")).ReadDataAsync<WordsSummaryDto>();
        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Due);

        var search = await (await client.GetAsync("/api/v1/words?search=train")).ReadDataAsync<List<UserWordDto>>();
        Assert.Equal("tren", Assert.Single(search).Word);

        // Another learner can't touch my words.
        var (other, _) = await LearnerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/words/{saved.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/words/{saved.Id}")).StatusCode);
        Assert.Single(await (await client.GetAsync("/api/v1/words")).ReadDataAsync<List<UserWordDto>>());
    }

    // ------------------------------------------------------------------ admin library and AI drafts, speaking pre-grade

    [Fact]
    public async Task Ai_drafts_practice_items_that_go_through_review_before_learners_see_them()
    {
        var seeded = await SeedAsync();
        int requestId;
        using (var scope = factory.Services.CreateScope())
        {
            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            var created = await ai.RequestPracticeAsync(new PracticeAiInput
            {
                LanguageId = seeded.LanguageId, Type = PracticeType.Read, Level = CefrLevel.A2, Count = 2, Topic = "Food"
            });
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error!.Message : null);
            requestId = created.Value;
        }
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAiJobProcessor>();
            while (await jobs.RunNextAsync("test")) { }
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(AiRequestStatus.Completed, (await db.AiGenerationRequests.SingleAsync(r => r.Id == requestId)).Status);
            var drafts = await db.PracticeItems.Where(p => p.LanguageId == seeded.LanguageId && p.Source == ContentSource.Ai).ToListAsync();
            Assert.Equal(2, drafts.Count);
            Assert.All(drafts, d => Assert.Equal(ContentStatus.Draft, d.Status));
            Assert.All(drafts, d => Assert.Equal(3, ReadingQuestions.Parse(d.QuestionsJson).Count));
            Assert.Equal(2, await db.AiGeneratedItems.CountAsync(i => i.RequestId == requestId && i.EntityType == ReviewEntityType.PracticeItem));

            var admin = scope.ServiceProvider.GetRequiredService<IPracticeAdminService>();
            Assert.True((await admin.PublishAsync(drafts[0].Id)).IsFailure); // not approved yet
            var list = await admin.ListAsync(new PracticeItemFilter(seeded.LanguageId, PracticeType.Read, ContentStatus.Draft, null));
            Assert.Equal(2, list.Count);
        }
    }

    [Fact]
    public async Task Admin_validation_keeps_speaking_short_and_reading_questions_well_formed()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IPracticeAdminService>();

        var tooLong = await admin.SaveAsync(new PracticeItemInput
        {
            LanguageId = seeded.LanguageId, Type = PracticeType.Speak, Title = "Long", Text = new string('a', 301)
        });
        Assert.True(tooLong.IsFailure);

        var badIndex = await admin.SaveAsync(new PracticeItemInput
        {
            LanguageId = seeded.LanguageId, Type = PracticeType.Read, Title = "Passage", Text = "Text.",
            Questions = [new PracticeQuestionInput { Question = "Q?", Options = "a\nb", Correct = 3 }]
        });
        Assert.True(badIndex.IsFailure);

        var ok = await admin.SaveAsync(new PracticeItemInput
        {
            LanguageId = seeded.LanguageId, Type = PracticeType.Read, Title = "Passage", Text = "Text.",
            Questions = [new PracticeQuestionInput { Question = "Q?", Options = "a\nb\n", Correct = 2 }, new PracticeQuestionInput()]
        });
        Assert.True(ok.IsSuccess);
        var loaded = await admin.GetAsync(ok.Value);
        Assert.Equal("a\nb", Assert.Single(loaded!.Questions).Options);
        Assert.Equal(2, loaded.Questions[0].Correct);
    }

    [Fact]
    public async Task Exam_speaking_answers_get_a_pronunciation_suggestion_but_sample_scores_never_grade_automatically()
    {
        var seeded = await SeedAsync();
        var (_, userId) = await LearnerAsync(seeded.CourseId);
        int attemptId, requestId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var url = await storage.SaveAsync(new MemoryStream(Tone(2)), "answers", ".wav", "audio/wav");

            var speak = new Question
            {
                LanguageId = seeded.LanguageId, CourseId = seeded.CourseId, Type = QuestionType.Speak, Skill = Skill.Speaking, Level = CefrLevel.A1,
                Stem = "Say this.", Prompt = "Buenos días", Points = 4, Status = ContentStatus.Published
            };
            db.Questions.Add(speak);
            await db.SaveChangesAsync();
            var attempt = new QuizAttempt
            {
                UserId = userId, CourseId = seeded.CourseId, Type = QuizType.Monthly, Title = "Exam", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                SubmittedAt = DateTimeOffset.UtcNow, PassMark = 50, Status = AttemptStatus.PendingReview, MaxScore = 4,
                Answers = { new AttemptAnswer { QuestionId = speak.Id, SortOrder = 1, MaxScore = 4, NeedsReview = true, AnsweredAt = DateTimeOffset.UtcNow, AudioUrl = url } }
            };
            db.QuizAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
            var request = new AiGenerationRequest
            {
                Type = AiRequestType.WritingGrading, Status = AiRequestStatus.Queued, AttemptId = attemptId, CourseId = seeded.CourseId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.AiGenerationRequests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IAiJobProcessor>();
            while (await jobs.RunNextAsync("test")) { }
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var answer = await db.AttemptAnswers.SingleAsync(a => a.AttemptId == attemptId);
            Assert.NotNull(answer.AiScore);
            Assert.Equal(0d, answer.AiConfidence);
            Assert.True(answer.NeedsReview);
            Assert.Contains("Pronunciation", answer.AiFeedback);
            Assert.Contains("Sample scores", answer.AiFeedback);
            var request = await db.AiGenerationRequests.SingleAsync(r => r.Id == requestId);
            Assert.Equal(AiRequestStatus.Completed, request.Status);
            Assert.Contains("1 speaking", request.Summary);
        }
    }
}
