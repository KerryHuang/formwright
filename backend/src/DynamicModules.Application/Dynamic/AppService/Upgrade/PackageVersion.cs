namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// manifest.version 的比較（套件更新設計 §7.2）：三段數字依序比大小。格式不合（理論上驗證已擋）的一律排在最小，
/// 所以壞掉的版號不會被當成新版。
/// </summary>
public static class PackageVersion
{
    public static int Compare(string? a, string? b)
    {
        var (x, y) = (Parse(a), Parse(b));
        if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
        for (var i = 0; i < 3; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static long[]? Parse(string? v)
    {
        var parts = (v ?? "").Split('.');
        if (parts.Length != 3) return null;
        var result = new long[3];
        for (var i = 0; i < 3; i++)
            if (!long.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out result[i])) return null;
        return result;
    }
}
