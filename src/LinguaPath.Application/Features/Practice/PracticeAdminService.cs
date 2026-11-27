using System.ComponentModel.DataAnnotations;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Practice;

// ===================================================================== admin models

public record PracticeItemListItem(
    int Id, PracticeType Type, string Title, string Language, CefrLevel Level, string? Topic, ContentStatus Status, ContentSource Source,
    int Questions, int Attempts, DateTimeOffset UpdatedAt);

public class PracticeQuestionInput
{
    [StringLength(500)] public string? Question { get; set; }
    /// <summary>One option per line.</summary>
    [StringLength(2000)] public string? Options { get; set; }
    /// <summary>1-based in the form, like the option list a teacher sees.</summary>
    [Range(1, 6)] public int Correct { get; set; } = 1;
    [StringLength(500)] public string? Explanation { get; set; }
}

public class PracticeItemInput
{
    public int Id { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Choose a language."), Display(Name = "Language")] public int LanguageId { get; set; }
    public PracticeType Type { get; set; } = PracticeType.Read;
    public CefrLevel Level { get; set; } = CefrLevel.A1;
    [Required, StringLength(200)] public string Title { get; set; } = "";
    [StringLength(100)] public string? Topic { get; set; }
    [Required, StringLength(6000), Display(Name = "Text")] public string Text { get; set; } = "";
    [StringLength(6000)] public string? Translation { get; set; }
    public List<PracticeQuestionInput> Questions { get; set; } = [];
}

public record PracticeItemFilter(int? LanguageId, PracticeType? Type, ContentStatus? Status, string? Search);

/// <summary>Practice library in the admin panel: reviewed like other content (Draft → In review → Approved → Published).</summary>
public interface IPracticeAdminService
{
    Task<IReadOnlyList<PracticeItemListItem>> ListAsync(PracticeItemFilter filter, CancellationToken ct = default);
    Task<PracticeItemInput?> GetAsync(int id, CancellationToken ct = default);
    Task<(ContentStatus Status, ContentSource Source)?> GetStateAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveAsync(PracticeItemInput input, CancellationToken ct = default);
    Task<Result> PublishAsync(int id, CancellationToken ct = default);
    Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default);
    Task<Result> DeleteAsync(int id, CancellationToken ct = default);
}

