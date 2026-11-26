using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

public class UpdateModuleValidator : AbstractValidator<UpdateModuleRequest>
{
    public UpdateModuleValidator(RequiredTextGuard guard, IMessageCatalog messages, RequestLocale locale)
    {
        RuleFor(x => x.Name).ValidModuleName(guard, isNew: false);
        RuleFor(x => x.Section).ValidModuleSection(() => messages.Get(locale.Current, MessageText.ModuleSectionZhRequired));
    }
}
