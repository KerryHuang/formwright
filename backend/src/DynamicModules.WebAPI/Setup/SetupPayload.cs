using System.Text.Json;

namespace DynamicModules.WebAPI.Setup;

/// <summary>
/// 精靈完成後留給一般啟動的一次性檔案（D-08）：admin 密碼雜湊與網站名稱，放資料目錄；
/// 一般啟動在 migration 之後套用，兩步都成功才刪檔。只存雜湊，不存明文。
/// </summary>
public sealed record SetupPayload(string PasswordHash, string SiteName)
{
    public const string FileName = "setup-pending.json";

    public static SetupPayload? Read(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return null;
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<SetupPayload>(File.ReadAllText(path));
    }

    /// <summary>寫檔，權限同設定碼檔（非 Windows 是 0600，建立時就是）。</summary>
    public static void Write(string dataDirectory, SetupPayload payload)
    {
        Directory.CreateDirectory(dataDirectory);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var writer = new StreamWriter(Path.Combine(dataDirectory, FileName), new System.Text.UTF8Encoding(false), options);
        writer.Write(JsonSerializer.Serialize(payload));
    }

    public static void Delete(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path)) File.Delete(path);
    }
}
