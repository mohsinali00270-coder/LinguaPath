using System.ComponentModel.DataAnnotations;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared.Common;

namespace LinguaPath.Admin.Models;

public class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class LearnerIndexViewModel
{
    public string? Search { get; set; }
    public LearnerStatusFilter Status { get; set; }
    public PagedResult<LearnerListItem> Results { get; set; } = new();
}

public class SettingsViewModel
{
    public IReadOnlyList<IGrouping<string, SystemSetting>> Groups { get; set; } = [];
}

public class AuditLogIndexViewModel
{
    public AuditLogFilter Filter { get; set; } = new(null, null, null, null);
    public IReadOnlyList<string> EntityTypes { get; set; } = [];
    public PagedResult<AuditLogItem> Results { get; set; } = new();
}

public class PagerModel
{
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
    /// <summary>Builds the URL for a page number, keeping the current filters.</summary>
    public Func<int, string> UrlFor { get; set; } = _ => "#";
}

public class LectureIndexViewModel
{
    public int? CourseId { get; set; }
    public LinguaPath.Shared.ContentStatus? Status { get; set; }
    public string? Search { get; set; }
    public PagedResult<LectureListItem> Results { get; set; } = new();
}

/// <summary>Query-string form for the question bank filters.</summary>
public class QuestionFilterForm
{
    public int? LanguageId { get; set; }
    public int? LectureId { get; set; }
    public LinguaPath.Shared.CefrLevel? Level { get; set; }
    public LinguaPath.Shared.Skill? Skill { get; set; }
    public LinguaPath.Shared.QuestionType? Type { get; set; }
    public int? Difficulty { get; set; }
    public LinguaPath.Shared.ContentStatus? Status { get; set; }
    public string? Search { get; set; }
    public bool Flagged { get; set; }
    public int Page { get; set; } = 1;

    public QuestionFilter ToFilter() =>
        new(LanguageId, null, LectureId, Level, Skill, Type, Difficulty, Status, Search, Flagged, Math.Max(1, Page));

    public object RouteValues(int page) => new { LanguageId, LectureId, Level, Skill, Type, Difficulty, Status, Search, Flagged, page };
}

public class QuestionIndexViewModel
{
    public QuestionFilterForm Filter { get; set; } = new();
    public PagedResult<QuestionListItem> Results { get; set; } = new();
}

/// <summary>Phase 9: tournament form. Times are entered in UTC.</summary>
public sealed class TournamentInput
{
    public int Id { get; set; }

    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(120)]
    public string Title { get; set; } = "";

    [System.ComponentModel.DataAnnotations.StringLength(1000)]
    public string? Description { get; set; }

    [System.ComponentModel.DataAnnotations.Display(Name = "Language")]
    public int LanguageId { get; set; }

    public LinguaPath.Shared.CefrLevel Level { get; set; } = LinguaPath.Shared.CefrLevel.A1;

    [System.ComponentModel.DataAnnotations.Display(Name = "Module (question pool, optional)")]
    public int? ModuleId { get; set; }

    [System.ComponentModel.DataAnnotations.Display(Name = "Entry opens (UTC)")]
    public DateTime EntryOpensAtUtc { get; set; } = DateTime.UtcNow.Date.AddDays(1).AddHours(12);

    [System.ComponentModel.DataAnnotations.Display(Name = "Starts (UTC)")]
    public DateTime StartsAtUtc { get; set; } = DateTime.UtcNow.Date.AddDays(3).AddHours(18);

    public int Rounds { get; set; } = 2;

    [System.ComponentModel.DataAnnotations.Display(Name = "Questions per round")]
    public int QuestionCount { get; set; } = 10;

    [System.ComponentModel.DataAnnotations.Display(Name = "Seconds per question")]
    public int SecondsPerQuestion { get; set; } = 15;

    [System.ComponentModel.DataAnnotations.Display(Name = "Max entrants (optional)")]
    public int? MaxEntrants { get; set; }

    [System.ComponentModel.DataAnnotations.Display(Name = "1st prize (points)")]
    public int PrizeFirst { get; set; } = 300;

    [System.ComponentModel.DataAnnotations.Display(Name = "2nd prize")]
    public int PrizeSecond { get; set; } = 200;

    [System.ComponentModel.DataAnnotations.Display(Name = "3rd prize")]
    public int PrizeThird { get; set; } = 100;
}
