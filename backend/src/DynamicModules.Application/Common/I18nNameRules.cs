using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 多語名稱（報表、儀表板、任務檢視）與選填多語文字（儀表板卡片標題）共用的格式檢查（名稱多語設計 §7）。
/// 語系鍵只驗代碼格式（沒安裝的語系也收，語系包設計 M1）；拒收 null 值；每語去空白後不超過上限（上限由各功能的常數傳入，技術限制）。
/// 語系鍵數不超過 <see cref="MaxLocales"/>（技術上限，防止一個名稱塞上千個語系放大儲存與回應；名稱多語設計 §15）。
/// </summary>
public static class I18nNameRules
{
    /// <summary>一段多語文字最多幾個語系鍵：系統最多能安裝的語系數（政策 A39 <c>locale.maxInstalled</c> 的技術上限），不另寫一份數字。</summary>
    public static int MaxLocales => PolicyCatalog.LocaleMaxInstalled.Max;

    /// <summary>只檢查格式，不檢查必填語系（選填文字用，如卡片標題 T7）。通過回 null。</summary>
    public static string? CheckShape(I18nText text, int max, string tooLong) => FormatError(text) ?? (TooLong(text, max) ? tooLong : null);

    /// <summary>語系鍵太多、格式不對、或有 null 值時回訊息；否則 null。</summary>
    public static string? FormatError(I18nText text)
    {
        if (text.Values.Count > MaxLocales) return TooManyLocales;
        if (I18nText.InvalidLocale(text.Values.Keys) is { } invalid) return $"不支援的語系：{invalid}";
        return text.Values.FirstOrDefault(kv => kv.Value is null) is { Key: { } nullKey } ? NullValue(nullKey) : null;
    }

    /// <summary>有語系去空白後超過 <paramref name="max"/> 字（先確認沒有 null 值，見 <see cref="FormatError"/>）。</summary>
    public static bool TooLong(I18nText text, int max) => text.Values.Values.Any(v => v.Trim().Length > max);

    /// <summary>
    /// 必填名稱：先檢查格式，再照政策 locale.requiredText 檢查必填語系（<see cref="RequiredTextGuard.Check"/>）。
    /// <paramref name="stored"/> 新增時傳 null；修改時傳資料庫裡的值，內容沒變就不檢查新增的必填語系（政策設計 §3.5）。
    /// </summary>
    public static string? CheckRequired(RequiredTextGuard guard, I18nText? incoming, I18nText? stored, string subject, int max, string tooLong) =>
        (incoming is null ? null : CheckShape(incoming, max, tooLong)) ?? guard.Check(incoming, stored, subject);

    /// <summary>只有繁中的多語文字（舊畫面與應用程式套件送的字串名稱，字串一律當繁中，§6.1）。</summary>
    public static I18nText ZhOnly(string text) => new([KeyValuePair.Create(I18nText.FallbackLocale, text)]);

    public static string NullValue(string locale) => $"語系 {locale} 的文字不能是空值";

    public static string TooManyLocales => $"最多 {MaxLocales} 個語系";
}
