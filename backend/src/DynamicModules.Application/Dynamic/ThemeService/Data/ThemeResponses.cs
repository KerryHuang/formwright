using System.Text.Json.Serialization;

namespace DynamicModules.Application.Dynamic.ThemeService.Data;

/// <summary>全站外觀預設；<paramref name="SiteName"/> 是站台名稱（帳號信的寄件名稱與主旨前綴，§12），沒設為 null。</summary>
public record ThemeDefaultsResponse(string Mode, string Style, string? SiteName = null);

/// <summary>Mode、Style 是個人設定，null 代表跟隨全站預設；Program 全域略過 null，這裡要明確保留。</summary>
public record MeThemeResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Style,
    string EffectiveMode, string EffectiveStyle, string DefaultMode, string DefaultStyle, bool CanSave);
