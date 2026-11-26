using System.Security.Cryptography;

namespace DynamicModules.WebAPI.Setup;

/// <summary>
/// 設定精靈的一次性設定碼（D-11）：存在資料目錄的 <c>setup-code</c>，精靈完成時刪除。
/// 字母表去掉容易看錯的 I、O、0、1，12 碼每 4 碼一組。
/// </summary>
public static class SetupCode
{
    public const string FileName = "setup-code";
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var raw = RandomNumberGenerator.GetString(Alphabet, 12);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    /// <summary>去空白與連字號、轉大寫，照畫面抄的變化都收。</summary>
    public static string Normalize(string? input) =>
        input is null ? "" : new string(input.Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray()).ToUpperInvariant();

    public static bool Matches(string? input, string expected)
    {
        var a = Normalize(input);
        var b = Normalize(expected);
        if (a.Length == 0 || a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    }

    public static string? Read(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : text;
    }

    public static string Ensure(string dataDirectory, out bool created)
    {
        var existing = Read(dataDirectory);
        if (existing is not null) { created = false; return existing; }
        Directory.CreateDirectory(dataDirectory);
        var code = Generate();
        var path = Path.Combine(dataDirectory, FileName);
        // 一次建成 0600，不留 umask 權限的空窗（同 SetupPayload.Write）
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(path, new System.Text.UTF8Encoding(false), options)) writer.Write(code);
        created = true;
        return code;
    }

    public static void Delete(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path)) File.Delete(path);
    }
}
