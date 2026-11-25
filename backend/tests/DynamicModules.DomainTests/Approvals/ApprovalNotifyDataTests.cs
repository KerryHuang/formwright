using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>通知節點的 DataJson 契約（流程圖設計 §12.5、fg5 計畫 T1）：序列化形狀、解析白名單、與 shared/approval-notify-cases.json 對照的組字。</summary>
public class ApprovalNotifyDataTests
{
    private static readonly JsonElement Doc = Load();

    private static JsonElement Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "approval-notify-cases.json"))) dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("找不到 shared/approval-notify-cases.json");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "shared", "approval-notify-cases.json"))).RootElement.Clone();
    }

    public static TheoryData<int, string> Cases()
    {
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in Doc.GetProperty("cases").EnumerateArray()) data.Add(i++, c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedCases(int index, string name)
    {
        var c = Doc.GetProperty("cases")[index];
        var t = Doc.GetProperty("texts");
        var texts = new NotifyTexts(t.GetProperty("denied").GetString()!, t.GetProperty("empty").GetString()!, t.GetProperty("noTitle").GetString()!);
        var data = ApprovalNotifyData.Parse(c.GetProperty("data").GetRawText());
        data.Should().NotBeNull(name);
        var fallbacks = c.GetProperty("fallbacks").EnumerateArray().Select(e => e.GetString()!).ToList();
        var (title, body) = data!.Render(c.GetProperty("locale").GetString()!, fallbacks, texts);
        title.Should().Be(c.GetProperty("expected").GetProperty("title").GetString(), name);
        body.Should().Be(c.GetProperty("expected").GetProperty("body").GetString(), name);
    }

    private static I18nText T(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    [Fact]
    public void Serialize_ContractShape()
    {
        var data = new ApprovalNotifyData(1, "tell-hr", true, new NotifyTemplateData(T("{title}"), T("金額 {f:21}")),
            new Dictionary<string, NotifyValue>
            {
                ["title"] = NotifyValue.Text("PO-0001"),
                ["flow"] = NotifyValue.Of(T("採購單")),
                ["f:21"] = NotifyValue.Denied,
                ["f:22"] = NotifyValue.Empty
            });
        var json = JsonDocument.Parse(data.ToJson()).RootElement;
        json.GetProperty("v").GetInt32().Should().Be(1);
        json.GetProperty("node").GetString().Should().Be("tell-hr");
        json.GetProperty("read").GetBoolean().Should().BeTrue();
        json.GetProperty("template").GetProperty("title").GetProperty("zh-TW").GetString().Should().Be("{title}");
        var values = json.GetProperty("values");
        values.GetProperty("title").EnumerateObject().Select(p => p.Name).Should().Equal("t");
        values.GetProperty("flow").GetProperty("i18n").GetProperty("zh-TW").GetString().Should().Be("採購單");
        values.GetProperty("f:21").GetProperty("s").GetString().Should().Be("denied");
        values.GetProperty("f:22").GetProperty("s").GetString().Should().Be("empty");
        values.GetProperty("f:21").EnumerateObject().Should().ContainSingle();   // 不寫 null 屬性
    }

    [Fact]
    public void RoundTrip_Equal()
    {
        var data = new ApprovalNotifyData(1, "n", false, new NotifyTemplateData(T("{initiator}"), null),
            new Dictionary<string, NotifyValue> { ["initiator"] = NotifyValue.Denied, ["node"] = NotifyValue.Of(T("節點")) });
        var back = ApprovalNotifyData.Parse(data.ToJson());
        back!.ToJson().Should().Be(data.ToJson());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"v\":2,\"node\":\"n\",\"read\":true,\"template\":{\"title\":{}},\"values\":{}}")]
    [InlineData("{\"v\":1,\"read\":true,\"template\":{\"title\":{}},\"values\":{}}")]
    [InlineData("{\"v\":1,\"node\":\"n\",\"read\":true,\"values\":{}}")]
    [InlineData("{\"v\":1,\"node\":\"n\",\"read\":true,\"template\":{\"title\":{}},\"values\":{\"x\":{\"t\":1}}}")]
    public void Parse_Malformed_Null(string json)
    {
        ApprovalNotifyData.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_DropsUnknownProperties()
    {
        var back = ApprovalNotifyData.Parse(
            "{\"v\":1,\"node\":\"n\",\"read\":true,\"extra\":\"x\",\"template\":{\"title\":{\"zh-TW\":\"a\"},\"x\":1},\"values\":{\"title\":{\"t\":\"a\",\"evil\":\"<b>\"}}}");
        back.Should().NotBeNull();
        back!.ToJson().Should().NotContain("extra").And.NotContain("evil");
    }
}
