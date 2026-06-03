using ManufacturingAI.API.Controllers;
using FluentValidation;

namespace ManufacturingAI.API.Validators;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
    }
}

public class FeedbackRequestValidator : AbstractValidator<FeedbackRequest>
{
    public FeedbackRequestValidator()
    {
        RuleFor(x => x.Feedback).IsInEnum();
    }
}

public class TriggerSyncRequestValidator : AbstractValidator<TriggerSyncRequest>
{
    public TriggerSyncRequestValidator()
    {
        // ConnectorId is optional
    }
}

public class GenerateTestScriptRequestValidator : AbstractValidator<GenerateTestScriptRequest>
{
    public GenerateTestScriptRequestValidator()
    {
        RuleFor(x => x.ScriptType).NotEmpty()
            .Must(t => new[] { "python", "csv", "robotframework" }.Contains(t))
            .WithMessage("ScriptType must be python, csv, or robotframework.");
    }
}
