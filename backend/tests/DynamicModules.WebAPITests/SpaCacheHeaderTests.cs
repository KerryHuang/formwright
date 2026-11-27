using System.Net;
using DynamicModules.WebAPITests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DynamicModules.WebAPITests;

/// <summary>
/// index.html 不能被瀏覽器啟發式快取（部署後會沿用舊頁面與舊 bundle，設計 2026-11-27 §3）：預設文件、直接請求、SPA fallback
/// 三條路都帶 <c>Cache-Control: no-cache</c>；帶雜湊檔名的 assets 不動。一般模式與設定模式主機各驗一次。
/// </summary>
public class SpaCacheHeaderTests
{
    /// <summary>暫存的內容根目錄，裡面是一份假的前端建置結果。</summary>
    internal sealed class SpaRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fw-spa-{Guid.NewGuid():N}");

        public SpaRoot()
        {
            var assets = System.IO.Path.Combine(Path, "wwwroot", "assets");
            Directory.CreateDirectory(assets);
            File.WriteAllText(System.IO.Path.Combine(Path, "wwwroot", "index.html"), "<!doctype html><title>spa</title>");
            File.WriteAllText(System.IO.Path.Combine(assets, "app-abc123.js"), "console.log(1)");
        }

        public HttpClient ClientOf(WebApplicationFactory<Program> app) => app.WithWebHostBuilder(b => b.UseContentRoot(Path)).CreateClient();

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* 暫存 */ }
        }
    }

    internal static async Task AssertCacheHeadersAsync(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("<title>spa</title>");
        res.Headers.CacheControl.Should().NotBeNull();
        res.Headers.CacheControl!.NoCache.Should().BeTrue();

        var asset = await client.GetAsync("/assets/app-abc123.js");
        asset.StatusCode.Should().Be(HttpStatusCode.OK);
        asset.Headers.CacheControl.Should().BeNull();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/some/spa/route")]
    public async Task IndexHtml_IsNoCache_AssetsUntouched(string path)
    {
        using var root = new SpaRoot();
        using var app = new TestApp();
        await AssertCacheHeadersAsync(root.ClientOf(app), path);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/some/spa/route")]
    public async Task SetupMode_IndexHtml_IsNoCache_AssetsUntouched(string path)
    {
        using var root = new SpaRoot();
        using var app = new SetupHostFactory();
        await AssertCacheHeadersAsync(root.ClientOf(app), path);
    }
}
