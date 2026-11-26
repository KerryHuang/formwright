namespace DynamicModules.WebAPI.Setup;

/// <summary>
/// 安裝版設定檔（安裝版設計 TD-01）：<c>Install:ConfigFile</c> 指到的 JSON 是最高優先的設定來源，
/// 精靈完成後改的也是這一份。沒設 <c>Install:ConfigFile</c> 的部署（Render Demo、docker run、dotnet run）行為不變。
/// </summary>
public static class InstallConfig
{
    public const string ConfigFileKey = "Install:ConfigFile";
    public const string DataDirectoryKey = "Install:DataDirectory";
    public const string BackupDirectoryKey = "Install:BackupDirectory";
    public const string InitIfMissingKey = "Install:InitIfMissing";
    public const string PendingKey = "Setup:Pending";

    /// <summary>設定檔的絕對路徑；沒設回 null；相對路徑丟例外（工作目錄不可靠，服務啟動時是 System32）。</summary>
    public static string? ConfigFile(IConfiguration c)
    {
        var path = c[ConfigFileKey];
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathRooted(path))
            throw new InvalidOperationException($"{ConfigFileKey} 必須是絕對路徑：{path}");
        return path;
    }

    /// <summary>有設 <c>Install:ConfigFile</c> 時把它加在所有來源最後（優先於環境變數與命令列）；檔案不存在拒絕啟動。</summary>
    public static void AddConfigFile(ConfigurationManager c)
    {
        var path = ConfigFile(c);
        if (path is null) return;
        if (!File.Exists(path))
            throw new InvalidOperationException($"{ConfigFileKey} 指定的設定檔不存在：{path}");
        c.AddJsonFile(path, optional: false, reloadOnChange: false);
    }

    public static bool IsPending(IConfiguration c) => c.GetValue<bool>(PendingKey);
}
