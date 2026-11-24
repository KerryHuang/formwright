using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.AccountService.PasswordReset;
using DynamicModules.Application.Dynamic.PolicyService;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

public class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator(CachedSystemPolicy system)
    {
        RuleFor(x => x.OldPassword).NotEmpty().WithText(MessageText.OldPasswordRequired);
        // 最短長度讀政策 auth.passwordMinLength（帳號安全設計 §7.6）
        RuleFor(x => x.NewPassword).AtLeastPolicyMin(system, MessageText.NewPasswordTooShort);
    }
}
