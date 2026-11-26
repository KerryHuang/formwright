using System.Text.Json.Nodes;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DynamicModules.WebAPITests.Setup;

public class InstallInitializerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"fw-ii-{Guid.NewGuid():N}");
    private string Data => Path.Combine(_tmp, "data");
    private string Backup => Path.Combine(_tmp, "backup");
    private string ConfigFile => Path.Combine(_tmp, "etc", "formwright.json");

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* 暫存 */ }
    }

    private IConfiguration Config(bool withBackup = true, string? port = "8080")
    {
        var d = new Dictionary<string, string?>
        {
            [InstallConfig.ConfigFileKey] = ConfigFile,
            [InstallConfig.DataDirectoryKey] = Data,
        };
        if (withBackup) d[InstallConfig.BackupDirectoryKey] = Backup;
        if (port is not null) d[ListenAddress.PortKey] = port;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    [Fact]
    public void FreshInit_WritesConfigAndCode()
    {
        var output = new StringWriter();
        InstallInitializer.Run(Config(), output).Should().Be(0);

        var json = JsonNode.Parse(File.ReadAllText(ConfigFile))!;
        ((bool)json["Setup"]!["Pending"]!).Should().BeTrue();
        ((string)json["ConnectionStrings"]!["Dynamic"]!).Should().Be($"Data Source={Path.Combine(Data, "formwright.db")}");
        ((string)json["Backup"]!["Root"]!).Should().Be(Backup);
        ((int)json["Server"]!["Port"]!).Should().Be(8080);
        var code = SetupCode.Read(Data);
        code.Should().NotBeNull();
        output.ToString().Should().Contain($"FORMWRIGHT_SETUP_CODE={code}");
        Directory.Exists(Data).Should().BeTrue();
        Directory.Exists(Backup).Should().BeTrue();
    }

    [Fact]
    public void ExistingConfig_IsUntouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        const string original = """{"Server":{"Port":9000}}""";
        File.WriteAllText(ConfigFile, original);
        var output = new StringWriter();

        InstallInitializer.Run(Config(), output).Should().Be(0);

        output.ToString().Should().Contain("FORMWRIGHT_EXISTING_INSTALL");
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(Path.Combine(Data, SetupCode.FileName)).Should().BeFalse();
    }

    [Fact]
    public void MissingKey_Is64()
    {
        var output = new StringWriter();
        InstallInitializer.Run(Config(withBackup: false), output).Should().Be(64);
        output.ToString().Should().Contain("Install:BackupDirectory");
    }

    [Fact]
    public void NoPort_OmitsServerSection()
    {
        InstallInitializer.Run(Config(port: null), new StringWriter()).Should().Be(0);
        JsonNode.Parse(File.ReadAllText(ConfigFile))!.AsObject().ContainsKey("Server").Should().BeFalse();
    }

    [Fact(Skip = "Task 3")]
    public void Container_InitIfMissing_ThenSetupMode()
    {
    }
}
