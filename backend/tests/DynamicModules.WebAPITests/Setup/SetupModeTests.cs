using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>設定模式（D-11、D-14）：不連資料庫，只開精靈、health、version 與內建介面文字。</summary>
public class SetupModeTests
{
    private static async Task<string?> ErrorCode(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Status_InSetupMode_IsPending()
    {
        using var f = new SetupHostFactory();
        var res = await f.CreateClient().GetAsync("/api/setup/status");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("setupPending").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NormalMode_SetupEndpoints_Are404_AC04()
    {
        using var app = new TestApp(seedDemoPeople: false);
        var client = app.CreateClient();
        (await client.GetAsync("/api/setup/status")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/setup/verify", new { code = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/setup/complete", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RightCode_Verifies()
    {
        using var f = new SetupHostFactory();
        var res = await f.CreateClient().PostAsJsonAsync("/api/setup/verify", new { code = f.Code.ToLowerInvariant() });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("")]
    public async Task WrongCode_Is403_AC10(string code)
    {
        using var f = new SetupHostFactory();
        var client = f.CreateClient();

        var res = await client.PostAsJsonAsync("/api/setup/verify", new { code });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(res)).Should().Be("setupCodeInvalid");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/setup/checks");
        req.Headers.Add("X-Setup-Code", code);
        var other = await client.SendAsync(req);
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(other)).Should().Be("setupCodeInvalid");
    }

    [Fact]
    public async Task SetupMode_OtherApi_Is503_AndNoDatabaseCreated()
    {
        using var f = new SetupHostFactory();
        var client = f.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v3/dynamic/auth/login", new { userName = "admin", password = "x" });
        var me = await client.GetAsync("/api/v3/dynamic/me");

        foreach (var res in new[] { login, me })
        {
            res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await ErrorCode(res)).Should().Be("setupRequired");
        }
        File.Exists(Path.Combine(f.DataDirectory, "formwright.db")).Should().BeFalse();
        f.Logs.Entries.Should().NotContain(e => e.Message.Contains("已產生初始管理員密碼"));
    }

    [Fact]
    public async Task SetupMode_Health_And_Version()
    {
        using var f = new SetupHostFactory();
        var client = f.CreateClient();
        (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        var ready = await client.GetAsync("/api/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("setupRequired");
        (await client.GetAsync("/api/version")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetupMode_ServesBuiltinUiMessages()
    {
        using var f = new SetupHostFactory();
        var client = f.CreateClient();
        var (key, value) = FirstLeaf(Path.Combine(FindRepoRoot(), "shared", "i18n", "en-US", "ui.json"));

        var res = await client.GetAsync("/api/v3/dynamic/i18n/ui/en-US");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var flat = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        flat![key].Should().Be(value);
        (await client.GetAsync("/api/v3/dynamic/i18n/ui/xx-XX")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>D-11：Windows／Ubuntu 的設定碼由安裝程式顯示，不寫 log（Windows 預設會進本機互動使用者讀得到的事件記錄）。</summary>
    [Fact]
    public void SetupMode_NativeInstall_DoesNotLogCode()
    {
        using var f = new SetupHostFactory();
        f.CreateClient();
        f.Logs.Entries.Should().NotContain(e => e.Message.Contains(f.Code));
    }

    /// <summary>Docker 沒有安裝程式可顯示，設定碼只能從啟動 log 看到。</summary>
    [Fact]
    public void SetupMode_Container_LogsCode()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fw-ctr-{Guid.NewGuid():N}");
        try
        {
            using var f = new ContainerFactory(root);
            f.CreateClient();
            var code = DynamicModules.WebAPI.Setup.SetupCode.Read(Path.Combine(root, "data"))!;
            f.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(code));
        }
        finally { try { Directory.Delete(root, true); } catch { /* 暫存 */ } }
    }

    [Fact]
    public async Task Container_InitIfMissing_ThenSetupMode()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fw-ctr-{Guid.NewGuid():N}");
        try
        {
            using var f = new ContainerFactory(root);
            var res = await f.CreateClient().GetAsync("/api/setup/status");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(Path.Combine(root, "data", DynamicModules.WebAPI.Setup.SetupCode.FileName)).Should().BeTrue();
        }
        finally { try { Directory.Delete(root, true); } catch { /* 暫存 */ } }
    }

    private sealed class ContainerFactory(string root) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        public TestLogSink Logs { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureLogging(l => l.AddProvider(Logs));
            builder.UseSetting(DynamicModules.WebAPI.Setup.InstallConfig.ConfigFileKey, Path.Combine(root, "data", "formwright.json"));
            builder.UseSetting(DynamicModules.WebAPI.Setup.InstallConfig.DataDirectoryKey, Path.Combine(root, "data"));
            builder.UseSetting(DynamicModules.WebAPI.Setup.InstallConfig.BackupDirectoryKey, Path.Combine(root, "backup"));
            builder.UseSetting(DynamicModules.WebAPI.Setup.InstallConfig.InitIfMissingKey, "true");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared", "i18n"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 shared/i18n");
    }

    private static (string Key, string Value) FirstLeaf(string file)
    {
        var el = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        var path = new List<string>();
        while (el.ValueKind == JsonValueKind.Object)
        {
            var p = el.EnumerateObject().First();
            path.Add(p.Name);
            el = p.Value;
        }
        return (string.Join('.', path), el.GetString()!);
    }
}
