using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>安裝設定檔 Install:ConfigFile 是最高優先的設定來源（安裝版設計 TD-01）。</summary>
public class InstallConfigTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"fw-ic-{Guid.NewGuid():N}");

    public InstallConfigTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* 暫存 */ }
    }

    private sealed class Factory(string? configFile, Dictionary<string, string?>? extra = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (configFile is not null) builder.UseSetting(InstallConfig.ConfigFileKey, configFile);
            foreach (var (k, v) in extra ?? []) builder.UseSetting(k, v);
        }
    }

    private string WriteConfig(string extraJson = "")
    {
        var path = Path.Combine(_tmp, "formwright.json");
        var db = Path.Combine(_tmp, "fw.db").Replace("\\", "/");
        var keys = Path.Combine(_tmp, "keys").Replace("\\", "/");
        var att = Path.Combine(_tmp, "att").Replace("\\", "/");
        File.WriteAllText(path, $$"""
            {
              "ConnectionStrings": { "Dynamic": "Data Source={{db}}" },
              "DataProtection": { "KeysDirectory": "{{keys}}" },
              "Attachments": { "Root": "{{att}}" }{{extraJson}}
            }
            """);
        return path;
    }

    [Fact]
    public async Task NoConfigFile_StartsAsBefore()
    {
        using var app = new TestApp(seedDemoPeople: false);
        var res = await app.CreateClient().GetAsync("/api/health");
        res.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public void ConfigFile_OverridesEnvironmentStyleSettings()
    {
        var path = WriteConfig(""", "Reports": { "MaxRows": 123 }""");
        using var f = new Factory(path, new() { ["Reports:MaxRows"] = "50000" });
        f.CreateClient();

        f.Services.GetRequiredService<ReportOptions>().MaxRows.Should().Be(123);
        File.Exists(Path.Combine(_tmp, "fw.db")).Should().BeTrue();
    }

    [Fact]
    public void ConfigFile_Missing_RefusesToStart()
    {
        var missing = Path.Combine(_tmp, "nope.json");
        using var f = new Factory(missing);
        var act = () => f.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    [Fact]
    public void ConfigFile_Relative_RefusesToStart()
    {
        using var f = new Factory("formwright.json");
        var act = () => f.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Install:ConfigFile*");
    }
}
