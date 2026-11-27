using FluentValidation;
using LinguaPath.Shared.Contracts.Content;

namespace LinguaPath.Application.Validation;

public sealed class LectureProgressRequestValidator : AbstractValidator<LectureProgressRequest>
{
    public LectureProgressRequestValidator()
    {
        RuleFor(x => x.LearnerCourseId).GreaterThan(0);
        RuleFor(x => x.Seconds).InclusiveBetween(0, 3600);
    }
}

public sealed class CompleteLectureRequestValidator : AbstractValidator<CompleteLectureRequest>
{
    public CompleteLectureRequestValidator()
    {
        RuleFor(x => x.LearnerCourseId).GreaterThan(0);
        RuleFor(x => x.Seconds).InclusiveBetween(0, 3600);
    }
}

public sealed class SaveNoteRequestValidator : AbstractValidator<SaveNoteRequest>
{
    public SaveNoteRequestValidator() => RuleFor(x => x.Text).MaximumLength(2000).WithMessage("Notes can be up to 2,000 characters.");
}

public sealed class RateLectureRequestValidator : AbstractValidator<RateLectureRequest>
{
    public RateLectureRequestValidator()
    {
        RuleFor(x => x.Stars).InclusiveBetween(1, 5).WithMessage("Give 1 to 5 stars.");
        RuleFor(x => x.Comment).MaximumLength(500);
    }
}

public sealed class ReportContentRequestValidator : AbstractValidator<ReportContentRequest>
{
    public ReportContentRequestValidator() =>
        RuleFor(x => x.Message).NotEmpty().WithMessage("Tell us what's wrong.")
            .MinimumLength(5).WithMessage("Please describe the problem in a few words.")
            .MaximumLength(1000);
}
