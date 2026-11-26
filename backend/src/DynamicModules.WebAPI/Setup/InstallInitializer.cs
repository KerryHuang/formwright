using System.Text.Json.Nodes;

namespace DynamicModules.WebAPI.Setup;

/// <summary>
/// <c>--install-init</c>：安裝程式與容器首次啟動呼叫，產生安裝設定檔（<c>Setup:Pending=true</c>）與一次性設定碼。
/// 設定檔已存在就什麼都不改（重裝、升級不會動到既有安裝）。預設路徑由呼叫端（安裝程式、compose）以 <c>Install:*</c> 給，這裡不寫死。
/// </summary>
public static class InstallInitializer
{
    public const string DatabaseFileName = "formwright.db";
    public const string KeysDirectoryName = "keys";
    public const string AttachmentsDirectoryName = "attachments";

    public static int Run(IConfiguration c, TextWriter output)
    {
        foreach (var key in new[] { InstallConfig.ConfigFileKey, InstallConfig.DataDirectoryKey, InstallConfig.BackupDirectoryKey })
        {
            if (string.IsNullOrWhiteSpace(c[key]))
            {
                output.WriteLine($"缺少設定 {key}");
                return 64;
            }
        }
        var configFile = InstallConfig.ConfigFile(c)!;
        if (File.Exists(configFile))
        {
            output.WriteLine("FORMWRIGHT_EXISTING_INSTALL");
            return 0;
        }
        var code = Initialize(c, configFile);
        output.WriteLine($"FORMWRIGHT_SETUP_CODE={code}");
        return 0;
    }

    /// <summary>容器自動初始化：<c>Install:InitIfMissing=true</c> 且設定檔不存在時做同一件事，不結束程序。</summary>
    public static void EnsureForContainer(IConfiguration c, TextWriter output)
    {
        if (!c.GetValue<bool>(InstallConfig.InitIfMissingKey)) return;
        var configFile = InstallConfig.ConfigFile(c);
        if (configFile is null || File.Exists(configFile)) return;
        var code = Initialize(c, configFile);
        output.WriteLine($"FORMWRIGHT_SETUP_CODE={code}");
    }

    private static string Initialize(IConfiguration c, string configFile)
    {
        var data = c[InstallConfig.DataDirectoryKey]!;
        var backup = c[InstallConfig.BackupDirectoryKey]!;
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(backup);

        var root = new JsonObject
        {
            ["Setup"] = new JsonObject { ["Pending"] = true },
            ["Install"] = new JsonObject { ["DataDirectory"] = data },
        };
        if (c[ListenAddress.PortKey] is { Length: > 0 } port)
        {
            if (!int.TryParse(port, out var n) || n is < 1 or > 65535)
                throw new InvalidOperationException($"{ListenAddress.PortKey} 必須是 1–65535 的整數：{port}");
            root["Server"] = new JsonObject { ["Port"] = n };
        }
        root["Database"] = new JsonObject { ["Provider"] = "Sqlite" };
        root["ConnectionStrings"] = new JsonObject { ["Dynamic"] = $"Data Source={Path.Combine(data, DatabaseFileName)}" };
        root["DataProtection"] = new JsonObject { ["KeysDirectory"] = Path.Combine(data, KeysDirectoryName) };
        root["Attachments"] = new JsonObject { ["Root"] = Path.Combine(data, AttachmentsDirectoryName) };
        root["Backup"] = new JsonObject { ["Root"] = backup };

        var code = SetupCode.Ensure(data, out _);
        InstallConfigWriter.WriteAtomic(configFile, root);
        return code;
    }
}
