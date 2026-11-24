using FluentValidation;

namespace DynamicModules.Application.Common.Behaviors;

public static class MessageRuleExtensions
{
    /// <summary>驗證訊息走 <see cref="MessageText"/>：WithMessage 給繁中（FluentValidation 本身的結果不變），state 帶鍵讓 <see cref="ValidationBehavior{TReq,TRes}"/> 依語系解析。</summary>
    public static IRuleBuilderOptions<T, TProperty> WithText<T, TProperty>(
        this IRuleBuilderOptions<T, TProperty> rule, string key, params object[] args) =>
        rule.WithMessage(MessageText.Get(null, key, args)).WithState(_ => MessageRef.Of(key, args));

    /// <summary>自訂規則（<c>Custom</c>／<c>CustomAsync</c>）用的失敗，與 <see cref="WithText"/> 同一套：訊息給繁中，state 帶鍵依語系解析。</summary>
    public static FluentValidation.Results.ValidationFailure TextFailure(string propertyPath, string key, params object[] args) =>
        new(propertyPath, MessageText.Get(null, key, args)) { CustomState = MessageRef.Of(key, args) };
}
