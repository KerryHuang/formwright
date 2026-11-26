using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class ModulesApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;

    public ModulesApiTests(TestApp app) => _admin = app.AsUser("admin");

    [Fact]
    public async Task Create_Update_List_Get_Roundtrip()
    {
        var key = await CreateModule(_admin);

        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "改名" },
            icon = "star",
            section = new Dictionary<string, string> { ["zh-TW"] = "銷售", ["en-US"] = "Sales" }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        got.GetProperty("section").GetProperty("zh-TW").GetString().Should().Be("銷售");
        got.GetProperty("section").GetProperty("en-US").GetString().Should().Be("Sales");
        got.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("改名");
        got.GetProperty("status").GetString().Should().Be("Draft");

        var list = await JsonOf(await _admin.GetAsync($"{Base}/modules"));
        list.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(key);
    }

    [Fact]
    public async Task GetUnknownModule_Returns404()
        => (await _admin.GetAsync($"{Base}/modules/nope-nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Theory]
    [InlineData("zh_TW", "x", "不支援的語系：zh_TW")]
    [InlineData("en-US", null, "選單分區不能超過 100 字")]
    public async Task Update_InvalidSection_Returns400(string locale, string? text, string message)
    {
        var key = await CreateModule(_admin);

        var res = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string> { ["zh-TW"] = "主檔", [locale] = text ?? new string('a', 101) }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain(message);
    }

    [Fact]
    public async Task Section_WithJapanese_Accepted()
    {
        var key = await CreateModule(_admin);

        var res = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string> { ["zh-TW"] = "主檔", ["ja"] = "マスタ" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        got.GetProperty("section").GetProperty("ja").GetString().Should().Be("マスタ");
    }

    [Fact]
    public async Task Create_SectionWithBlankZhTwButOtherLocale_Returns400()
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key = NewKey(), name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string> { ["zh-TW"] = " ", ["en-US"] = "Sales" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("選單分區有其他語系時必須有繁體中文");
    }

    [Fact]
    public async Task Create_SectionAllBlank_HasNoSection()
    {
        var key = NewKey();
        (await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string> { ["zh-TW"] = " ", ["en-US"] = "" }
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).TryGetProperty("section", out var s)
            .Should().BeFalse($"沒有分區時不回 section，實際：{s}");
    }

    // 名稱多語設計 §18.5 M1：舊畫面送字串，當作只有繁中
    [Fact]
    public async Task Update_SectionAsString_IsZhTwOnly()
    {
        var key = await CreateModule(_admin);

        var res = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "x" }, section = "主檔"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var section = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("section");
        section.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().Equal(("zh-TW", "主檔"));
    }

    [Fact]
    public async Task Create_SectionAsString_IsZhTwOnly()
    {
        var key = NewKey();
        (await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "x" }, section = "主檔"
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("section").GetProperty("zh-TW").GetString().Should().Be("主檔");
    }

    // M2
    [Fact]
    public async Task Update_SectionWithoutZhTw_Returns400()
    {
        var key = await CreateModule(_admin);

        var res = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string> { ["en-US"] = "Master" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("選單分區有其他語系時必須有繁體中文");
    }

    // M3
    [Fact]
    public async Task Update_SectionEmptyOtherLocale_IsDropped()
    {
        var key = await CreateModule(_admin);

        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string> { ["zh-TW"] = "主檔", ["en-US"] = "" }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var section = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("section");
        section.EnumerateObject().Select(p => p.Name).Should().Equal("zh-TW");
    }

    [Fact]
    public async Task Update_SectionNullValue_Returns400()
    {
        var key = await CreateModule(_admin);

        var res = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "x" },
            section = new Dictionary<string, string?> { ["zh-TW"] = "主檔", ["en-US"] = null }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("語系 en-US 的文字不能是空值");
    }

    [Fact]
    public async Task CreateDuplicateKey_Returns409()
    {
        var key = await CreateModule(_admin);
        var res = await _admin.PostAsJsonAsync($"{Base}/modules",
            new { key, name = new Dictionary<string, string> { ["zh-TW"] = "x" } });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ActivateThenDeactivate_Succeeds()
    {
        var key = await CreateModule(_admin);
        (await _admin.PostAsync($"{Base}/modules/{key}/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsync($"{Base}/modules/{key}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("status").GetString().Should().Be("Inactive");
    }

    [Fact]
    public async Task ExtensionModule_ApiRoundtrip_ByHostAndSchema()
    {
        var key = NewKey();
        var res = await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "設備擴充" }, kind = "extension", hostEntityKey = "test.entity"
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonOf(res);
        created.GetProperty("kind").GetString().Should().Be("extension");
        created.GetProperty("hostEntityKey").GetString().Should().Be("test.entity");

        var dup = await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key = NewKey(), name = new Dictionary<string, string> { ["zh-TW"] = "x" }, kind = "extension", hostEntityKey = "test.entity"
        });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var (_, noteKey, _) = await AddField(_admin, key, "text");
        await Activate(_admin, key);
        var schema = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/schema"));
        schema.GetProperty("kind").GetString().Should().Be("extension");
        schema.GetProperty("hostEntityKey").GetString().Should().Be("test.entity");

        (await _admin.GetAsync($"{Base}/modules/{key}/records/by-host/E-0005")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var rec = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, string> { [noteKey] = "x" }, hostKey = "E-0005" });
        rec.StatusCode.Should().Be(HttpStatusCode.Created);
        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/records/by-host/E-0005"));
        got.GetProperty("hostKey").GetString().Should().Be("E-0005");
        got.GetProperty("title").GetString().Should().Be("E-0005 戊號");
    }
}
