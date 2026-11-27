using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Admin;

// ===================================================================== question bank (FR-A60, FR-A61, FR-A65, FR-A66)

public interface IQuestionAdminService
{
    Task<PagedResult<QuestionListItem>> ListAsync(QuestionFilter filter, CancellationToken ct = default);
    Task<QuestionEditorModel?> GetEditorAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(QuestionInput input, CancellationToken ct = default);
    Task<Result> PublishAsync(int id, CancellationToken ct = default);
    Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default);
    Task<Result> DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Label)>> LectureOptionsAsync(int languageId, CancellationToken ct = default);
    Task<byte[]> ExportAsync(QuestionFilter filter, CancellationToken ct = default);
    Task<Result<ImportResult>> ImportAsync(int languageId, Stream file, CancellationToken ct = default);

    /// <summary>Server-side rules for each question type; also used by the import.</summary>
    IReadOnlyDictionary<string, string[]> Validate(QuestionInput input);
}

internal sealed class QuestionAdminService(
    IApplicationDbContext db,
    IAuditLogger audit,
    IQuestionSpreadsheet spreadsheet) : IQuestionAdminService
{
    /// <summary>FR-A66: questions most learners get right or wrong are flagged for a second look.</summary>
    public const int FlagLowPercent = 20, FlagHighPercent = 95, FlagMinAnswers = 10;

    private IQueryable<Question> Filtered(QuestionFilter f)
    {
        var q = db.Questions.AsNoTracking().Where(x => !x.IsDeleted);
        if (f.LanguageId is { } lang) q = q.Where(x => x.LanguageId == lang);
        if (f.CourseId is { } course) q = q.Where(x => x.CourseId == course);
        if (f.LectureId is { } lecture) q = q.Where(x => x.LectureId == lecture);
        if (f.Level is { } level) q = q.Where(x => x.Level == level);
        if (f.Skill is { } skill) q = q.Where(x => x.Skill == skill);
        if (f.Type is { } type) q = q.Where(x => x.Type == type);
        if (f.Difficulty is { } d) q = q.Where(x => x.Difficulty == d);
        if (f.Status is { } status) q = q.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(x => x.Stem.Contains(s) || (x.Prompt != null && x.Prompt.Contains(s)));
        }
        if (f.FlaggedOnly)
            q = q.Where(x => x.TimesAnswered >= FlagMinAnswers &&
                             (x.TimesCorrect * 100 < FlagLowPercent * x.TimesAnswered || x.TimesCorrect * 100 > FlagHighPercent * x.TimesAnswered));
        return q;
    }

    public async Task<PagedResult<QuestionListItem>> ListAsync(QuestionFilter filter, CancellationToken ct = default)
    {
        var q = Filtered(filter);
        var total = await q.CountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var rows = await q.OrderByDescending(x => x.Id).Skip((page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new
            {
                x.Id, x.Stem, x.Type, x.Skill, x.Level, x.Difficulty, Language = x.Language.Name,
                Lecture = x.Lecture != null ? x.Lecture.Title : null, x.Status, x.Source, x.TimesAnswered, x.TimesCorrect, x.TotalTimeSec
            }).ToListAsync(ct);

        return new PagedResult<QuestionListItem>
        {
            Page = page,
            PageSize = filter.PageSize,
            TotalCount = total,
            Items = rows.Select(r =>
            {
                int? percent = r.TimesAnswered == 0 ? null : (int)Math.Round(100.0 * r.TimesCorrect / r.TimesAnswered);
                int? avg = r.TimesAnswered == 0 ? null : (int)(r.TotalTimeSec / r.TimesAnswered);
                string? flag = r.TimesAnswered < FlagMinAnswers || percent is null ? null
                    : percent < FlagLowPercent ? "Too hard?"
                    : percent > FlagHighPercent ? "Too easy?"
                    : null;
                return new QuestionListItem(r.Id, r.Stem, r.Type, r.Skill, r.Level, r.Difficulty, r.Language, r.Lecture, r.Status, r.Source,
                    r.TimesAnswered, percent, avg, flag);
            }).ToList()
        };
    }

    public async Task<QuestionEditorModel?> GetEditorAsync(int id, CancellationToken ct = default)
    {
        var q = await db.Questions.AsNoTracking().Include(x => x.Options).FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (q is null) return null;
        var lastReview = await db.ReviewItems.AsNoTracking()
            .Where(r => r.EntityType == ReviewEntityType.Question && r.EntityId == id).OrderByDescending(r => r.Id)
            .Select(r => new { r.Status, r.Comment }).FirstOrDefaultAsync(ct);
        return new QuestionEditorModel(ToInput(q), q.Status, q.Source, q.Version, q.TimesAnswered,
            q.TimesAnswered == 0 ? null : (int)Math.Round(100.0 * q.TimesCorrect / q.TimesAnswered),
            lastReview?.Comment, lastReview?.Status, q.SourceText);
    }

    private static QuestionInput ToInput(Question q) => new()
    {
        Id = q.Id, LanguageId = q.LanguageId, LectureId = q.LectureId, Type = q.Type, Skill = q.Skill, Level = q.Level,
        Difficulty = q.Difficulty, Points = q.Points, Stem = q.Stem, Prompt = q.Prompt, MediaUrl = q.MediaUrl, Explanation = q.Explanation,
        Options = q.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
            .Select(o => new OptionInput { Text = o.Text, IsCorrect = o.IsCorrect, MatchKey = o.MatchKey }).ToList()
    };

    public IReadOnlyDictionary<string, string[]> Validate(QuestionInput input)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        var options = input.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)).ToList();
        switch (input.Type)
        {
            case QuestionType.MultipleChoice:
            case QuestionType.ListenChoose:
                if (options.Count < 2) Add("Options", "Add at least two options.");
                if (!options.Any(o => o.IsCorrect)) Add("Options", "Tick the correct option.");
                if (input.Type == QuestionType.ListenChoose && string.IsNullOrWhiteSpace(input.Prompt) && string.IsNullOrWhiteSpace(input.MediaUrl))
                    Add("Prompt", "Give the sentence to listen to, or an audio URL.");
                break;
            case QuestionType.TrueFalse:
                if (options.Count != 2 || options.Count(o => o.IsCorrect) != 1) Add("Options", "True/false needs two options with one correct.");
                break;
            case QuestionType.FillBlank:
                if (!input.Stem.Contains("___")) Add("Stem", "Mark the gap with three underscores: ___");
                if (options.Count == 0) Add("Options", "Add at least one accepted answer.");
                break;
            case QuestionType.MatchPairs:
                if (options.Count < 2) Add("Options", "Add at least two pairs.");
                if (options.Any(o => string.IsNullOrWhiteSpace(o.MatchKey))) Add("Options", "Every pair needs both sides.");
                break;
            case QuestionType.ReorderWords:
                if (options.Count == 0 || options[0].Text!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
                    Add("Options", "Give the correct sentence (at least two words); extra rows are other accepted orders.");
                break;
            case QuestionType.Translate:
                if (string.IsNullOrWhiteSpace(input.Prompt)) Add("Prompt", "Give the sentence to translate.");
                if (options.Count == 0) Add("Options", "Add at least one accepted translation.");
                break;
            case QuestionType.Speak:
                if (string.IsNullOrWhiteSpace(input.Prompt)) Add("Prompt", "Give the sentence the learner has to say.");
                break;
        }
        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    public async Task<Result<int>> SaveAsync(QuestionInput input, CancellationToken ct = default)
    {
        Normalize(input);
        var errors = Validate(input);
        if (errors.Count > 0) return Error.Validation(errors.ToDictionary(e => e.Key, e => e.Value));

        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation("LanguageId", "Choose a language.");
        int? courseId = null, moduleId = null;
        if (input.LectureId is { } lectureId)
        {
            var lecture = await db.Lectures.AsNoTracking().Where(l => l.Id == lectureId)
                .Select(l => new { l.ModuleId, l.Module.CourseId, l.Module.Course.LanguageId }).FirstOrDefaultAsync(ct);
            if (lecture is null || lecture.LanguageId != input.LanguageId)
                return Error.Validation("LectureId", "Choose a lecture of the same language.");
            courseId = lecture.CourseId;
            moduleId = lecture.ModuleId;
        }

        Question q;
        if (input.Id == 0)
        {
            q = new Question { Status = ContentStatus.Draft, Source = ContentSource.Manual };
            db.Questions.Add(q);
        }
        else
        {
            q = await db.Questions.Include(x => x.Options).FirstOrDefaultAsync(x => x.Id == input.Id && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException();
            q.Version++;
            // An approved question that changes needs a new review; a published one stays live.
            if (q.Status == ContentStatus.Approved) q.Status = ContentStatus.Draft;
            db.QuestionOptions.RemoveRange(q.Options);
            q.Options.Clear();
        }

        q.LanguageId = input.LanguageId;
        q.LectureId = input.LectureId;
        q.ModuleId = moduleId;
        q.CourseId = courseId;
        q.Type = input.Type;
        q.Skill = input.Skill;
        q.Level = input.Level;
        q.Difficulty = Math.Clamp(input.Difficulty, 1, 5);
        q.Points = Math.Clamp(input.Points, 1, 20);
        q.Stem = input.Stem.Trim();
        q.Prompt = Clean(input.Prompt);
        q.MediaUrl = Clean(input.MediaUrl);
        q.Explanation = Clean(input.Explanation);

        var order = 0;
        foreach (var o in input.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)))
            q.Options.Add(new QuestionOption { Text = o.Text!.Trim(), IsCorrect = o.IsCorrect, MatchKey = Clean(o.MatchKey), SortOrder = ++order });

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Question", q.Id.ToString(), ct: ct);
        return q.Id;
    }

    /// <summary>Type-specific clean-up before validation: true/false options, accepted answers marked correct.</summary>
    private static void Normalize(QuestionInput input)
    {
        if (input.Type == QuestionType.TrueFalse && input.Options.All(o => string.IsNullOrWhiteSpace(o.Text)))
            input.Options = [new() { Text = "True", IsCorrect = true }, new() { Text = "False" }];
        if (input.Type is QuestionType.FillBlank or QuestionType.Translate or QuestionType.ReorderWords)
            foreach (var o in input.Options) o.IsCorrect = true;
        if (input.Type is QuestionType.Writing or QuestionType.Speak)
            input.Options = [];
        if (input.Type != QuestionType.MatchPairs)
            foreach (var o in input.Options) o.MatchKey = null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<Result> PublishAsync(int id, CancellationToken ct = default)
    {
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (q is null) return Error.NotFound("Question");
        if (q.Status != ContentStatus.Approved) return Error.Validation("Status", "Only approved questions can be published.");
        q.Status = ContentStatus.Published;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Publish", "Question", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default)
    {
        if (status is not (ContentStatus.Draft or ContentStatus.Archived)) return Error.Validation("Status", "Use review and publish for other states.");
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (q is null) return Error.NotFound("Question");
        q.Status = status;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(status == ContentStatus.Draft ? "Unpublish" : "Archive", "Question", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id, CancellationToken ct = default)
    {
        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (q is null) return Error.NotFound("Question");
        db.Questions.Remove(q); // soft delete: old attempts keep showing it
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "Question", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<(int Id, string Label)>> LectureOptionsAsync(int languageId, CancellationToken ct = default)
    {
        var rows = await db.Lectures.AsNoTracking().Where(l => l.Module.Course.LanguageId == languageId)
            .OrderBy(l => l.Module.Course.Title).ThenBy(l => l.Module.SortOrder).ThenBy(l => l.SortOrder)
            .Select(l => new { l.Id, Label = l.Module.Course.Title + " · M" + l.Module.MonthNo + " · " + l.Title }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Label)).ToList();
    }

    public async Task<byte[]> ExportAsync(QuestionFilter filter, CancellationToken ct = default)
    {
        var questions = await Filtered(filter).Include(q => q.Options).Include(q => q.Lecture)
            .OrderBy(q => q.Id).Take(5000).ToListAsync(ct);
        return spreadsheet.Export(questions.Select(q => new QuestionSheetRow(
            0, q.Id, q.LectureId?.ToString(), q.Type.ToString(), q.Skill.ToString(), q.Level.ToString(), q.Difficulty, q.Points,
            q.Stem, q.Prompt, q.MediaUrl, q.Explanation,
            q.Options.OrderBy(o => o.SortOrder).Select(o => q.Type == QuestionType.MatchPairs
                ? $"{o.Text} => {o.MatchKey}"
                : (o.IsCorrect && q.Type is not (QuestionType.FillBlank or QuestionType.Translate or QuestionType.ReorderWords) ? "*" : "") + o.Text)
                .ToList())));
    }

    /// <summary>FR-A65: rows with an Id update that question (same language); others create Draft questions.</summary>
    public async Task<Result<ImportResult>> ImportAsync(int languageId, Stream file, CancellationToken ct = default)
    {
        if (!await db.Languages.AnyAsync(l => l.Id == languageId, ct)) return Error.Validation("LanguageId", "Choose a language.");
        IReadOnlyList<QuestionSheetRow> rows;
        try
        {
            rows = spreadsheet.Read(file);
        }
        catch (Exception)
        {
            return Error.Validation("File", "That file couldn't be read. Use the Excel template from Export.");
        }

        var lectures = await db.Lectures.AsNoTracking().Where(l => l.Module.Course.LanguageId == languageId)
            .Select(l => new { l.Id, l.Title }).ToListAsync(ct);
        var errors = new List<string>();
        int created = 0, updated = 0;

        foreach (var row in rows)
        {
            if (row.Error is not null)
            {
                errors.Add($"Row {row.RowNumber}: {row.Error}");
                continue;
            }
            if (!Enum.TryParse<QuestionType>(row.Type, true, out var type) || !Enum.IsDefined(type))
            {
                errors.Add($"Row {row.RowNumber}: unknown type \"{row.Type}\".");
                continue;
            }
            if (!Enum.TryParse<Skill>(row.Skill, true, out var skill) || !Enum.IsDefined(skill))
            {
                errors.Add($"Row {row.RowNumber}: unknown skill \"{row.Skill}\".");
                continue;
            }
            if (!Enum.TryParse<CefrLevel>(row.Level, true, out var level) || !Enum.IsDefined(level))
            {
                errors.Add($"Row {row.RowNumber}: unknown level \"{row.Level}\".");
                continue;
            }

            int? lectureId = null;
            if (!string.IsNullOrWhiteSpace(row.Lecture))
            {
                var match = int.TryParse(row.Lecture, out var lid)
                    ? lectures.FirstOrDefault(l => l.Id == lid)
                    : lectures.FirstOrDefault(l => string.Equals(l.Title, row.Lecture.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    errors.Add($"Row {row.RowNumber}: lecture \"{row.Lecture}\" not found in this language.");
                    continue;
                }
                lectureId = match.Id;
            }

            var input = new QuestionInput
            {
                Id = 0, LanguageId = languageId, LectureId = lectureId, Type = type, Skill = skill, Level = level,
                Difficulty = row.Difficulty, Points = row.Points, Stem = row.Stem, Prompt = row.Prompt, MediaUrl = row.MediaUrl,
                Explanation = row.Explanation,
                Options = row.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(ParseOption).ToList()
            };

            if (row.Id is { } existingId)
            {
                if (!await db.Questions.AnyAsync(q => q.Id == existingId && q.LanguageId == languageId && !q.IsDeleted, ct))
                {
                    errors.Add($"Row {row.RowNumber}: question {existingId} not found in this language.");
                    continue;
                }
                input.Id = existingId;
            }

            var result = await SaveAsync(input, ct);
            if (result.IsFailure)
            {
                var detail = result.Error!.Details is { Count: > 0 } d ? string.Join(" ", d.SelectMany(x => x.Value)) : result.Error.Message;
                errors.Add($"Row {row.RowNumber}: {detail}");
                continue;
            }
            if (input.Id == 0) created++;
            else updated++;
        }
        return new ImportResult(created, updated, errors);
    }

    private static OptionInput ParseOption(string cell)
    {
        var text = cell.Trim();
        var arrow = text.IndexOf("=>", StringComparison.Ordinal);
        if (arrow > 0) return new OptionInput { Text = text[..arrow].Trim(), MatchKey = text[(arrow + 2)..].Trim(), IsCorrect = true };
        var correct = text.StartsWith('*');
        return new OptionInput { Text = correct ? text[1..].Trim() : text, IsCorrect = correct };
    }
}

// ===================================================================== monthly exams and quiz rules (FR-A63, FR-A64)

public interface IQuizAdminService
{
    Task<IReadOnlyList<ExamListItem>> ListAsync(int? courseId, CancellationToken ct = default);
    Task<ExamEditorModel?> GetEditorAsync(int id, CancellationToken ct = default);
    Task<ExamEditorModel?> NewAsync(int courseId, CancellationToken ct = default);
    Task<ExamEditorModel?> RebuildAsync(ExamInput input, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(ExamInput input, CancellationToken ct = default);
    Task<Result> PublishAsync(int id, CancellationToken ct = default);
    Task<Result> UnpublishAsync(int id, CancellationToken ct = default);
    Task<Result> DeleteAsync(int id, CancellationToken ct = default);
    Task<DailyQuizRuleInput> GetDailyRuleAsync(CancellationToken ct = default);
    Task<Result> SaveDailyRuleAsync(DailyQuizRuleInput input, CancellationToken ct = default);
}

internal sealed class QuizAdminService(IApplicationDbContext db, ISettingsService settings, IAuditLogger audit) : IQuizAdminService
{
    public async Task<IReadOnlyList<ExamListItem>> ListAsync(int? courseId, CancellationToken ct = default)
    {
        var rows = await db.Quizzes.AsNoTracking()
            .Where(q => q.Type == QuizType.Monthly && (courseId == null || q.CourseId == courseId))
            .OrderBy(q => q.Course.Language.Name).ThenBy(q => q.Course.Title).ThenBy(q => q.MonthNo)
            .Select(q => new
            {
                q.Id, q.CourseId, Course = q.Course.Title, Language = q.Course.Language.Name, q.MonthNo, q.Title, q.TimeLimitMinutes, q.PassMark,
                q.Status,
                Sections = q.Sections.Select(s => new { s.SelectionMode, s.QuestionCount, Fixed = s.Questions.Count }).ToList()
            }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var attempts = await db.QuizAttempts.AsNoTracking().Where(a => a.QuizId != null && ids.Contains(a.QuizId.Value))
            .GroupBy(a => a.QuizId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return rows.Select(r => new ExamListItem(r.Id, r.CourseId, r.Course, r.Language, r.MonthNo, r.Title, r.TimeLimitMinutes, r.PassMark,
            r.Status, r.Sections.Count, r.Sections.Sum(s => s.SelectionMode == SelectionMode.Fixed ? s.Fixed : s.QuestionCount),
            attempts.FirstOrDefault(a => a.Key == r.Id)?.Count ?? 0)).ToList();
    }

    public async Task<ExamEditorModel?> GetEditorAsync(int id, CancellationToken ct = default)
    {
        var quiz = await db.Quizzes.AsNoTracking().Include(q => q.Sections).ThenInclude(s => s.Questions)
            .FirstOrDefaultAsync(q => q.Id == id && q.Type == QuizType.Monthly, ct);
        if (quiz is null) return null;
        var input = new ExamInput
        {
            Id = quiz.Id, CourseId = quiz.CourseId, Title = quiz.Title, Instructions = quiz.Instructions, MonthNo = quiz.MonthNo ?? 1,
            TimeLimitMinutes = quiz.TimeLimitMinutes, PassMark = quiz.PassMark,
            Sections = quiz.Sections.OrderBy(s => s.SortOrder).Select(s => new SectionInput
            {
                Title = s.Title, Skill = s.Skill, QuestionType = s.QuestionType, QuestionCount = s.QuestionCount, SelectionMode = s.SelectionMode,
                QuestionIds = s.Questions.OrderBy(x => x.SortOrder).Select(x => x.QuestionId).ToList()
            }).ToList()
        };
        return await BuildAsync(input, quiz.Status, ct);
    }

    public async Task<ExamEditorModel?> NewAsync(int courseId, CancellationToken ct = default)
    {
        if (!await db.Courses.AnyAsync(c => c.Id == courseId, ct)) return null;
        var used = await db.Quizzes.Where(q => q.CourseId == courseId && q.Type == QuizType.Monthly).Select(q => q.MonthNo ?? 0).ToListAsync(ct);
        var months = await db.CourseModules.Where(m => m.CourseId == courseId).Select(m => m.MonthNo).Distinct().OrderBy(m => m).ToListAsync(ct);
        var month = months.FirstOrDefault(m => !used.Contains(m));
        if (month == 0) month = (used.Count == 0 ? 0 : used.Max()) + 1;
        var input = new ExamInput
        {
            CourseId = courseId,
            MonthNo = month,
            Title = $"Month {month} exam",
            Sections =
            [
                new() { Title = "Grammar", Skill = Skill.Grammar, QuestionCount = 5 },
                new() { Title = "Vocabulary", Skill = Skill.Vocabulary, QuestionCount = 5 },
                new() { Title = "Reading", Skill = Skill.Reading, QuestionCount = 3 },
                new() { Title = "Listening", Skill = Skill.Listening, QuestionCount = 3 },
                new() { Title = "Writing", Skill = Skill.Writing, QuestionCount = 1 },
                new() { Title = "Speaking", Skill = Skill.Speaking, QuestionCount = 1 }
            ]
        };
        return await BuildAsync(input, ContentStatus.Draft, ct);
    }

    public async Task<ExamEditorModel?> RebuildAsync(ExamInput input, CancellationToken ct = default)
    {
        var status = input.Id == 0 ? ContentStatus.Draft
            : await db.Quizzes.Where(q => q.Id == input.Id).Select(q => q.Status).FirstOrDefaultAsync(ct);
        return await BuildAsync(input, status, ct);
    }

    private async Task<ExamEditorModel?> BuildAsync(ExamInput input, ContentStatus status, CancellationToken ct)
    {
        var course = await db.Courses.AsNoTracking().Where(c => c.Id == input.CourseId)
            .Select(c => new { c.Title, Language = c.Language.Name, c.LanguageId }).FirstOrDefaultAsync(ct);
        if (course is null) return null;

        var eligible = await db.Questions.AsNoTracking()
            .Where(q => !q.IsDeleted && q.Status == ContentStatus.Published && q.LanguageId == course.LanguageId &&
                        (q.CourseId == input.CourseId || q.CourseId == null))
            .OrderBy(q => q.Skill).ThenBy(q => q.Id)
            .Select(q => new EligibleQuestion(q.Id, q.Stem, q.Type, q.Skill, q.Level,
                (q.Module != null && q.Module.MonthNo == input.MonthNo) || (q.Lecture != null && q.Lecture.Module.MonthNo == input.MonthNo)))
            .Take(1000).ToListAsync(ct);

        var warnings = new List<string>();
        foreach (var s in input.Sections)
        {
            if (s.SelectionMode == SelectionMode.Fixed)
            {
                if (s.QuestionIds.Count == 0) warnings.Add($"\"{s.Title}\": pick at least one question.");
                continue;
            }
            var month = eligible.Count(q => q.InMonth && (s.Skill is null || q.Skill == s.Skill) && (s.QuestionType is null || q.Type == s.QuestionType));
            var any = eligible.Count(q => (s.Skill is null || q.Skill == s.Skill) && (s.QuestionType is null || q.Type == s.QuestionType));
            if (any < s.QuestionCount)
                warnings.Add($"\"{s.Title}\": only {any} published question(s) match, {s.QuestionCount} needed.");
            else if (month < s.QuestionCount)
                warnings.Add($"\"{s.Title}\": {month} question(s) from month {input.MonthNo}; the rest will come from other months.");
        }

        var attempts = input.Id == 0 ? 0 : await db.QuizAttempts.CountAsync(a => a.QuizId == input.Id, ct);
        return new ExamEditorModel(input, status, course.Title, course.Language, attempts,
            await settings.GetIntAsync(SettingKeys.MonthlyExamPassMark, 60, ct), eligible, warnings);
    }

    public async Task<Result<int>> SaveAsync(ExamInput input, CancellationToken ct = default)
    {
        var sections = input.Sections.Where(s => !string.IsNullOrWhiteSpace(s.Title)).ToList();
        if (sections.Count == 0) return Error.Validation("Sections", "Add at least one section.");
        if (!await db.Courses.AnyAsync(c => c.Id == input.CourseId, ct)) return Error.Validation("CourseId", "Choose a course.");
        if (await db.Quizzes.AnyAsync(q => q.Id != input.Id && q.CourseId == input.CourseId && q.Type == QuizType.Monthly && q.MonthNo == input.MonthNo, ct))
            return Error.Validation("MonthNo", $"This course already has an exam for month {input.MonthNo}.");

        var allIds = sections.SelectMany(s => s.QuestionIds).Distinct().ToList();
        var known = await db.Questions.Where(q => allIds.Contains(q.Id) && !q.IsDeleted).Select(q => q.Id).ToListAsync(ct);

        Quiz quiz;
        if (input.Id == 0)
        {
            quiz = new Quiz { Type = QuizType.Monthly, Status = ContentStatus.Draft };
            db.Quizzes.Add(quiz);
        }
        else
        {
            quiz = await db.Quizzes.Include(q => q.Sections).ThenInclude(s => s.Questions)
                .FirstOrDefaultAsync(q => q.Id == input.Id && q.Type == QuizType.Monthly, ct) ?? throw new KeyNotFoundException();
            foreach (var s in quiz.Sections) db.QuizQuestions.RemoveRange(s.Questions);
            db.QuizSections.RemoveRange(quiz.Sections);
            quiz.Sections.Clear();
        }

        quiz.CourseId = input.CourseId;
        quiz.Title = input.Title.Trim();
        quiz.Instructions = string.IsNullOrWhiteSpace(input.Instructions) ? null : input.Instructions.Trim();
        quiz.MonthNo = input.MonthNo;
        quiz.TimeLimitMinutes = input.TimeLimitMinutes;
        quiz.PassMark = input.PassMark;

        var order = 0;
        foreach (var s in sections)
        {
            var section = new QuizSection
            {
                Title = s.Title.Trim(), Skill = s.Skill, QuestionType = s.QuestionType, SelectionMode = s.SelectionMode,
                QuestionCount = Math.Clamp(s.QuestionCount, 1, 100), SortOrder = ++order
            };
            if (s.SelectionMode == SelectionMode.Fixed)
            {
                var qOrder = 0;
                foreach (var id in s.QuestionIds.Distinct().Where(known.Contains))
                    section.Questions.Add(new QuizQuestion { QuestionId = id, SortOrder = ++qOrder });
                section.QuestionCount = Math.Max(1, section.Questions.Count);
            }
            quiz.Sections.Add(section);
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "Exam", quiz.Id.ToString(), ct: ct);
        return quiz.Id;
    }

    public async Task<Result> PublishAsync(int id, CancellationToken ct = default)
    {
        var editor = await GetEditorAsync(id, ct);
        if (editor is null) return Error.NotFound("Exam");
        var empty = editor.Input.Sections.Count == 0 ||
                    editor.Warnings.Any(w => w.Contains("pick at least one") || w.Contains("only 0 published"));
        if (empty) return Error.Validation("Sections", "Every section needs questions before the exam can be published. " + string.Join(" ", editor.Warnings));
        var quiz = await db.Quizzes.FirstAsync(q => q.Id == id, ct);
        quiz.Status = ContentStatus.Published;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Publish", "Exam", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> UnpublishAsync(int id, CancellationToken ct = default)
    {
        var quiz = await db.Quizzes.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (quiz is null) return Error.NotFound("Exam");
        if (await db.QuizAttempts.AnyAsync(a => a.QuizId == id && a.Status == AttemptStatus.InProgress, ct))
            return Error.Conflict("exam_in_use", "Learners are taking this exam right now. Try again later.");
        quiz.Status = ContentStatus.Draft;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Unpublish", "Exam", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id, CancellationToken ct = default)
    {
        var quiz = await db.Quizzes.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (quiz is null) return Error.NotFound("Exam");
        if (await db.QuizAttempts.AnyAsync(a => a.QuizId == id, ct))
            return Error.Conflict("exam_has_attempts", "Learners have taken this exam, so it can only be unpublished, not deleted.");
        db.Quizzes.Remove(quiz);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "Exam", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<DailyQuizRuleInput> GetDailyRuleAsync(CancellationToken ct = default) => new()
    {
        FromRecent = await settings.GetIntAsync(SettingKeys.DailyQuizFromTodayCount, 3, ct),
        FromWeek = await settings.GetIntAsync(SettingKeys.DailyQuizFromWeekCount, 3, ct),
        FromReview = await settings.GetIntAsync(SettingKeys.DailyQuizReviewCount, 2, ct),
        PlacementPerLevel = await settings.GetIntAsync(SettingKeys.PlacementQuestionsPerLevel, 2, ct)
    };

    public async Task<Result> SaveDailyRuleAsync(DailyQuizRuleInput input, CancellationToken ct = default)
    {
        if (input.FromRecent + input.FromWeek + input.FromReview == 0)
            return Error.Validation("FromRecent", "The daily quiz needs at least one question.");
        foreach (var (key, value) in new[]
                 {
                     (SettingKeys.DailyQuizFromTodayCount, input.FromRecent), (SettingKeys.DailyQuizFromWeekCount, input.FromWeek),
                     (SettingKeys.DailyQuizReviewCount, input.FromReview), (SettingKeys.PlacementQuestionsPerLevel, input.PlacementPerLevel)
                 })
        {
            var result = await settings.SetAsync(key, value.ToString(), ct);
            if (result.IsFailure) return result;
        }
        return Result.Success();
    }
}

// ===================================================================== grading queue and marks (FR-A80, FR-A81, BR-14)

public interface IGradingService
{
    Task<IReadOnlyList<GradingQueueItem>> QueueAsync(CancellationToken ct = default);
    Task<int> PendingCountAsync(CancellationToken ct = default);
    Task<Result> GradeAsync(int answerId, decimal score, string? feedback, CancellationToken ct = default);
    Task<Result> OverrideAsync(int answerId, decimal newScore, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<AttemptListItem>> AttemptsForLearnerAsync(Guid userId, CancellationToken ct = default);
    Task<AttemptDetailModel?> AttemptDetailAsync(int attemptId, CancellationToken ct = default);
}

internal sealed class GradingService(IApplicationDbContext db, ICurrentUser currentUser, IAuditLogger audit, TimeProvider clock,
    Progress.IProgressService progress) : IGradingService
{
    public async Task<IReadOnlyList<GradingQueueItem>> QueueAsync(CancellationToken ct = default)
    {
        var rows = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.NeedsReview && a.Attempt.Status == AttemptStatus.PendingReview)
            .OrderBy(a => a.Attempt.SubmittedAt).ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id, a.AttemptId, a.Attempt.UserId, Course = a.Attempt.Course.Title, a.Attempt.Title, a.Attempt.Type,
                QType = a.Question.Type, a.Question.Stem, a.Question.Prompt, a.AnswerText, a.AudioUrl, a.MaxScore, a.Attempt.SubmittedAt,
                a.AiScore, a.AiConfidence, a.AiFeedback
            }).Take(200).ToListAsync(ct);
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var u = users.FirstOrDefault(x => x.Id == r.UserId);
            return new GradingQueueItem(r.Id, r.AttemptId, u?.FullName ?? "Learner", u?.Email ?? "", r.Course, r.Title, r.Type, r.QType,
                r.Stem, r.Prompt, r.AnswerText, r.AudioUrl, r.MaxScore, r.SubmittedAt, r.AiScore, r.AiConfidence, r.AiFeedback);
        }).ToList();
    }

    public Task<int> PendingCountAsync(CancellationToken ct = default) =>
        db.AttemptAnswers.CountAsync(a => a.NeedsReview && a.Attempt.Status == AttemptStatus.PendingReview, ct);

    public async Task<Result> GradeAsync(int answerId, decimal score, string? feedback, CancellationToken ct = default)
    {
        var answer = await LoadAnswerAsync(answerId, ct);
        if (answer is null || !answer.NeedsReview) return Error.NotFound("Answer waiting for grading");
        if (score < 0 || score > answer.MaxScore) return Error.Validation("Score", $"Give 0 to {answer.MaxScore} points.");

        answer.Score = Math.Round(score, 2);
        answer.IsCorrect = score * 2 >= answer.MaxScore;
        answer.NeedsReview = false;
        answer.Feedback = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
        answer.ReviewedBy = currentUser.UserId;
        answer.ReviewedByEmail = currentUser.Email;
        answer.ReviewedAt = clock.GetUtcNow();
        AttemptScoring.CountForAnalytics(answer);
        if (answer.Attempt.Status != AttemptStatus.InProgress) AttemptScoring.Recalculate(answer.Attempt);

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Grade", "AttemptAnswer", answerId.ToString(), null, new { score, feedback }, ct);
        if (answer.Attempt.Status == AttemptStatus.Graded) await progress.OnAttemptClosedAsync(answer.AttemptId, ct);
        return Result.Success();
    }

    public async Task<Result> OverrideAsync(int answerId, decimal newScore, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Error.Validation("Reason", "A reason is required (BR-14).");
        var answer = await LoadAnswerAsync(answerId, ct);
        if (answer is null) return Error.NotFound("Answer");
        if (answer.Attempt.Status == AttemptStatus.InProgress) return Error.Conflict("attempt_open", "The attempt is still in progress.");
        if (newScore < 0 || newScore > answer.MaxScore) return Error.Validation("Score", $"Give 0 to {answer.MaxScore} points.");

        var old = answer.Score;
        db.MarkOverrides.Add(new MarkOverride
        {
            AttemptAnswerId = answer.Id, OldScore = old, NewScore = Math.Round(newScore, 2), Reason = reason.Trim(),
            ChangedBy = currentUser.UserId, ChangedByEmail = currentUser.Email, ChangedAt = clock.GetUtcNow()
        });
        answer.Score = Math.Round(newScore, 2);
        answer.IsCorrect = newScore * 2 >= answer.MaxScore;
        if (answer.NeedsReview)
        {
            answer.NeedsReview = false;
            answer.ReviewedBy = currentUser.UserId;
            answer.ReviewedByEmail = currentUser.Email;
            answer.ReviewedAt = clock.GetUtcNow();
        }
        AttemptScoring.Recalculate(answer.Attempt);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("OverrideMark", "AttemptAnswer", answerId.ToString(), new { score = old }, new { score = newScore, reason }, ct);
        // A raised mark can pass an exam (points, certificate); a lowered one doesn't take points back automatically (FR-S10: admins revoke).
        if (answer.Attempt.Status == AttemptStatus.Graded) await progress.OnAttemptClosedAsync(answer.AttemptId, ct);
        return Result.Success();
    }

    private Task<AttemptAnswer?> LoadAnswerAsync(int answerId, CancellationToken ct) =>
        db.AttemptAnswers.Include(a => a.Question).Include(a => a.Attempt).ThenInclude(x => x.Answers)
            .FirstOrDefaultAsync(a => a.Id == answerId, ct);

    public async Task<IReadOnlyList<AttemptListItem>> AttemptsForLearnerAsync(Guid userId, CancellationToken ct = default) =>
        await db.QuizAttempts.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.Id).Take(200)
            .Select(a => new AttemptListItem(a.Id, a.Title, a.Type, a.Course.Title, a.StartedAt, a.SubmittedAt, a.Status, (double)a.Score,
                a.MaxScore, a.Percentage, a.IsPassed, a.IsPractice, a.BackgroundEvents))
            .ToListAsync(ct);

    public async Task<AttemptDetailModel?> AttemptDetailAsync(int attemptId, CancellationToken ct = default)
    {
        var attempt = await db.QuizAttempts.AsNoTracking().Include(a => a.Course)
            .Include(a => a.Answers).ThenInclude(a => a.Question).ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is null) return null;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == attempt.UserId).Select(u => new { u.FullName, u.Email }).FirstOrDefaultAsync(ct);
        var answerIds = attempt.Answers.Select(a => a.Id).ToList();
        var overrides = await db.MarkOverrides.AsNoTracking().Where(m => answerIds.Contains(m.AttemptAnswerId)).OrderBy(m => m.Id).ToListAsync(ct);

        var answers = attempt.Answers.OrderBy(a => a.SortOrder).Select((a, i) => new AttemptAnswerItem(
            a.Id, i + 1, a.SectionTitle, a.Question.Type, a.Question.Skill, a.Question.Stem, a.Question.Prompt, Describe(a), a.AudioUrl,
            AnswerGrader.CorrectAnswerText(a.Question), a.IsCorrect, a.Score, a.MaxScore, a.NeedsReview, a.Feedback, a.ReviewedByEmail,
            overrides.Where(m => m.AttemptAnswerId == a.Id)
                .Select(m => new MarkOverrideItem(m.OldScore, m.NewScore, m.Reason, m.ChangedByEmail, m.ChangedAt)).ToList())).ToList();

        var header = new AttemptListItem(attempt.Id, attempt.Title, attempt.Type, attempt.Course.Title, attempt.StartedAt, attempt.SubmittedAt,
            attempt.Status, (double)attempt.Score, attempt.MaxScore, attempt.Percentage, attempt.IsPassed, attempt.IsPractice, attempt.BackgroundEvents);
        return new AttemptDetailModel(header, attempt.UserId, user?.FullName ?? "Learner", user?.Email ?? "", answers);
    }

    private static string Describe(AttemptAnswer a)
    {
        if (a.AnsweredAt is null) return "(no answer)";
        var q = a.Question;
        return q.Type switch
        {
            QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose =>
                string.Join(", ", q.Options.Where(o => GradeRules.ParseCsv(a.SelectedOptionIds).Contains(o.Id)).Select(o => o.Text)),
            QuestionType.MatchPairs => string.Join("; ", GradeRules.ParsePairs(a.MatchAnswer)
                .Select(p => $"{q.Options.FirstOrDefault(o => o.Id == p.Key)?.Text} → {p.Value}")),
            QuestionType.Speak => a.AudioUrl is null ? "(no recording)" : "Recording",
            _ => a.AnswerText ?? "(no answer)"
        };
    }
}
