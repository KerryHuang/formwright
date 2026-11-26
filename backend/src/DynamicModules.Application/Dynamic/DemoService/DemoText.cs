using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 種子的四語文字（繁中、英文、泰文、越南文）。th、vi 為機器翻譯，待母語者校對（同內建套件的 x-translation）。</summary>
internal static class DemoText
{
    public static I18nText Of(string zh, string en, string th, string vi) =>
        new([KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en), KeyValuePair.Create("th", th), KeyValuePair.Create("vi", vi)]);
}
