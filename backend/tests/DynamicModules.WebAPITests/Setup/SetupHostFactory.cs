using DynamicModules.WebAPI.Setup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>設定模式測試主機：用 <c>InstallInitializer.Run</c> 在暫存目錄造設定檔（<c>Setup:Pending=true</c>）與設定碼。</summary>
public sealed class SetupHostFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fw-setup-{Guid.NewGuid():N}");

    public string DataDirectory => Path.Combine(_root, "data");
    public string BackupDirectory => Path.Combine(_root, "backup");
    public string ConfigFile => Path.Combine(_root, "etc", "formwright.json");
    public string Code { get; }
    public TestLogSink Logs { get; } = new();

    public SetupHostFactory()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [InstallConfig.ConfigFileKey] = ConfigFile,
            [InstallConfig.DataDirectoryKey] = DataDirectory,
            [InstallConfig.BackupDirectoryKey] = BackupDirectory,
            [ListenAddress.PortKey] = "8080",
        }).Build();
        var exit = InstallInitializer.Run(config, TextWriter.Null);
        if (exit != 0) throw new InvalidOperationException($"--install-init 回 {exit}");
        Code = SetupCode.Read(DataDirectory)!;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(InstallConfig.ConfigFileKey, ConfigFile);
        builder.ConfigureLogging(l => l.AddProvider(Logs));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) try { Directory.Delete(_root, true); } catch { /* 暫存 */ }
    }
}
