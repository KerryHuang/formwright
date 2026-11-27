using System.Text.Json;

namespace DynamicModules.Domain.ValueObjects;

/// <summary>外觀設定的允許值與出廠預設；比對區分大小寫。出廠預設的唯一來源是 <c>shared/theme/factory-theme.json</c>（csproj 內嵌）。</summary>
public static class ThemeOptions
{
    private static readonly string[] Modes = ["light", "dark", "system"];
    private static readonly string[] Styles = ["workbench", "airy", "enterprise", "industrial", "glass", "bento", "command", "signature"];

    // 要放在 Modes、Styles 之後：static 欄位依宣告順序初始化，讀出廠值時要用它們檢查。
    private static readonly (string Mode, string Style) Factory = ReadFactory();

    public static string FactoryMode => Factory.Mode;
    public static string FactoryStyle => Factory.Style;

    /// <summary>不在允許值就回 null。</summary>
    public static string? NormalizeMode(string? value) => value is not null && Modes.Contains(value) ? value : null;

    public static string? NormalizeStyle(string? value) => value is not null && Styles.Contains(value) ? value : null;

    /// <summary>讀不到或值不在允許清單就拋例外，不退回寫死的後備值。</summary>
    private static (string, string) ReadFactory()
    {
        using var stream = typeof(ThemeOptions).Assembly.GetManifestResourceStream("factory-theme.json")
            ?? throw new InvalidOperationException("缺少內嵌資源 factory-theme.json");
        using var json = JsonDocument.Parse(stream);
        string Read(string key, Func<string?, string?> normalize) => normalize(json.RootElement.GetProperty(key).GetString())
            ?? throw new InvalidOperationException($"factory-theme.json 的 {key} 不在允許值");
        return (Read("mode", NormalizeMode), Read("style", NormalizeStyle));
    }
}
