using FluentValidation;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Learning;

namespace LinguaPath.Application.Validation;

internal static class PlanRules
{
    public static readonly int[] DurationMonths = [1, 3, 6, 12];
    public const int MinDailyMinutes = 5;
    public const int MaxDailyMinutes = 240;
}

public sealed class LearningPlanRequestValidator : AbstractValidator<LearningPlanRequest>
{
    public LearningPlanRequestValidator()
    {
        RuleFor(x => x.CourseId).GreaterThan(0).WithMessage("Choose a course.");
        RuleFor(x => x.CurrentLevel).IsInEnum();
        RuleFor(x => x.TargetLevel).IsInEnum()
            .GreaterThanOrEqualTo(x => x.CurrentLevel).WithMessage("Your goal level can't be below your current level.");
        RuleFor(x => x.Goal).IsInEnum();
        RuleFor(x => x.ExamName).MaximumLength(100);
        RuleFor(x => x.DailyMinutes).InclusiveBetween(PlanRules.MinDailyMinutes, PlanRules.MaxDailyMinutes)
            .WithMessage("Daily study time must be between 5 and 240 minutes.");
        RuleFor(x => x.StudyDays).NotEqual(StudyDays.None).WithMessage("Pick at least one study day.")
            .Must(d => ((int)d & ~(int)StudyDays.Everyday) == 0).WithMessage("Unknown study days.");
        RuleFor(x => x.FocusSkills).Must(f => ((int)f & ~(int)FocusSkill.All) == 0).WithMessage("Unknown focus skills.");
        RuleFor(x => x).Must(x => x.DurationMonths is not null ^ x.EndDate is not null)
            .OverridePropertyName("DurationMonths").WithMessage("Choose a duration or a custom end date.");
        RuleFor(x => x.DurationMonths).Must(m => m is null || PlanRules.DurationMonths.Contains(m.Value))
            .WithMessage("Duration must be 1, 3, 6 or 12 months.");
    }
}

public sealed class UpdateLearningPlanRequestValidator : AbstractValidator<UpdateLearningPlanRequest>
{
    public UpdateLearningPlanRequestValidator()
    {
        RuleFor(x => x.TargetLevel).IsInEnum();
        RuleFor(x => x.Goal).IsInEnum();
        RuleFor(x => x.ExamName).MaximumLength(100);
        RuleFor(x => x.DailyMinutes).InclusiveBetween(PlanRules.MinDailyMinutes, PlanRules.MaxDailyMinutes)
            .WithMessage("Daily study time must be between 5 and 240 minutes.");
        RuleFor(x => x.StudyDays).NotEqual(StudyDays.None).WithMessage("Pick at least one study day.")
            .Must(d => ((int)d & ~(int)StudyDays.Everyday) == 0).WithMessage("Unknown study days.");
        RuleFor(x => x.FocusSkills).Must(f => ((int)f & ~(int)FocusSkill.All) == 0).WithMessage("Unknown focus skills.");
        RuleFor(x => x).Must(x => x.DurationMonths is not null ^ x.EndDate is not null)
            .OverridePropertyName("DurationMonths").WithMessage("Choose a duration or a custom end date.");
        RuleFor(x => x.DurationMonths).Must(m => m is null || PlanRules.DurationMonths.Contains(m.Value))
            .WithMessage("Duration must be 1, 3, 6 or 12 months.");
    }
}

public sealed class PauseCourseRequestValidator : AbstractValidator<PauseCourseRequest>
{
    public PauseCourseRequestValidator() =>
        RuleFor(x => x.Days).InclusiveBetween(1, 30).WithMessage("You can pause for 1 to 30 days.");
}

public sealed class CreateLanguageRequestValidator : AbstractValidator<CreateLanguageRequest>
{
    public CreateLanguageRequestValidator()
    {
        RuleFor(x => x.LanguageName).NotEmpty().WithMessage("Which language would you like?").MaximumLength(80);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
