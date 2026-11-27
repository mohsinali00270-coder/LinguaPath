using System.Net;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Assessment;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Content;
using LinguaPath.Shared.Contracts.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 4 acceptance: daily quiz, review list, monthly exam with grading, placement test, marks, question import.</summary>
public sealed class AssessmentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Seeded(int LanguageId, int CourseId, int[] LectureIds, int[] DailyQuestionIds, int WritingQuestionId, int ExamId);

    /// <summary>
    /// Month 1: six 30-minute lectures (so with 10 minutes a day the month's exam isn't due yet).
    /// Lecture 1 has three multiple-choice questions and a fill-in; there is a writing question and a month 1 exam
    /// (2 random vocabulary questions + the writing question); placement questions at A1 and A2.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "q" + Guid.NewGuid().ToString("N")[..6];
        var language = new Language { Name = "Lang " + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Published, MaxLevel = CefrLevel.A2 };
        var course = new Course { Language = language, Title = "Quiz course", StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A2, Status = ContentStatus.Published };
        var module = new CourseModule { Title = "Month one", Level = CefrLevel.A1, MonthNo = 1, SortOrder = 1 };
        for (var i = 1; i <= 6; i++)
            module.Lectures.Add(new Lecture
            {
                Title = $"L{i}", EstimatedMinutes = 30, IsCore = true, SortOrder = i, Level = CefrLevel.A1,
                Status = ContentStatus.Published, BodyHtml = "<p>x</p>"
            });
        course.Modules.Add(module);
        db.Courses.Add(course);
        await db.SaveChangesAsync();

        var first = module.Lectures.OrderBy(l => l.SortOrder).First();
        Question Mc(string stem) => new()
        {
            Language = language, CourseId = course.Id, ModuleId = module.Id, LectureId = first.Id, Type = QuestionType.MultipleChoice,
            Skill = Skill.Vocabulary, Level = CefrLevel.A1, Stem = stem, Status = ContentStatus.Published, Explanation = "Because.",
            Options = { new QuestionOption { Text = "right", IsCorrect = true, SortOrder = 1 }, new QuestionOption { Text = "wrong", SortOrder = 2 } }
        };
        var daily = new[] { Mc("Q1"), Mc("Q2"), Mc("Q3") }.ToList();
        daily.Add(new Question
        {
            Language = language, CourseId = course.Id, ModuleId = module.Id, LectureId = first.Id, Type = QuestionType.FillBlank,
            Skill = Skill.Grammar, Level = CefrLevel.A1, Stem = "Buenos ___", Status = ContentStatus.Published,
            Options = { new QuestionOption { Text = "días", IsCorrect = true, SortOrder = 1 } }
        });
        var writing = new Question
        {
            Language = language, CourseId = course.Id, ModuleId = module.Id, LectureId = first.Id, Type = QuestionType.Writing,
            Skill = Skill.Writing, Level = CefrLevel.A1, Stem = "Introduce yourself.", Points = 4, Status = ContentStatus.Published
        };
        var placement = new[] { CefrLevel.A1, CefrLevel.A1, CefrLevel.A2, CefrLevel.A2 }.Select((level, i) => new Question
        {
            Language = language, Type = QuestionType.TrueFalse, Skill = Skill.Grammar, Level = level, Stem = $"P{i}",
            Status = ContentStatus.Published,
            Options = { new QuestionOption { Text = "True", IsCorrect = true, SortOrder = 1 }, new QuestionOption { Text = "False", SortOrder = 2 } }
        }).ToList();
        db.Questions.AddRange(daily);
        db.Questions.Add(writing);
        db.Questions.AddRange(placement);

        var exam = new Quiz
        {
            CourseId = course.Id, Type = QuizType.Monthly, Title = "Month 1 exam", MonthNo = 1, TimeLimitMinutes = 30, PassMark = 60,
            Status = ContentStatus.Published,
            Sections =
            {
                new QuizSection { Title = "Vocabulary", Skill = Skill.Vocabulary, QuestionCount = 2, SortOrder = 1 },
                new QuizSection { Title = "Writing", SelectionMode = SelectionMode.Fixed, QuestionCount = 1, SortOrder = 2, Questions = { new QuizQuestion { Question = writing, SortOrder = 1 } } }
            }
        };
        db.Quizzes.Add(exam);
        await db.SaveChangesAsync();

        return new Seeded(language.Id, course.Id, module.Lectures.OrderBy(l => l.SortOrder).Select(l => l.Id).ToArray(),
            daily.Select(q => q.Id).ToArray(), writing.Id, exam.Id);
    }

    private async Task<HttpClient> LearnerAsync()
    {
        var email = $"quiz-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Quiz Learner", email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        return factory.CreateClient().WithBearer(auth.AccessToken);
    }

    private static async Task<int> EnrolAsync(HttpClient client, int courseId)
    {
        var plan = new LearningPlanRequest(courseId, CefrLevel.A1, CefrLevel.A2, LearningGoal.Travel, null, 3, null, 10,
            StudyDays.Everyday, new TimeOnly(19, 0), FocusSkill.All);
        var response = await client.PostJsonAsync("/api/v1/learner-courses", plan);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadDataAsync<EnrolResponse>()).Course.Id;
    }

    private static async Task CompleteAsync(HttpClient client, int learnerCourseId, int lectureId)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/lectures/{lectureId}?learnerCourseId={learnerCourseId}")).StatusCode);
        var done = await client.PostJsonAsync($"/api/v1/lectures/{lectureId}/complete", new CompleteLectureRequest(learnerCourseId, 900));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    private async Task<Dictionary<int, int>> CorrectOptionsAsync(IEnumerable<int> questionIds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = questionIds.ToList();
        return await db.QuestionOptions.Where(o => ids.Contains(o.QuestionId) && o.IsCorrect)
            .GroupBy(o => o.QuestionId).Select(g => new { g.Key, Id = g.Min(o => o.Id) }).ToDictionaryAsync(x => x.Key, x => x.Id);
    }

    private static async Task<AnswerFeedbackDto> AnswerAsync(HttpClient client, int attemptId, SaveAnswerRequest request)
    {
        var response = await client.PostJsonAsync($"/api/v1/attempts/{attemptId}/answers", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadDataAsync<AnswerFeedbackDto>();
    }

    [Fact]
    public async Task Daily_quiz_unlocks_after_a_lecture_gives_instant_feedback_and_counts_once()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);

        var before = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/daily-quiz")).ReadDataAsync<DailyQuizDto>();
        Assert.Equal(DailyQuizState.NotReady, before.State);

        await CompleteAsync(client, lc, seeded.LectureIds[0]);
        var ready = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/daily-quiz")).ReadDataAsync<DailyQuizDto>();
        Assert.Equal(DailyQuizState.Ready, ready.State);
        Assert.Equal(4, ready.QuestionCount);

        var attempt = await (await client.PostAsync($"/api/v1/learner-courses/{lc}/daily-quiz/start", null)).ReadDataAsync<AttemptDto>();
        Assert.True(attempt.InstantFeedback);
        Assert.Null(attempt.SecondsLeft);
        Assert.Equal(4, attempt.Questions.Count);
        Assert.DoesNotContain(attempt.Questions, q => q.Type == QuestionType.Writing);

        // Resuming returns the same attempt.
        var again = await (await client.PostAsync($"/api/v1/learner-courses/{lc}/daily-quiz/start", null)).ReadDataAsync<AttemptDto>();
        Assert.Equal(attempt.Id, again.Id);

        var correct = await CorrectOptionsAsync(seeded.DailyQuestionIds);
        var mcs = attempt.Questions.Where(q => q.Type == QuestionType.MultipleChoice).ToList();
        var right = await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(mcs[0].QuestionId, null, [correct[mcs[0].QuestionId]], null, 5));
        Assert.True(right.IsCorrect);
        Assert.Equal("Because.", right.Explanation);

        var wrongOption = mcs[1].Options.First(o => o.Id != correct[mcs[1].QuestionId]).Id;
        var wrong = await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(mcs[1].QuestionId, null, [wrongOption], null, 5));
        Assert.False(wrong.IsCorrect);
        Assert.Equal("right", wrong.CorrectAnswer);

        // The first answer is final in a daily quiz.
        var changed = await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(mcs[1].QuestionId, null, [correct[mcs[1].QuestionId]], null, 5));
        Assert.False(changed.IsCorrect);

        var fill = attempt.Questions.Single(q => q.Type == QuestionType.FillBlank);
        Assert.True((await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(fill.QuestionId, "Días", null, null, 5))).IsCorrect);

        // Submit with one question left open: 2 of 4 correct.
        var result = await (await client.PostAsync($"/api/v1/attempts/{attempt.Id}/submit", null)).ReadDataAsync<AttemptResultDto>();
        Assert.Equal(AttemptStatus.Graded, result.Status);
        Assert.Equal(2, result.Correct);
        Assert.Equal(50, result.Percentage);
        Assert.False(result.IsPractice);

        var done = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/daily-quiz")).ReadDataAsync<DailyQuizDto>();
        Assert.Equal(DailyQuizState.Done, done.State);

        var second = await client.PostAsync($"/api/v1/learner-courses/{lc}/daily-quiz/start", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("daily_quiz_done", (await second.ReadProblemAsync()).Code);

        var practice = await (await client.PostAsync($"/api/v1/learner-courses/{lc}/daily-quiz/start?practice=true", null)).ReadDataAsync<AttemptDto>();
        Assert.True(practice.IsPractice);

        // FR-L54: the two missed questions are on the review list for tomorrow.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.ReviewCards.CountAsync(c => c.LearnerCourseId == lc));

        // Marks: the counted quiz only (practice is still open, so not listed).
        var marks = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/marks")).ReadDataAsync<MarksDto>();
        Assert.Equal(50, marks.DailyAverage);
        Assert.Equal(50, marks.CourseGrade);
        Assert.Single(marks.Rows);
        Assert.Contains(marks.Skills, s => s.Skill == Skill.Vocabulary);
    }

    [Fact]
    public async Task Monthly_exam_unlocks_hides_answers_waits_for_grading_then_counts()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);

        var exams = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/exams")).ReadDataAsync<List<ExamSummaryDto>>();
        var exam = Assert.Single(exams);
        Assert.Equal(ExamAvailability.Locked, exam.Availability);
        Assert.Equal(3, exam.QuestionCount);
        var locked = await client.PostAsync($"/api/v1/learner-courses/{lc}/exams/{seeded.ExamId}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);

        // 80% of month 1 (5 of 6 lectures) unlocks it.
        foreach (var id in seeded.LectureIds.Take(5)) await CompleteAsync(client, lc, id);
        exam = (await (await client.GetAsync($"/api/v1/learner-courses/{lc}/exams")).ReadDataAsync<List<ExamSummaryDto>>())[0];
        Assert.Equal(ExamAvailability.Available, exam.Availability);

        var attempt = await (await client.PostAsync($"/api/v1/learner-courses/{lc}/exams/{seeded.ExamId}/start", null)).ReadDataAsync<AttemptDto>();
        Assert.False(attempt.InstantFeedback);
        Assert.InRange(attempt.SecondsLeft ?? 0, 1700, 1800);
        Assert.Equal(3, attempt.Questions.Count);
        Assert.Equal(new[] { "Vocabulary", "Vocabulary", "Writing" }, attempt.Questions.Select(q => q.Section));

        // Starting again resumes the same attempt.
        var resumed = await (await client.PostAsync($"/api/v1/learner-courses/{lc}/exams/{seeded.ExamId}/start", null)).ReadDataAsync<AttemptDto>();
        Assert.Equal(attempt.Id, resumed.Id);

        var correct = await CorrectOptionsAsync(seeded.DailyQuestionIds);
        foreach (var q in attempt.Questions.Where(q => q.Type == QuestionType.MultipleChoice))
        {
            var saved = await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(q.QuestionId, null, [correct[q.QuestionId]], null, 10));
            Assert.Null(saved.IsCorrect);       // exams don't reveal answers
            Assert.Null(saved.CorrectAnswer);
        }
        foreach (var q in attempt.Questions.Where(q => q.Type == QuestionType.FillBlank))
            await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(q.QuestionId, "días", null, null, 10));
        await AnswerAsync(client, attempt.Id, new SaveAnswerRequest(seeded.WritingQuestionId, "Me llamo Ana. Soy de Lahore.", null, null, 60));

        // Answers survive a "crash": the attempt comes back with them.
        var reopened = await (await client.GetAsync($"/api/v1/attempts/{attempt.Id}")).ReadDataAsync<AttemptDto>();
        Assert.All(reopened.Questions, q => Assert.NotNull(q.Answer));

        var result = await (await client.PostAsync($"/api/v1/attempts/{attempt.Id}/submit", null)).ReadDataAsync<AttemptResultDto>();
        Assert.Equal(AttemptStatus.PendingReview, result.Status);
        Assert.Equal(1, result.PendingReview);
        Assert.False(result.IsPassed);

        // A reviewer grades the writing answer (FR-A81).
        using (var scope = factory.Services.CreateScope())
        {
            var grading = scope.ServiceProvider.GetRequiredService<IGradingService>();
            var item = (await grading.QueueAsync()).Single(i => i.AttemptId == attempt.Id);
            Assert.Equal(4, item.MaxScore);
            Assert.True((await grading.GradeAsync(item.AnswerId, 9, null)).IsFailure);   // more than the maximum
            Assert.True((await grading.GradeAsync(item.AnswerId, 3, "Good start.")).IsSuccess);
        }

        var graded = await (await client.GetAsync($"/api/v1/attempts/{attempt.Id}/result")).ReadDataAsync<AttemptResultDto>();
        Assert.Equal(AttemptStatus.Graded, graded.Status);
        Assert.Equal(5, graded.Score);           // 1 + 1 + 3
        Assert.Equal(6, graded.MaxScore);
        Assert.Equal(83, graded.Percentage);
        Assert.True(graded.IsPassed);

        var review = await (await client.GetAsync($"/api/v1/attempts/{attempt.Id}/review")).ReadDataAsync<AttemptReviewDto>();
        Assert.Contains(review.Answers, a => a.Feedback == "Good start.");

        exam = (await (await client.GetAsync($"/api/v1/learner-courses/{lc}/exams")).ReadDataAsync<List<ExamSummaryDto>>())[0];
        Assert.Equal(ExamAvailability.Passed, exam.Availability);
        Assert.Equal(83, exam.BestPercentage);

        var marks = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/marks")).ReadDataAsync<MarksDto>();
        Assert.Equal(83, marks.ExamAverage);
        Assert.Equal(83, marks.CourseGrade);

        // A reviewer can change a mark with a reason; it is recorded (BR-14).
        using (var scope = factory.Services.CreateScope())
        {
            var grading = scope.ServiceProvider.GetRequiredService<IGradingService>();
            var detail = await grading.AttemptDetailAsync(attempt.Id);
            var writingAnswer = detail!.Answers.Single(a => a.Type == QuestionType.Writing);
            Assert.True((await grading.OverrideAsync(writingAnswer.AnswerId, 1, "")).IsFailure);
            Assert.True((await grading.OverrideAsync(writingAnswer.AnswerId, 1, "Too short on second look.")).IsSuccess);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.MarkOverrides.CountAsync(m => m.AttemptAnswerId == writingAnswer.AnswerId));
        }
        var overridden = await (await client.GetAsync($"/api/v1/attempts/{attempt.Id}/result")).ReadDataAsync<AttemptResultDto>();
        Assert.Equal(50, overridden.Percentage);
        Assert.False(overridden.IsPassed);
    }

    [Fact]
    public async Task Placement_test_suggests_a_level()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();

        var attempt = await (await client.PostAsync($"/api/v1/courses/{seeded.CourseId}/placement-test", null)).ReadDataAsync<AttemptDto>();
        Assert.Equal(QuizType.Placement, attempt.Type);
        Assert.Equal(4, attempt.Questions.Count);  // 2 per level, A1 and A2

        // Right at A1, wrong at A2 → A2 is where to start.
        Dictionary<int, CefrLevel> levels;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = attempt.Questions.Select(q => q.QuestionId).ToList();
            levels = await db.Questions.Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id, q => q.Level);
        }
        var correct = await CorrectOptionsAsync(attempt.Questions.Select(q => q.QuestionId));
        foreach (var q in attempt.Questions)
        {
            var right = levels[q.QuestionId] == CefrLevel.A1;
            var request = q.Type == QuestionType.FillBlank
                ? new SaveAnswerRequest(q.QuestionId, right ? "días" : "noches", null, null, 3)
                : new SaveAnswerRequest(q.QuestionId, null,
                    [right ? correct[q.QuestionId] : q.Options.First(o => o.Id != correct[q.QuestionId]).Id], null, 3);
            await AnswerAsync(client, attempt.Id, request);
        }

        var result = await (await client.PostAsync($"/api/v1/attempts/{attempt.Id}/submit", null)).ReadDataAsync<AttemptResultDto>();
        Assert.Equal(CefrLevel.A2, result.SuggestedLevel);
    }

    [Fact]
    public async Task Question_bank_validates_types_and_round_trips_through_excel()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var questions = scope.ServiceProvider.GetRequiredService<IQuestionAdminService>();

        var bad = await questions.SaveAsync(new QuestionInput
        {
            LanguageId = seeded.LanguageId, Type = QuestionType.FillBlank, Stem = "No gap here", Options = [new() { Text = "x" }]
        });
        Assert.True(bad.IsFailure);
        Assert.Contains("Stem", bad.Error!.Details!.Keys);

        var file = await questions.ExportAsync(new QuestionFilter(seeded.LanguageId, null, null, null, null, null, null, null, null));
        var import = await questions.ImportAsync(seeded.LanguageId, new MemoryStream(file));
        Assert.True(import.IsSuccess, import.IsFailure ? import.Error!.Message : null);
        Assert.Empty(import.Value.Errors);
        Assert.Equal(0, import.Value.Created);
        Assert.Equal(9, import.Value.Updated);    // 4 daily + writing + 4 placement

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fill = await db.Questions.Include(q => q.Options).SingleAsync(q => q.Id == seeded.DailyQuestionIds[3]);
        Assert.Equal("días", Assert.Single(fill.Options).Text);
        Assert.Equal(2, fill.Version);
    }
}