internal sealed class PracticeAdminService(IApplicationDbContext db, IAuditLogger audit) : IPracticeAdminService
{
    public async Task<IReadOnlyList<PracticeItemListItem>> ListAsync(PracticeItemFilter filter, CancellationToken ct = default)
    {
        var q = db.PracticeItems.AsNoTracking().AsQueryable();
        if (filter.LanguageId is { } l) q = q.Where(p => p.LanguageId == l);
        if (filter.Type is { } t) q = q.Where(p => p.Type == t);
        if (filter.Status is { } s) q = q.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            q = q.Where(p => p.Title.Contains(term) || p.Text.Contains(term) || (p.Topic != null && p.Topic.Contains(term)));
        }
        var rows = await q.OrderBy(p => p.Language.Name).ThenBy(p => p.Type).ThenBy(p => p.Level).ThenByDescending(p => p.Id).Take(500)
            .Select(p => new
            {
                p.Id, p.Type, p.Title, Language = p.Language.Name, p.Level, p.Topic, p.Status, p.Source, p.QuestionsJson,
                Attempts = db.PracticeAttempts.Count(a => a.PracticeItemId == p.Id), Updated = p.UpdatedAt ?? p.CreatedAt
            }).ToListAsync(ct);
        return rows.Select(r => new PracticeItemListItem(r.Id, r.Type, r.Title, r.Language, r.Level, r.Topic, r.Status, r.Source,
            ReadingQuestions.Parse(r.QuestionsJson).Count, r.Attempts, r.Updated)).ToList();
    }

    public async Task<PracticeItemInput?> GetAsync(int id, CancellationToken ct = default)
    {
        var p = await db.PracticeItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : new PracticeItemInput
        {
            Id = p.Id, LanguageId = p.LanguageId, Type = p.Type, Level = p.Level, Title = p.Title, Topic = p.Topic, Text = p.Text,
            Translation = p.Translation,
            Questions = ReadingQuestions.Parse(p.QuestionsJson).Select(q => new PracticeQuestionInput
            {
                Question = q.Question, Options = string.Join("\n", q.Options), Correct = q.Correct + 1, Explanation = q.Explanation
            }).ToList()
        };
    }

    public async Task<(ContentStatus Status, ContentSource Source)?> GetStateAsync(int id, CancellationToken ct = default)
    {
        var p = await db.PracticeItems.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Status, x.Source }).FirstOrDefaultAsync(ct);
        return p is null ? null : (p.Status, p.Source);
    }

    public async Task<Result<int>> SaveAsync(PracticeItemInput input, CancellationToken ct = default)
    {
        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation(nameof(input.LanguageId), "Choose a language.");
        if (input.Type == PracticeType.Conversation)
            return Error.Validation(nameof(input.Type), "Conversation needs no library items; learners pick a topic in the app.");
        if (string.IsNullOrWhiteSpace(input.Text)) return Error.Validation(nameof(input.Text), "Enter the text.");
        var words = Dictation.Words(input.Text).Count;
        if (input.Type is PracticeType.Speak && input.Text.Trim().Length > 300)
            return Error.Validation(nameof(input.Text), "Speaking sentences can be at most 300 characters (one breath, under a minute).");
        if (input.Type is PracticeType.Listen && words > 40)
            return Error.Validation(nameof(input.Text), "Keep dictation to one or two sentences (at most 40 words).");

        var questions = new List<ReadingQuestions.Stored>();
        if (input.Type == PracticeType.Read)
        {
            for (var i = 0; i < input.Questions.Count; i++)
            {
                var q = input.Questions[i];
                if (string.IsNullOrWhiteSpace(q.Question) && string.IsNullOrWhiteSpace(q.Options)) continue;
                var options = (q.Options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (string.IsNullOrWhiteSpace(q.Question) || options.Count < 2)
                    return Error.Validation($"Questions[{i}].Options", $"Question {i + 1} needs its text and at least two options (one per line).");
                if (q.Correct < 1 || q.Correct > options.Count)
                    return Error.Validation($"Questions[{i}].Correct", $"Question {i + 1}: the correct option must be between 1 and {options.Count}.");
                questions.Add(new ReadingQuestions.Stored
                {
                    Question = q.Question.Trim(), Options = options, Correct = q.Correct - 1, Explanation = q.Explanation?.Trim()
                });
            }
        }

        PracticeItem item;
        if (input.Id == 0)
        {
            item = new PracticeItem { Status = ContentStatus.Draft, Source = ContentSource.Manual };
            db.PracticeItems.Add(item);
        }
        else
        {
            item = await db.PracticeItems.FirstOrDefaultAsync(p => p.Id == input.Id, ct) ?? throw new InvalidOperationException();
            if (item.Status == ContentStatus.Archived) return Error.Validation("Status", "Archived items can't be edited.");
            if (item.Status is ContentStatus.Approved or ContentStatus.InReview) item.Status = ContentStatus.Draft; // changes need a new review
        }

        item.LanguageId = input.LanguageId;
        item.Type = input.Type;
        item.Level = input.Level;
        item.Title = input.Title.Trim();
        item.Topic = string.IsNullOrWhiteSpace(input.Topic) ? null : input.Topic.Trim();
        item.Text = input.Text.Trim();
        item.Translation = string.IsNullOrWhiteSpace(input.Translation) ? null : input.Translation.Trim();
        item.QuestionsJson = input.Type == PracticeType.Read ? ReadingQuestions.Serialize(questions) : null;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "PracticeItem", item.Id.ToString(), null,
            new { item.Title, Type = item.Type.ToString(), item.LanguageId, Level = item.Level.ToString() }, ct);
        return item.Id;
    }

    public async Task<Result> PublishAsync(int id, CancellationToken ct = default)
    {
        var item = await db.PracticeItems.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (item is null) return Error.NotFound("Practice item");
        if (item.Status != ContentStatus.Approved) return Error.Validation("Status", "Only approved items can be published. Send it for review first.");
        if (item.Type == PracticeType.Read && ReadingQuestions.Parse(item.QuestionsJson).Count == 0)
            return Error.Validation("Questions", "Reading passages need at least one comprehension question.");
        item.Status = ContentStatus.Published;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Publish", "PracticeItem", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> SetStatusAsync(int id, ContentStatus status, CancellationToken ct = default)
    {
        if (status is not (ContentStatus.Draft or ContentStatus.Archived))
            return Error.Validation("Status", "Use the review workflow to approve or publish.");
        var item = await db.PracticeItems.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (item is null) return Error.NotFound("Practice item");
        item.Status = status;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("SetStatus", "PracticeItem", id.ToString(), null, new { Status = status.ToString() }, ct);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id, CancellationToken ct = default)
    {
        var item = await db.PracticeItems.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (item is null) return Error.NotFound("Practice item");
        item.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "PracticeItem", id.ToString(), new { item.Title }, null, ct);
        return Result.Success();
    }
}
