using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Learning;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Assessment;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Assessment;

/// <summary>Learner side of assessment: daily quiz, monthly exams, placement test, answering, results and marks.</summary>
public interface IAssessmentService
{
    Task<Result<DailyQuizDto>> GetDailyQuizAsync(Guid userId, int learnerCourseId, CancellationToken ct = default);
    Task<Result<AttemptDto>> StartDailyQuizAsync(Guid userId, int learnerCourseId, bool practice, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ExamSummaryDto>>> ListExamsAsync(Guid userId, int learnerCourseId, CancellationToken ct = default);
    Task<Result<AttemptDto>> StartExamAsync(Guid userId, int learnerCourseId, int quizId, CancellationToken ct = default);
    Task<Result<AttemptDto>> StartPlacementAsync(Guid userId, int courseId, CancellationToken ct = default);
    Task<Result<AttemptDto>> GetAttemptAsync(Guid userId, int attemptId, CancellationToken ct = default);
    Task<Result<AnswerFeedbackDto>> SaveAnswerAsync(Guid userId, int attemptId, SaveAnswerRequest request, CancellationToken ct = default);
    Task<Result<AnswerFeedbackDto>> SaveAudioAnswerAsync(Guid userId, int attemptId, int questionId, Stream audio, string extension,
        string contentType, CancellationToken ct = default);
    Task<Result<AttemptResultDto>> SubmitAsync(Guid userId, int attemptId, CancellationToken ct = default);
    Task<Result> LogEventAsync(Guid userId, int attemptId, AttemptEventRequest request, CancellationToken ct = default);
    Task<Result<AttemptResultDto>> GetResultAsync(Guid userId, int attemptId, CancellationToken ct = default);
    Task<Result<AttemptReviewDto>> GetReviewAsync(Guid userId, int attemptId, CancellationToken ct = default);
    Task<Result<MarksDto>> GetMarksAsync(Guid userId, int learnerCourseId, CancellationToken ct = default);

    /// <summary>Closes attempts whose time ran out (exam timer, daily quiz midnight). Run by a background job.</summary>
    Task<int> CloseExpiredAsync(CancellationToken ct = default);
}

internal sealed class AssessmentService(
    IApplicationDbContext db,
    ISettingsService settings,
    IFileStorage storage,
    Progress.IProgressService progress,
    TimeProvider clock) : IAssessmentService
{
    private const int PlacementMinutes = 30;

    // ================================================================== daily quiz (FR-L50 .. FR-L54)

    public async Task<Result<DailyQuizDto>> GetDailyQuizAsync(Guid userId, int learnerCourseId, CancellationToken ct = default)
    {
        var lc = await FindLearnerCourseAsync(userId, learnerCourseId, ct);
        if (lc is null) return Error.NotFound("Course");
        var today = await TodayAsync(userId, ct);
        var due = await db.ReviewCards.CountAsync(c => c.LearnerCourseId == lc.Id && c.DueDate <= today, ct);

        var attempt = await LatestDailyAsync(lc.Id, today, ct);
        if (attempt is not null)
        {
            if (attempt.Status == AttemptStatus.InProgress && !IsExpired(attempt))
                return new DailyQuizDto(lc.Id, today, DailyQuizState.InProgress, attempt.Id, attempt.Answers.Count, null,
                    $"You've answered {attempt.Answers.Count(a => a.AnsweredAt != null)} of {attempt.Answers.Count}. Keep going!", due);
            if (attempt.Status == AttemptStatus.InProgress) await FinalizeAsync(attempt, ct);
            var result = await BuildResultAsync(attempt, ct);
            return new DailyQuizDto(lc.Id, today, DailyQuizState.Done, attempt.Id, attempt.Answers.Count, result,
                $"Today's quiz: {result.Percentage}%. Come back tomorrow for a new one.", due);
        }

        if (lc.Status != LearnerCourseStatus.Active)
            return new DailyQuizDto(lc.Id, today, DailyQuizState.NotReady, null, 0, null, "Your course is paused. Resume it to get daily quizzes.", due);

        var completed = await CompletedLecturesAsync(lc.Id, ct);
        if (completed.Count == 0 && due == 0)
            return new DailyQuizDto(lc.Id, today, DailyQuizState.NotReady, null, 0, null, "Complete your first lecture to unlock the daily quiz.", due);

        var plan = await PlanDailyAsync(lc, completed, today, ct);
        return plan.Count == 0
            ? new DailyQuizDto(lc.Id, today, DailyQuizState.NoQuestions, null, 0, null, "No questions for your lectures yet. Check back soon.", due)
            : new DailyQuizDto(lc.Id, today, DailyQuizState.Ready, null, plan.Count, null, $"{plan.Count} questions from your recent lectures.", due);
    }

    public async Task<Result<AttemptDto>> StartDailyQuizAsync(Guid userId, int learnerCourseId, bool practice, CancellationToken ct = default)
    {
        var lc = await FindLearnerCourseAsync(userId, learnerCourseId, ct);
        if (lc is null) return Error.NotFound("Course");
        if (lc.Status != LearnerCourseStatus.Active) return Error.Conflict("course_paused", "Resume your course to take the daily quiz.");
        var today = await TodayAsync(userId, ct);

        var existing = await LatestDailyAsync(lc.Id, today, ct);
        if (existing is { Status: AttemptStatus.InProgress } && !IsExpired(existing))
            return await BuildAttemptAsync(existing, ct);
        if (existing is { Status: AttemptStatus.InProgress }) await FinalizeAsync(existing, ct);
        if (existing is not null && !practice)
            return Error.Conflict("daily_quiz_done", "You've already taken today's quiz. You can practise again; it won't change your marks.");

        var completed = await CompletedLecturesAsync(lc.Id, ct);
        var plan = await PlanDailyAsync(lc, completed, today, ct);
        if (plan.Count == 0)
            return Error.Conflict("no_questions", "There are no questions for your lectures yet.");

        var now = clock.GetUtcNow();
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        var attempt = new QuizAttempt
        {
            UserId = userId,
            CourseId = lc.CourseId,
            LearnerCourseId = lc.Id,
            Type = QuizType.Daily,
            Title = practice ? $"Practice quiz · {today:d MMM}" : $"Daily quiz · {today:d MMM}",
            ForDate = today,
            AttemptNo = (existing?.AttemptNo ?? 0) + 1,
            StartedAt = now,
            ExpiresAt = GradeRules.NextMidnightUtc(now, user),
            PassMark = await settings.GetIntAsync(SettingKeys.DailyQuizPassMark, 50, ct),
            IsPractice = practice
        };
        AddQuestions(attempt, plan.Select(q => (q, (string?)null)));
        db.QuizAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return await BuildAttemptAsync(attempt, ct);
    }

    /// <summary>FR-A63: recent lectures, last week, then due review items; topped up from all completed lectures.</summary>
    private async Task<List<Question>> PlanDailyAsync(LearnerCourse lc, IReadOnlyList<(int LectureId, DateTimeOffset CompletedAt)> completed,
        DateOnly today, CancellationToken ct)
    {
        var recentCount = await settings.GetIntAsync(SettingKeys.DailyQuizFromTodayCount, 3, ct);
        var weekCount = await settings.GetIntAsync(SettingKeys.DailyQuizFromWeekCount, 3, ct);
        var reviewCount = await settings.GetIntAsync(SettingKeys.DailyQuizReviewCount, 2, ct);
        var total = Math.Max(1, recentCount + weekCount + reviewCount);

        var now = clock.GetUtcNow();
        var lectureIds = completed.Select(c => c.LectureId).ToList();
        var recent = completed.Where(c => c.CompletedAt >= now.AddDays(-2)).Select(c => c.LectureId).ToHashSet();
        var week = completed.Where(c => c.CompletedAt >= now.AddDays(-7)).Select(c => c.LectureId).ToHashSet();

        List<Question> pool = await PublishedQuestions()
            .Where(q => q.LectureId != null && lectureIds.Contains(q.LectureId.Value)
                        && q.Type != QuestionType.Writing && q.Type != QuestionType.Speak)
            .ToListAsync(ct);

        var dueIds = await db.ReviewCards.Where(c => c.LearnerCourseId == lc.Id && c.DueDate <= today)
            .OrderBy(c => c.DueDate).ThenBy(c => c.Box).Select(c => c.QuestionId).Take(reviewCount).ToListAsync(ct);
        List<Question> reviewQs = await PublishedQuestions().Where(q => dueIds.Contains(q.Id)).ToListAsync(ct);

        var chosen = new List<Question>();
        void Take(IEnumerable<Question> source, int count)
        {
            foreach (var q in Shuffle(source.Where(q => chosen.All(c => c.Id != q.Id))).Take(count)) chosen.Add(q);
        }

        Take(pool.Where(q => recent.Contains(q.LectureId!.Value)), recentCount);
        Take(pool.Where(q => week.Contains(q.LectureId!.Value)), weekCount + (recentCount - Math.Min(recentCount, chosen.Count)));
        Take(reviewQs, reviewCount);
        Take(pool, total - chosen.Count);
        return chosen;
    }

    /// <summary>Today's attempt: an open one first, else the one that counts, else the latest practice.</summary>
    private async Task<QuizAttempt?> LatestDailyAsync(int learnerCourseId, DateOnly today, CancellationToken ct)
    {
        var attempts = await WithAnswers()
            .Where(a => a.LearnerCourseId == learnerCourseId && a.Type == QuizType.Daily && a.ForDate == today)
            .OrderByDescending(a => a.Id).ToListAsync(ct);
        return attempts.FirstOrDefault(a => a.Status == AttemptStatus.InProgress)
               ?? attempts.FirstOrDefault(a => !a.IsPractice)
               ?? attempts.FirstOrDefault();
    }

    private IQueryable<QuizAttempt> WithAnswers() =>
        db.QuizAttempts.Include(a => a.Answers).ThenInclude(a => a.Question).ThenInclude(q => q.Options);

    private async Task<List<(int LectureId, DateTimeOffset CompletedAt)>> CompletedLecturesAsync(int learnerCourseId, CancellationToken ct)
    {
        var rows = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => i.LearnerCourseId == learnerCourseId && i.State == RoadmapItemState.Completed && i.CompletedAt != null)
            .Select(i => new { i.LectureId, i.CompletedAt }).ToListAsync(ct);
        return rows.Select(r => (r.LectureId, r.CompletedAt!.Value)).ToList();
    }

