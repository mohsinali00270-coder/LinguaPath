using FluentValidation;
using LinguaPath.Shared.Contracts.Assessment;

namespace LinguaPath.Application.Validation;

public sealed class SaveAnswerRequestValidator : AbstractValidator<SaveAnswerRequest>
{
    public SaveAnswerRequestValidator()
    {
        RuleFor(x => x.QuestionId).GreaterThan(0);
        RuleFor(x => x.Text).MaximumLength(4000).WithMessage("Answers can be up to 4,000 characters.");
        RuleFor(x => x.OptionIds).Must(o => o is null || o.Count <= 20);
        RuleFor(x => x.Pairs).Must(p => p is null || p.Count <= 20);
        RuleForEach(x => x.Pairs).ChildRules(p => p.RuleFor(x => x.Target).MaximumLength(300));
        RuleFor(x => x.TimeSpentSec).InclusiveBetween(0, 3600).When(x => x.TimeSpentSec is not null);
    }
}

public sealed class AttemptEventRequestValidator : AbstractValidator<AttemptEventRequest>
{
    public AttemptEventRequestValidator() =>
        RuleFor(x => x.Kind).NotEmpty().MaximumLength(30);
}
