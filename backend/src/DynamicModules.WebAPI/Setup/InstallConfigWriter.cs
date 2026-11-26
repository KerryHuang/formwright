using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicModules.WebAPI.Setup;

/// <summary>讀寫安裝設定檔（精靈完成時也用它改檔）；寫入走同目錄暫存檔再覆蓋，不留寫到一半的檔。</summary>
public static class InstallConfigWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public static void WriteAtomic(string path, JsonObject content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        // 一次建成 0600（選 PostgreSQL 時裡面有密碼），不留 umask 權限的空窗
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(tmp, new System.Text.UTF8Encoding(false), options)) writer.Write(content.ToJsonString(Indented));
        File.Move(tmp, path, overwrite: true);
    }
}
