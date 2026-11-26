using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Data;

public static class ModuleRules
{
    /// <summary>模組代碼（Module.Key）的格式；範本的 key 與模組 code、key 共用。</summary>
    public const string ModuleKeyPattern = "^[a-z][a-z0-9-]{1,39}$";

    public static IRuleBuilderOptions<T, string> ValidModuleKey<T>(this IRuleBuilder<T, string> rule) =>
        rule.Matches(ModuleKeyPattern)
            .WithMessage("模組代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭");

    /// <summary>新增時依政策 locale.requiredText 檢查必填語系；修改時只檢查格式，必填語系由 handler 載入舊值後檢查（§3.5）。</summary>
    public static void ValidModuleName<T>(this IRuleBuilder<T, I18nText> rule, RequiredTextGuard guard, bool isNew) =>
        rule.Custom((v, ctx) =>
        {
            if ((isNew ? guard.Check(v, null, "名稱") : guard.CheckWellFormed(v, "名稱")) is { } error) ctx.AddFailure(error);
        });

    public const int SectionMaxLength = 100;

    /// <summary>
    /// 選單分區：可省略；語系鍵同 <see cref="I18nText.IsWellFormed"/>；拒收 null 值；每個語系值去前後空白後不超過 100 字（同 <see cref="I18nNameRules.TooLong"/>）；
    /// 有任何非空值時繁中必填（分組鍵是繁中，名稱多語設計 §9 第 4 點）。全部語系都空白視同不分區，不算錯。
    /// </summary>
    public static IRuleBuilderOptions<T, I18nText?> ValidModuleSection<T>(this IRuleBuilder<T, I18nText?> rule, Func<string> zhRequiredMessage) =>
        rule.Must(s => UnsupportedLocale(s) is null)
            .WithMessage((_, s) => $"不支援的語系：{UnsupportedLocale(s)}")
            .Must(s => NullValueLocale(s) is null)
            .WithMessage((_, s) => I18nNameRules.NullValue(NullValueLocale(s)!))
            .Must(s => s is null || s.Values.Values.All(v => (v?.Trim().Length ?? 0) <= SectionMaxLength))
            .WithMessage($"選單分區不能超過 {SectionMaxLength} 字")
            .Must(s => s is null || !s.Values.Values.Any(v => !string.IsNullOrWhiteSpace(v))
                || !string.IsNullOrWhiteSpace(s.Values.GetValueOrDefault(I18nText.FallbackLocale)))
            .WithMessage(_ => zhRequiredMessage());

    private static string? NullValueLocale(I18nText? s) => s?.Values.FirstOrDefault(kv => kv.Value is null).Key;

    private static string? UnsupportedLocale(I18nText? s) => s is null ? null : I18nText.InvalidLocale(s.Values.Keys);
}
