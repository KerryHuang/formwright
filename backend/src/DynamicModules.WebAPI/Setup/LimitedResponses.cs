using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Localization;
using DynamicModules.WebAPI.Ops;

namespace DynamicModules.WebAPI.Setup;

/// <summary>
/// 受限模式（設定模式、容器維護模式）共用的少數回應：存活、版本、內建介面文字，以及「不能用」的 503。
/// 這兩種模式都沒有資料庫可給 MediatR 管線，所以放 WebAPI。
/// </summary>
public static class LimitedResponses
{
    public static IResult Live() => Results.Json(new { status = "ok" });

    public static IResult Version() =>
        Results.Json(new { version = BuildInfo.Version, commit = BuildInfo.Commit, builtAt = BuildInfo.BuiltAt });

    public static IResult NotReady(string status) =>
        Results.Json(new { status }, statusCode: StatusCodes.Status503ServiceUnavailable);

    public static IResult Unavailable(string code) =>
        Results.Json(new { code }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>內建語系補齊後的介面文字（形狀同一般模式的 <c>GET i18n/ui/{code}</c>）；非內建語系 404。</summary>
    public static IResult BuiltinUiMessages(string localeCode)
    {
        if (!LocaleCode.IsValid(localeCode) || !BuiltinLocaleResources.Ui.TryGetValue(localeCode, out var own))
            return Results.NotFound();
        var layers = new List<IReadOnlyDictionary<string, string>> { own };
        if (localeCode != I18nText.FallbackLocale) layers.Add(BuiltinLocaleResources.Ui[I18nText.FallbackLocale]);
        var merged = UiTextMerge.Merge(BuiltinLocaleResources.BaseKeys, layers);
        var (hash, json) = UiTextMerge.Serialize(merged);
        return new UiMessagesResult(hash, json);
    }

    private sealed class UiMessagesResult(string hash, byte[] json) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers["X-UI-Hash"] = hash;
            return Results.File(json, "application/json; charset=utf-8").ExecuteAsync(httpContext);
        }
    }
}
