namespace DynamicModules.WebAPI.Setup;

/// <summary>聽的位址：<c>Server:Port</c> → 環境變數 <c>PORT</c> → <c>http://127.0.0.1:5180</c>（本機開發）。有 port 時聽所有介面。</summary>
public static class ListenAddress
{
    public const string PortKey = "Server:Port";

    public static string Resolve(IConfiguration c, string? portEnv)
    {
        var configured = c[PortKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!int.TryParse(configured, out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException($"{PortKey} 必須是 1–65535 的整數：{configured}");
            return $"http://0.0.0.0:{port}";
        }
        return portEnv is null ? "http://127.0.0.1:5180" : $"http://0.0.0.0:{portEnv}";
    }
}