    // ================================================================== monthly exams (FR-L60 .. FR-L65)

    public async Task<Result<IReadOnlyList<ExamSummaryDto>>> ListExamsAsync(Guid userId, int learnerCourseId, CancellationToken ct = default)
    {
        var lc = await FindLearnerCourseAsync(userId, learnerCourseId, ct);
        if (lc is null) return Error.NotFound("Course");
        await CloseExpiredForUserAsync(userId, ct);

        var exams = await db.Quizzes.AsNoTracking().Include(q => q.Sections).ThenInclude(s => s.Questions)
            .Where(q => q.CourseId == lc.CourseId && q.Type == QuizType.Monthly && q.Status == ContentStatus.Published)
            .OrderBy(q => q.MonthNo).ToListAsync(ct);
        var result = new List<ExamSummaryDto>();
        foreach (var exam in exams) result.Add(await SummariseExamAsync(lc, exam, ct));
        return Result<IReadOnlyList<ExamSummaryDto>>.Success(result);
    }

    private async Task<ExamSummaryDto> SummariseExamAsync(LearnerCourse lc, Quiz exam, CancellationToken ct)
    {
        var month = exam.MonthNo ?? 1;
        var items = await db.LearnerRoadmapItems.AsNoTracking()
            .Where(i => i.LearnerCourseId == lc.Id && i.Lecture.Module.MonthNo == month)
            .Select(i => new { i.State, i.PlannedDate }).ToListAsync(ct);
        var done = items.Count(i => i.State == RoadmapItemState.Completed);
        DateOnly? scheduled = items.Count == 0 ? null : items.Max(i => i.PlannedDate);
        var today = await TodayAsync(lc.UserId, ct);
        var unlockPercent = await settings.GetIntAsync(SettingKeys.MonthlyExamUnlockPercent, 80, ct);
        var unlocked = items.Count == 0 || done * 100 >= unlockPercent * items.Count || (scheduled is { } d && today >= d);

        var maxAttempts = 1 + await settings.GetIntAsync(SettingKeys.ExamMaxRetakes, 2, ct);
        var gapDays = await settings.GetIntAsync(SettingKeys.ExamRetakeGapDays, 7, ct);
        var attempts = await db.QuizAttempts.AsNoTracking()
            .Where(a => a.LearnerCourseId == lc.Id && a.QuizId == exam.Id)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Status, a.IsPassed, a.Percentage, a.SubmittedAt, a.ExpiresAt })
            .ToListAsync(ct);
        var active = attempts.LastOrDefault(a => a.Status == AttemptStatus.InProgress);
        var closed = attempts.Where(a => a.Status != AttemptStatus.InProgress).ToList();
        int? best = closed.Where(a => a.Status == AttemptStatus.Graded).Select(a => (int?)a.Percentage).Max();
        var now = clock.GetUtcNow();
        DateTimeOffset? nextRetake = null;

        ExamAvailability availability;
        string text;
        if (active is not null)
        {
            availability = ExamAvailability.InProgress;
            var left = active.ExpiresAt is { } e ? Math.Max(0, (int)(e - now).TotalMinutes) : 0;
            text = $"In progress · about {left} min left";
        }
        else if (closed.Any(a => a.Status == AttemptStatus.Graded && a.IsPassed))
        {
            availability = ExamAvailability.Passed;
            text = $"Passed · best {best}%";
        }
        else if (closed.Count > 0 && closed[^1].Status == AttemptStatus.PendingReview)
        {
            availability = ExamAvailability.PendingReview;
            text = "Submitted · a teacher is grading your writing and speaking";
        }
        else if (closed.Count == 0)
        {
            availability = unlocked ? ExamAvailability.Available : ExamAvailability.Locked;
            text = unlocked
                ? $"Ready · {exam.TimeLimitMinutes} min"
                : $"Unlocks at {unlockPercent}% of month {month} lectures ({done}/{items.Count} done)" +
                  (scheduled is { } s ? $" or on {s:d MMM}" : "");
        }
        else if (closed.Count >= maxAttempts)
        {
            availability = ExamAvailability.NoRetakesLeft;
            text = $"Not passed · no retakes left (best {best ?? 0}%)";
        }
        else
        {
            nextRetake = (closed[^1].SubmittedAt ?? now).AddDays(gapDays);
            availability = now >= nextRetake ? ExamAvailability.RetakeAvailable : ExamAvailability.RetakeLater;
            text = availability == ExamAvailability.RetakeAvailable
                ? $"Not passed ({best ?? 0}%) · retake available ({maxAttempts - closed.Count} left)"
                : $"Not passed ({best ?? 0}%) · retake from {nextRetake.Value.ToLocalTime():d MMM}";
        }

        var questionCount = exam.Sections.Sum(s => s.SelectionMode == SelectionMode.Fixed ? s.Questions.Count : s.QuestionCount);
        return new ExamSummaryDto(exam.Id, month, exam.Title, exam.TimeLimitMinutes,
            exam.PassMark ?? await settings.GetIntAsync(SettingKeys.MonthlyExamPassMark, 60, ct), questionCount,
            availability, text, done, items.Count, scheduled, closed.Count, maxAttempts, nextRetake, best, active?.Id, closed.LastOrDefault()?.Id);
    }

    public async Task<Result<AttemptDto>> StartExamAsync(Guid userId, int learnerCourseId, int quizId, CancellationToken ct = default)
    {
        var lc = await FindLearnerCourseAsync(userId, learnerCourseId, ct);
        if (lc is null) return Error.NotFound("Course");
        await CloseExpiredForUserAsync(userId, ct);

        var exam = await db.Quizzes.Include(q => q.Sections).ThenInclude(s => s.Questions)
            .FirstOrDefaultAsync(q => q.Id == quizId && q.CourseId == lc.CourseId && q.Type == QuizType.Monthly && q.Status == ContentStatus.Published, ct);
        if (exam is null) return Error.NotFound("Exam");

        var summary = await SummariseExamAsync(lc, exam, ct);
        if (summary.Availability == ExamAvailability.InProgress && summary.ActiveAttemptId is { } activeId)
            return await GetAttemptAsync(userId, activeId, ct);
        if (summary.Availability is not (ExamAvailability.Available or ExamAvailability.RetakeAvailable))
            return Error.Conflict("exam_not_available", summary.StatusText);

        // FR-L65: one active exam attempt per learner.
        if (await db.QuizAttempts.AnyAsync(a => a.UserId == userId && a.Type == QuizType.Monthly && a.Status == AttemptStatus.InProgress, ct))
            return Error.Conflict("exam_in_progress", "Finish the exam you've already started first.");

        var questions = await PickExamQuestionsAsync(lc.CourseId, exam, ct);
        if (questions.Count == 0) return Error.Conflict("exam_not_ready", "This exam has no questions yet. Please try again later.");

        var now = clock.GetUtcNow();
        var attempt = new QuizAttempt
        {
            UserId = userId,
            CourseId = lc.CourseId,
            LearnerCourseId = lc.Id,
            QuizId = exam.Id,
            Type = QuizType.Monthly,
            Title = exam.Title,
            MonthNo = exam.MonthNo,
            AttemptNo = summary.AttemptsUsed + 1,
            StartedAt = now,
            ExpiresAt = now.AddMinutes(Math.Max(1, exam.TimeLimitMinutes)),
            PassMark = summary.PassMark
        };
        AddQuestions(attempt, questions);
        db.QuizAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return await BuildAttemptAsync(attempt, ct);
    }

    /// <summary>FR-A64: fixed sections use their chosen questions; random sections draw from the month's published questions.</summary>
    private async Task<List<(Question Question, string? Section)>> PickExamQuestionsAsync(int courseId, Quiz exam, CancellationToken ct)
    {
        var month = exam.MonthNo ?? 1;
        var courseLanguage = await db.Courses.Where(c => c.Id == courseId).Select(c => c.LanguageId).FirstAsync(ct);
        List<Question> monthPool = await PublishedQuestions()
            .Where(q => q.LanguageId == courseLanguage &&
                        ((q.Lecture != null && q.Lecture.Module.CourseId == courseId && q.Lecture.Module.MonthNo == month) ||
                         (q.Module != null && q.Module.CourseId == courseId && q.Module.MonthNo == month)))
            .ToListAsync(ct);
        List<Question> coursePool = await PublishedQuestions()
            .Where(q => q.LanguageId == courseLanguage &&
                        (q.CourseId == courseId || (q.Lecture != null && q.Lecture.Module.CourseId == courseId) ||
                         (q.Module != null && q.Module.CourseId == courseId)))
            .ToListAsync(ct);

        var chosen = new List<(Question, string?)>();
        foreach (var section in exam.Sections.OrderBy(s => s.SortOrder))
        {
            if (section.SelectionMode == SelectionMode.Fixed)
            {
                var ids = section.Questions.OrderBy(x => x.SortOrder).Select(x => x.QuestionId).ToList();
                List<Question> fixedQs = await PublishedQuestions().Where(q => ids.Contains(q.Id)).ToListAsync(ct);
                foreach (var id in ids)
                    if (fixedQs.FirstOrDefault(q => q.Id == id) is { } q && chosen.All(c => c.Item1.Id != q.Id)) chosen.Add((q, section.Title));
                continue;
            }

            bool Fits(Question q) => (section.Skill is null || q.Skill == section.Skill) &&
                                     (section.QuestionType is null || q.Type == section.QuestionType) &&
                                     chosen.All(c => c.Item1.Id != q.Id);
            var picked = Shuffle(monthPool.Where(Fits)).Take(section.QuestionCount).ToList();
            if (picked.Count < section.QuestionCount)
                picked.AddRange(Shuffle(coursePool.Where(q => Fits(q) && picked.All(p => p.Id != q.Id))).Take(section.QuestionCount - picked.Count));
            chosen.AddRange(picked.Select(q => (q, (string?)section.Title)));
        }
        return chosen;
    }

    // ================================================================== placement test (FR-L11)

    public async Task<Result<AttemptDto>> StartPlacementAsync(Guid userId, int courseId, CancellationToken ct = default)
    {
        var course = await db.Courses.AsNoTracking().Include(c => c.Language)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.Status == ContentStatus.Published, ct);
        if (course is null) return Error.NotFound("Course");

        var open = await WithAnswers()
            .Where(a => a.UserId == userId && a.CourseId == courseId && a.Type == QuizType.Placement && a.Status == AttemptStatus.InProgress)
            .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        if (open is not null && !IsExpired(open)) return await BuildAttemptAsync(open, ct);
        if (open is not null) await FinalizeAsync(open, ct);

        var perLevel = Math.Clamp(await settings.GetIntAsync(SettingKeys.PlacementQuestionsPerLevel, 2, ct), 1, 10);
        var levels = Enumerable.Range((int)course.StartLevel, Math.Max(1, (int)course.EndLevel - (int)course.StartLevel + 1))
            .Select(l => (CefrLevel)l).ToList();
        // Ten questions in total by default: widen to the language's range when the course spans few levels.
        if (levels.Count < 5)
        {
            var max = Math.Min((int)course.Language.MaxLevel, (int)course.StartLevel + 4);
            levels = Enumerable.Range((int)course.StartLevel, Math.Max(1, max - (int)course.StartLevel + 1)).Select(l => (CefrLevel)l).ToList();
        }

        List<Question> pool = await PublishedQuestions()
            .Where(q => q.LanguageId == course.LanguageId && levels.Contains(q.Level) &&
                        q.Type != QuestionType.Writing && q.Type != QuestionType.Speak)
            .ToListAsync(ct);
        var chosen = levels.SelectMany(l => Shuffle(pool.Where(q => q.Level == l)).Take(perLevel)).ToList();
        if (chosen.Count == 0)
            return Error.Conflict("placement_not_available", "The placement test isn't available for this language yet. Choose your level instead.");

        var now = clock.GetUtcNow();
        var attempt = new QuizAttempt
        {
            UserId = userId,
            CourseId = courseId,
            Type = QuizType.Placement,
            Title = $"{course.Language.Name} placement test",
            StartedAt = now,
            ExpiresAt = now.AddMinutes(PlacementMinutes),
            PassMark = await settings.GetIntAsync(SettingKeys.PlacementLevelPassPercent, 60, ct)
        };
        AddQuestions(attempt, chosen.Select(q => (q, (string?)q.Level.ToString())));
        db.QuizAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return await BuildAttemptAsync(attempt, ct);
    }

    // ================================================================== answering

    public async Task<Result<AttemptDto>> GetAttemptAsync(Guid userId, int attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(userId, attemptId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        if (attempt.Status == AttemptStatus.InProgress && IsExpired(attempt)) await FinalizeAsync(attempt, ct);
        return await BuildAttemptAsync(attempt, ct);
    }

    public async Task<Result<AnswerFeedbackDto>> SaveAnswerAsync(Guid userId, int attemptId, SaveAnswerRequest request, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(userId, attemptId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        var closed = await EnsureOpenAsync(attempt, ct);
        if (closed is not null) return closed;

        var answer = attempt.Answers.FirstOrDefault(a => a.QuestionId == request.QuestionId);
        if (answer is null) return Error.NotFound("Question");
        var question = answer.Question;
        if (question.Type == QuestionType.Speak)
            return Error.Validation("Text", "Record your answer for this question.");

        // Daily quiz and placement: the first answer is final, because the right answer is shown straight away.
        if (InstantFeedback(attempt) && answer.AnsweredAt is not null) return Feedback(attempt, answer);

        var optionIds = (request.OptionIds ?? []).Where(id => question.Options.Any(o => o.Id == id)).Distinct().ToList();
        var pairs = (request.Pairs ?? [])
            .Where(p => question.Options.Any(o => o.Id == p.OptionId) && !string.IsNullOrWhiteSpace(p.Target))
            .GroupBy(p => p.OptionId).ToDictionary(g => g.Key, g => g.Last().Target.Trim());
        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();

        answer.AnswerText = text;
        answer.SelectedOptionIds = optionIds.Count == 0 ? null : GradeRules.Csv(optionIds);
        answer.MatchAnswer = pairs.Count == 0 ? null : GradeRules.PairsToText(pairs);
        answer.AnsweredAt = clock.GetUtcNow();
        answer.TimeSpentSec += Math.Clamp(request.TimeSpentSec ?? 0, 0, 3600);
        ApplyGrade(answer, new AnswerInput(text, optionIds, pairs, null));

        await db.SaveChangesAsync(ct);
        return Feedback(attempt, answer);
    }

    public async Task<Result<AnswerFeedbackDto>> SaveAudioAnswerAsync(Guid userId, int attemptId, int questionId, Stream audio, string extension,
        string contentType, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(userId, attemptId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        var closed = await EnsureOpenAsync(attempt, ct);
        if (closed is not null) return closed;

        var answer = attempt.Answers.FirstOrDefault(a => a.QuestionId == questionId);
        if (answer is null) return Error.NotFound("Question");
        if (answer.Question.Type != QuestionType.Speak) return Error.Validation("Audio", "This question doesn't take a recording.");
        if (InstantFeedback(attempt) && answer.AnsweredAt is not null) return Feedback(attempt, answer);

        var old = answer.AudioUrl;
        answer.AudioUrl = await storage.SaveAsync(audio, "answers", extension, contentType, ct);
        answer.AnsweredAt = clock.GetUtcNow();
        ApplyGrade(answer, AnswerInput.Empty with { AudioUrl = answer.AudioUrl });
        await db.SaveChangesAsync(ct);
        if (old is not null) await storage.DeleteAsync(old, ct);
        return Feedback(attempt, answer);
    }

    public async Task<Result<AttemptResultDto>> SubmitAsync(Guid userId, int attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(userId, attemptId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        if (attempt.Status == AttemptStatus.InProgress) await FinalizeAsync(attempt, ct);
        return await BuildResultAsync(attempt, ct);
    }

    public async Task<Result> LogEventAsync(Guid userId, int attemptId, AttemptEventRequest request, CancellationToken ct = default)
    {
        var attempt = await db.QuizAttempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        if (attempt.Status != AttemptStatus.InProgress) return Result.Success();
        if (request.Kind is "background" or "left") attempt.BackgroundEvents++;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<AttemptResultDto>> GetResultAsync(Guid userId, int attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(userId, attemptId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        if (attempt.Status == AttemptStatus.InProgress && IsExpired(attempt)) await FinalizeAsync(attempt, ct);
        if (attempt.Status == AttemptStatus.InProgress) return Error.Conflict("attempt_open", "Finish and submit first.");
        return await BuildResultAsync(attempt, ct);
    }

    public async Task<Result<AttemptReviewDto>> GetReviewAsync(Guid userId, int attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(userId, attemptId, ct);
        if (attempt is null) return Error.NotFound("Attempt");
        if (attempt.Status == AttemptStatus.InProgress && IsExpired(attempt)) await FinalizeAsync(attempt, ct);
        if (attempt.Status == AttemptStatus.InProgress)
            return Error.Conflict("attempt_open", "You can review the answers once the attempt is closed.");

        var answers = attempt.Answers.OrderBy(a => a.SortOrder).Select((a, i) => new ReviewedAnswerDto(
            i + 1, a.QuestionId, a.Question.Type, a.Question.Skill, a.Question.Stem, a.Question.Prompt,
            DescribeAnswer(a), AnswerGrader.CorrectAnswerText(a.Question), a.Question.Explanation,
            a.IsCorrect, (double)a.Score, a.MaxScore, a.Feedback, a.NeedsReview)).ToList();
        return new AttemptReviewDto(await BuildResultAsync(attempt, ct), answers);
    }

    // ================================================================== marks (FR-L70 .. FR-L73, BR-05)

    public async Task<Result<MarksDto>> GetMarksAsync(Guid userId, int learnerCourseId, CancellationToken ct = default)
    {
        var lc = await db.LearnerCourses.AsNoTracking().Include(x => x.Course)
            .FirstOrDefaultAsync(x => x.Id == learnerCourseId && x.UserId == userId, ct);
        if (lc is null) return Error.NotFound("Course");
        await CloseExpiredForUserAsync(userId, ct);

        var attempts = await db.QuizAttempts.AsNoTracking()
            .Where(a => a.LearnerCourseId == lc.Id && (a.Status == AttemptStatus.Graded || a.Status == AttemptStatus.PendingReview))
            .OrderByDescending(a => a.SubmittedAt).ToListAsync(ct);

        var bestExamIds = attempts.Where(a => a.Type == QuizType.Monthly && a.Status == AttemptStatus.Graded)
            .GroupBy(a => a.QuizId).Select(g => g.OrderByDescending(a => a.Percentage).ThenBy(a => a.Id).First().Id).ToHashSet();
        bool Counts(QuizAttempt a) => a.Status == AttemptStatus.Graded &&
                                      (a.Type == QuizType.Daily ? !a.IsPractice : bestExamIds.Contains(a.Id));

        var rows = attempts.Select(a => new MarkRowDto(a.Id, a.Type, a.Title, a.SubmittedAt ?? a.StartedAt, (double)a.Score, a.MaxScore,
            a.Percentage, a.IsPassed, a.Status, a.IsPractice, Counts(a))).ToList();

        var daily = attempts.Where(a => a.Type == QuizType.Daily && Counts(a)).ToList();
        var exams = attempts.Where(a => a.Type == QuizType.Monthly && Counts(a)).ToList();
        int? dailyAvg = daily.Count == 0 ? null : (int)Math.Round(daily.Average(a => a.Percentage));
        int? examAvg = exams.Count == 0 ? null : (int)Math.Round(exams.Average(a => a.Percentage));
        var quizWeight = await settings.GetIntAsync(SettingKeys.CourseGradeQuizWeight, 30, ct);
        var examWeight = await settings.GetIntAsync(SettingKeys.CourseGradeExamWeight, 70, ct);

        var trend = attempts.Where(Counts).OrderBy(a => a.SubmittedAt).TakeLast(30)
            .Select(a => new TrendPointDto(DateOnly.FromDateTime((a.SubmittedAt ?? a.StartedAt).UtcDateTime), a.Percentage, a.Type)).ToList();

        var countedIds = attempts.Where(Counts).Select(a => a.Id).ToList();
        var skillRows = await db.AttemptAnswers.AsNoTracking()
            .Where(a => countedIds.Contains(a.AttemptId) && !a.NeedsReview && a.MaxScore > 0)
            .Select(a => new { a.Question.Skill, a.Score, a.MaxScore }).ToListAsync(ct);
        var skills = skillRows.GroupBy(r => r.Skill).OrderBy(g => g.Key)
            .Select(g => new SkillScoreDto(g.Key, GradeRules.Percent(g.Sum(r => r.Score), g.Sum(r => r.MaxScore)), g.Count())).ToList();

        return new MarksDto(lc.Id, lc.Course.Title, GradeRules.CourseGrade(dailyAvg, examAvg, quizWeight, examWeight),
            dailyAvg, examAvg, quizWeight, examWeight, rows, trend, skills);
    }

    // ================================================================== closing attempts

    public async Task<int> CloseExpiredAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var ids = await db.QuizAttempts.Where(a => a.Status == AttemptStatus.InProgress && a.ExpiresAt != null && a.ExpiresAt < now)
            .Select(a => a.Id).Take(200).ToListAsync(ct);
        foreach (var id in ids)
        {
            var attempt = await WithAnswers().FirstAsync(a => a.Id == id, ct);
            await FinalizeAsync(attempt, ct);
        }
        return ids.Count;
    }

    private async Task CloseExpiredForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await WithAnswers()
            .Where(a => a.UserId == userId && a.Status == AttemptStatus.InProgress && a.ExpiresAt != null && a.ExpiresAt < now)
            .ToListAsync(ct);
        foreach (var attempt in expired) await FinalizeAsync(attempt, ct);
    }

    /// <summary>Grades unanswered questions as wrong, totals the score, updates analytics and the review list.</summary>
    private async Task FinalizeAsync(QuizAttempt attempt, CancellationToken ct)
    {
        if (attempt.Status != AttemptStatus.InProgress) return;
        var now = clock.GetUtcNow();

        // A daily quiz nobody started just expires.
        if (attempt.Type == QuizType.Daily && attempt.Answers.All(a => a.AnsweredAt is null) && IsExpired(attempt))
        {
            attempt.Status = AttemptStatus.Expired;
            attempt.SubmittedAt = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var answer in attempt.Answers.Where(a => a.AnsweredAt is null)) ApplyGrade(answer, AnswerInput.Empty);

        if (attempt.Type == QuizType.Daily && !attempt.IsPractice && IsExpired(attempt) &&
            !await settings.GetBoolAsync(SettingKeys.LateQuizCountsTowardMarks, false, ct))
            attempt.IsPractice = true;

        attempt.SubmittedAt = now;
        AttemptScoring.Recalculate(attempt);

        if (attempt.Type == QuizType.Placement)
        {
            var lowest = attempt.Answers.Count == 0 ? CefrLevel.A1 : attempt.Answers.Min(a => a.Question.Level);
            attempt.SuggestedLevel = GradeRules.PlacementLevel(
                attempt.Answers.Select(a => (a.Question.Level, a.IsCorrect == true)), attempt.PassMark, lowest);
        }

        foreach (var answer in attempt.Answers.Where(a => a.IsCorrect is not null))
            AttemptScoring.CountForAnalytics(answer);

        if (attempt.LearnerCourseId is { } lcId && !attempt.IsPractice) await UpdateReviewCardsAsync(attempt, lcId, ct);

        // FR-L63: AI pre-grades exam writing; confident grades are applied, the rest wait for a reviewer with the suggestion.
        // Writing goes to the AI, speaking to pronunciation scoring (Phase 6); both leave a suggestion for the reviewer.
        var writing = attempt.Answers.Count(a => a.NeedsReview &&
            ((a.Question.Type == QuestionType.Writing && !string.IsNullOrWhiteSpace(a.AnswerText)) ||
             (a.Question.Type == QuestionType.Speak && !string.IsNullOrWhiteSpace(a.AudioUrl))));
        if (attempt.Type == QuizType.Monthly && writing > 0)
        {
            db.AiGenerationRequests.Add(new AiGenerationRequest
            {
                Type = AiRequestType.WritingGrading, Status = AiRequestStatus.Queued, CourseId = attempt.CourseId, AttemptId = attempt.Id,
                RequestedBy = attempt.UserId, ProgressTotal = writing, CreatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);

        // Phase 7: daily quiz and exam points, streak, badges and the certificate check. Idempotent; safe to call again on regrade.
        if (attempt.Type != QuizType.Placement) await progress.OnAttemptClosedAsync(attempt.Id, ct);
    }

    private async Task UpdateReviewCardsAsync(QuizAttempt attempt, int learnerCourseId, CancellationToken ct)
    {
        var today = await TodayAsync(attempt.UserId, ct);
        var now = clock.GetUtcNow();
        var questionIds = attempt.Answers.Where(a => a.IsCorrect is not null).Select(a => a.QuestionId).ToList();
        var cards = await db.ReviewCards.Where(c => c.LearnerCourseId == learnerCourseId && questionIds.Contains(c.QuestionId)).ToListAsync(ct);
        foreach (var answer in attempt.Answers.Where(a => a.IsCorrect is not null))
        {
            var card = cards.FirstOrDefault(c => c.QuestionId == answer.QuestionId);
            var correct = answer.IsCorrect == true;
            if (card is null)
            {
                if (correct) continue;
                card = new ReviewCard { LearnerCourseId = learnerCourseId, QuestionId = answer.QuestionId, Box = 1 };
                ReviewScheduler.Apply(card, false, today, now);
                card.Lapses = 0;
                db.ReviewCards.Add(card);
                cards.Add(card);
                continue;
            }
            if (!ReviewScheduler.Apply(card, correct, today, now)) db.ReviewCards.Remove(card);
        }
    }

    // ================================================================== helpers

    private IQueryable<Question> PublishedQuestions() =>
        db.Questions.Include(q => q.Options).Where(q => q.Status == ContentStatus.Published && !q.IsDeleted);

    private Task<LearnerCourse?> FindLearnerCourseAsync(Guid userId, int learnerCourseId, CancellationToken ct) =>
        db.LearnerCourses.FirstOrDefaultAsync(lc => lc.Id == learnerCourseId && lc.UserId == userId &&
            (lc.Status == LearnerCourseStatus.Active || lc.Status == LearnerCourseStatus.Paused || lc.Status == LearnerCourseStatus.Completed), ct);

    private async Task<DateOnly> TodayAsync(Guid userId, CancellationToken ct)
    {
        var tz = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        return LearnerClock.Today(clock, tz);
    }

    private Task<QuizAttempt?> LoadAttemptAsync(Guid userId, int attemptId, CancellationToken ct) =>
        WithAnswers().FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId, ct);

    private bool IsExpired(QuizAttempt attempt) => attempt.ExpiresAt is { } e && clock.GetUtcNow() >= e;

    private static bool InstantFeedback(QuizAttempt attempt) => attempt.Type != QuizType.Monthly;

    /// <summary>Exam answers can't be changed after the time is up; the attempt is closed and graded instead.</summary>
    private async Task<Error?> EnsureOpenAsync(QuizAttempt attempt, CancellationToken ct)
    {
        if (attempt.Status != AttemptStatus.InProgress) return Error.Conflict("attempt_closed", "This attempt is already closed.");
        if (!IsExpired(attempt)) return null;
        // Daily quizzes may still be finished after midnight; they just count as practice (FR-L53).
        if (attempt.Type == QuizType.Daily) return null;
        await FinalizeAsync(attempt, ct);
        return Error.Conflict("attempt_closed", "Time is up. Your answers were submitted.");
    }

    private static void ApplyGrade(AttemptAnswer answer, AnswerInput input)
    {
        var outcome = AnswerGrader.Grade(answer.Question, input);
        answer.MaxScore = Math.Max(1, answer.Question.Points);
        answer.IsCorrect = outcome.IsCorrect;
        answer.NeedsReview = outcome.NeedsReview;
        answer.Score = outcome.NeedsReview ? 0 : Math.Round(outcome.Fraction * answer.MaxScore, 2);
    }

    private static AnswerFeedbackDto Feedback(QuizAttempt attempt, AttemptAnswer answer) => InstantFeedback(attempt)
        ? new AnswerFeedbackDto(answer.QuestionId, answer.IsCorrect, (double)answer.Score, answer.MaxScore,
            AnswerGrader.CorrectAnswerText(answer.Question), answer.Question.Explanation, answer.NeedsReview)
        : new AnswerFeedbackDto(answer.QuestionId, null, 0, answer.MaxScore, null, null, answer.NeedsReview);

    private static void AddQuestions(QuizAttempt attempt, IEnumerable<(Question Question, string? Section)> questions)
    {
        var order = 0;
        foreach (var (q, section) in questions)
        {
            attempt.Answers.Add(new AttemptAnswer
            {
                QuestionId = q.Id,
                Question = q,
                SortOrder = ++order,
                SectionTitle = section,
                // FR-L65: option order is shuffled per attempt.
                OptionOrder = GradeRules.Csv(Shuffle(q.Options).Select(o => o.Id)),
                MaxScore = Math.Max(1, q.Points)
            });
        }
    }

    private static void AddQuestions(QuizAttempt attempt, IEnumerable<Question> questions) =>
        AddQuestions(attempt, questions.Select(q => (q, (string?)null)));

    private static List<T> Shuffle<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>Stable shuffle so a resumed attempt shows the same order (FR-L62).</summary>
    private static List<T> StableShuffle<T>(IEnumerable<T> items, int seed)
    {
        var list = items.ToList();
        var random = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private async Task<AttemptDto> BuildAttemptAsync(QuizAttempt attempt, CancellationToken ct)
    {
        var language = await db.Courses.AsNoTracking().Where(c => c.Id == attempt.CourseId)
            .Select(c => new { c.Language.SttLocale, c.Language.IsoCode, c.Language.Direction }).FirstAsync(ct);
        var now = clock.GetUtcNow();
        var instant = InstantFeedback(attempt);

        var questions = attempt.Answers.OrderBy(a => a.SortOrder).Select((a, i) =>
        {
            var q = a.Question;
            var order = GradeRules.ParseCsv(a.OptionOrder);
            int Rank(int id)
            {
                var index = order.IndexOf(id);
                return index < 0 ? int.MaxValue : index;
            }
            var options = q.Options.OrderBy(o => Rank(o.Id)).ToList();
            var seed = (attempt.Id * 397) ^ q.Id;
            var isMatch = q.Type == QuestionType.MatchPairs;
            var isReorder = q.Type == QuestionType.ReorderWords;
            var showsOptions = q.Type is QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose or QuestionType.MatchPairs;

            SavedAnswerDto? saved = a.AnsweredAt is null ? null : new SavedAnswerDto(
                a.AnswerText, GradeRules.ParseCsv(a.SelectedOptionIds),
                GradeRules.ParsePairs(a.MatchAnswer).Select(p => new MatchPairDto(p.Key, p.Value)).ToList(), a.AudioUrl);
            var feedback = instant && a.AnsweredAt is not null ? Feedback(attempt, a) : null;

            return new QuizQuestionDto(
                q.Id, i + 1, a.SectionTitle, q.Type, q.Skill, q.Stem, q.Prompt, q.MediaUrl, a.MaxScore,
                showsOptions ? options.Select(o => new QuestionOptionDto(o.Id, o.Text)).ToList() : [],
                isMatch ? StableShuffle(options.Select(o => o.MatchKey ?? ""), seed).ToList() : [],
                isReorder ? StableShuffle(AnswerGrader.WordTiles(q), seed).ToList() : [],
                AnswerGrader.AllowsMultiple(q), saved, feedback);
        }).ToList();

        int? secondsLeft = attempt.Status == AttemptStatus.InProgress && attempt.ExpiresAt is { } e && attempt.Type != QuizType.Daily
            ? Math.Max(0, (int)(e - now).TotalSeconds)
            : null;

        return new AttemptDto(attempt.Id, attempt.Type, attempt.Title, attempt.Status, attempt.IsPractice, instant, attempt.StartedAt,
            attempt.ExpiresAt, secondsLeft, questions, language.SttLocale, language.IsoCode, language.Direction);
    }

    private Task<AttemptResultDto> BuildResultAsync(QuizAttempt attempt, CancellationToken ct) =>
        Task.FromResult(AttemptScoring.ToResult(attempt));

    private static string DescribeAnswer(AttemptAnswer a)
    {
        var q = a.Question;
        if (a.AnsweredAt is null) return "(no answer)";
        switch (q.Type)
        {
            case QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose:
                var ids = GradeRules.ParseCsv(a.SelectedOptionIds);
                return string.Join(", ", q.Options.Where(o => ids.Contains(o.Id)).Select(o => o.Text));
            case QuestionType.MatchPairs:
                var pairs = GradeRules.ParsePairs(a.MatchAnswer);
                return string.Join("; ", q.Options.Select(o => $"{o.Text} → {(pairs.TryGetValue(o.Id, out var t) ? t : "?")}"));
            case QuestionType.Speak:
                return a.AudioUrl is null ? "(no recording)" : "Recording sent";
            default:
                return a.AnswerText ?? "(no answer)";
        }
    }
}

/// <summary>Totals shared by the learner service and the grading queue.</summary>
internal static class AttemptScoring
{
    public static void Recalculate(QuizAttempt attempt)
    {
        attempt.MaxScore = attempt.Answers.Sum(a => a.MaxScore);
        attempt.Score = attempt.Answers.Sum(a => a.Score);
        attempt.Percentage = GradeRules.Percent(attempt.Score, attempt.MaxScore);
        var pending = attempt.Answers.Any(a => a.NeedsReview);
        attempt.Status = pending ? AttemptStatus.PendingReview : AttemptStatus.Graded;
        attempt.IsPassed = !pending && attempt.Percentage >= attempt.PassMark;
    }

    public static void CountForAnalytics(AttemptAnswer answer)
    {
        answer.Question.TimesAnswered++;
        if (answer.IsCorrect == true) answer.Question.TimesCorrect++;
        answer.Question.TotalTimeSec += answer.TimeSpentSec;
    }

    public static AttemptResultDto ToResult(QuizAttempt attempt)
    {
        var skills = attempt.Answers.Where(a => !a.NeedsReview && a.Question is not null)
            .GroupBy(a => a.Question.Skill).OrderBy(g => g.Key)
            .Select(g => new SkillScoreDto(g.Key, GradeRules.Percent(g.Sum(a => a.Score), g.Sum(a => a.MaxScore)), g.Count())).ToList();
        return new AttemptResultDto(attempt.Id, attempt.Type, attempt.Title, attempt.Status, (double)attempt.Score, attempt.MaxScore,
            attempt.Percentage, attempt.IsPassed, attempt.PassMark, attempt.IsPractice,
            attempt.Answers.Count(a => a.IsCorrect == true), attempt.Answers.Count, attempt.Answers.Count(a => a.NeedsReview),
            skills, attempt.SuggestedLevel, attempt.SubmittedAt);
    }
}
