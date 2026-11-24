using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.PasswordReset;
using DynamicModules.Application.Dynamic.PolicyService;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;

public class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator(CachedSystemPolicy system)
    {
        RuleFor(x => x.Password).AtLeastPolicyMin(system, MessageText.PasswordTooShort);
    }
}
