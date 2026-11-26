using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.WebAPI.Setup;

public enum UpgradeDecision { Disabled, FreshDatabase, SameVersion, Downgrade, Upgrade }

/// <summary>
/// 容器啟動時判斷「這次啟動是不是升級」（安裝版設計 D-17）：是就先備份、成功才 migration。
/// 只有 compose 會設 <see cref="EnabledKey"/>；Windows、Ubuntu、Render、<c>dotnet run</c> 都是 <see cref="UpgradeDecision.Disabled"/>、行為不變
/// （它們的升級前備份由安裝程式呼叫 <c>--backup-now</c>）。
/// 版本標記檔 <see cref="VersionMarkerFile"/> 放在資料目錄，內容是上次完整啟動成功的 <c>BuildInfo.Version</c>。
/// 新版的 migration 若改過備份紀錄表的結構，升級前備份在舊 schema 上會丟例外，由 <c>PreUpgradeBackup.ExecuteAsync</c> 接成 1／<c>unexpected</c>，
/// 結果是進維護頁：不會在沒有備份的情況下升級，也不會讓程序結束、容器反覆重啟。
/// </summary>
public static class UpgradeGuard
{
    public const string EnabledKey = "Install:BackupBeforeUpgrade";
    public const string VersionMarkerFile = "app-version";

    public static async Task<UpgradeDecision> DecideAsync(DynamicDbContext db, IConfiguration c, string currentVersion, CancellationToken ct)
    {
        var dataDirectory = c[InstallConfig.DataDirectoryKey];
        if (!c.GetValue<bool>(EnabledKey) || string.IsNullOrWhiteSpace(dataDirectory)) return UpgradeDecision.Disabled;

        if (!(await db.Database.GetAppliedMigrationsAsync(ct)).Any()) return UpgradeDecision.FreshDatabase;
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) return UpgradeDecision.Upgrade;

        var marker = ReadMarker(dataDirectory);
        // 這版功能上線前建的 volume 沒有標記檔：保守備份一次
        if (marker is null) return UpgradeDecision.Upgrade;
        if (marker == currentVersion) return UpgradeDecision.SameVersion;
        if (SemVer.Parse(marker) is not { } was || SemVer.Parse(currentVersion) is not { } now) return UpgradeDecision.Upgrade;
        var order = SemVer.Compare(now, was);
        // 只在映像版本比資料庫新時備份；換回舊映像不備份，免得升級後備份位置壞掉時連退版都被維護頁擋住
        return order > 0 ? UpgradeDecision.Upgrade : order < 0 ? UpgradeDecision.Downgrade : UpgradeDecision.SameVersion;
    }

    public static string? ReadMarker(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, VersionMarkerFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>完整啟動成功後寫；只在 <see cref="UpgradeDecision.Disabled"/> 以外寫（<c>Downgrade</c> 也寫成目前版本）。權限同設定碼檔。</summary>
    public static void WriteMarker(string dataDirectory, string version)
    {
        var path = Path.Combine(dataDirectory, VersionMarkerFile);
        File.WriteAllText(path, version);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>SemVer 2.0 的比較（不加套件）：去掉 <c>+</c> 之後的 build metadata；核心版號用 <see cref="Version"/>；沒有 prerelease 的較大。</summary>
    private sealed record SemVer(Version Core, string[] Pre)
    {
        public static SemVer? Parse(string text)
        {
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text[..plus];
            var dash = text.IndexOf('-');
            var core = dash >= 0 ? text[..dash] : text;
            var pre = dash >= 0 ? text[(dash + 1)..] : "";
            // 核心至少 major.minor；不接受 "1" 或帶字母的版本（例如 dev）
            if (!core.Contains('.') || !Version.TryParse(core, out var version)) return null;
            if (dash >= 0 && pre.Length == 0) return null;
            return new SemVer(version, pre.Length == 0 ? [] : pre.Split('.'));
        }

        public static int Compare(SemVer a, SemVer b)
        {
            var core = a.Core.CompareTo(b.Core);
            if (core != 0) return core;
            if (a.Pre.Length == 0 || b.Pre.Length == 0) return b.Pre.Length.CompareTo(a.Pre.Length);
            for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
            {
                var order = ComparePart(a.Pre[i], b.Pre[i]);
                if (order != 0) return order;
            }
            return a.Pre.Length.CompareTo(b.Pre.Length);
        }

        private static int ComparePart(string a, string b)
        {
            var numA = a.All(char.IsAsciiDigit);
            var numB = b.All(char.IsAsciiDigit);
            if (numA && numB)
            {
                // 數字段比大小：去前導零後先比長度再比字串，不受 long 範圍限制
                var x = a.TrimStart('0');
                var y = b.TrimStart('0');
                return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            if (numA != numB) return numA ? -1 : 1;   // 數字段小於文字段
            return string.CompareOrdinal(a, b);
        }
    }
}
