using System.Reflection;
using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// apps/app.schema.json 是對外的最上層契約（套件設計 §15）：套件 record 的每個 section 都要列在 schema 裡，
/// formatVersion 的上限跟著 CurrentFormatVersion，防止 schema 再度落後（計畫 J12）。
/// </summary>
public class AppSchemaTests
{
    private static JsonElement Schema()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "apps", "app.schema.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        throw new FileNotFoundException("找不到 apps/app.schema.json");
    }

    /// <summary>會寫進檔案的屬性：可設定的公開屬性（IsMaster 這類計算屬性不序列化）。</summary>
    private static IEnumerable<string> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

    private static IEnumerable<string> Keys(JsonElement properties) => properties.EnumerateObject().Select(p => p.Name);

    [Fact]
    public void AppSchema_TopLevelCoversEveryPackageSection()
    {
        var schema = Schema();
        Keys(schema.GetProperty("properties")).Should().Contain(Properties(typeof(AppPackageData)));
        schema.GetProperty("properties").GetProperty("formatVersion").GetProperty("enum").EnumerateArray().Select(v => v.GetInt32())
            .Should().Equal(Enumerable.Range(AppPackageData.MinFormatVersion, AppPackageData.CurrentFormatVersion - AppPackageData.MinFormatVersion + 1));

        // manifest 與 requires 也是 additionalProperties: false，格式 6 的 kind、requires.masters 要在
        var manifest = schema.GetProperty("$defs").GetProperty("manifest").GetProperty("properties");
        Keys(manifest).Should().Contain(Properties(typeof(AppManifest)));
        Keys(manifest.GetProperty("requires").GetProperty("properties")).Should().Contain(Properties(typeof(AppManifestRequires)));
    }

    [Fact]
    public void AppSchema_ApprovalFlowCoversBothShapes()
    {
        // 格式 9 的流程圖（fg6）：approvalFlows 的項目、節點、連線、通知收件人與範本也是 additionalProperties: false，record 的屬性都要在
        var defs = Schema().GetProperty("$defs");
        Keys(defs.GetProperty("approvalFlow").GetProperty("properties")).Should().Contain(Properties(typeof(AppApprovalFlow)));
        Keys(defs.GetProperty("approvalStage").GetProperty("properties")).Should().Contain(Properties(typeof(AppApprovalStage)));
        Keys(defs.GetProperty("approverSource").GetProperty("properties")).Should().Contain(Properties(typeof(AppApproverSource)));
        Keys(defs.GetProperty("approvalNode").GetProperty("properties")).Should().Contain(Properties(typeof(AppApprovalNode)));
        Keys(defs.GetProperty("approvalEdge").GetProperty("properties")).Should().Contain(Properties(typeof(AppApprovalEdge)));
        Keys(defs.GetProperty("notifyRecipient").GetProperty("properties")).Should().Contain(Properties(typeof(AppNotifyRecipient)));
        Keys(defs.GetProperty("notifyTemplate").GetProperty("properties")).Should().Contain(Properties(typeof(AppNotifyTemplate)));
        defs.GetProperty("notifyRecipient").GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()).Should().Equal("group", "initiator", "initiatorManager");
    }

    [Fact]
    public void AppSchema_FormatVersionBindsFlowShape()
    {
        // 與 AppPackageValidator.V8 一致（審查 🟢1）：格式 9 起 approvalFlows 只收 graph，格式 8 以下只收 sequence；schema 不比 C# 寬
        string? ShapeWhen(string bound, int value) => Schema().GetProperty("allOf").EnumerateArray()
            .Where(x => x.TryGetProperty("if", out var c) && c.GetProperty("properties").GetProperty("formatVersion").TryGetProperty(bound, out var v) && v.GetInt32() == value)
            .Select(x => x.GetProperty("then").GetProperty("properties").GetProperty("approvalFlows").GetProperty("items").GetProperty("properties")
                .GetProperty("shape").GetProperty("const").GetString())
            .SingleOrDefault();
        ShapeWhen("minimum", 9).Should().Be("graph");
        ShapeWhen("maximum", 8).Should().Be("sequence");
    }

    [Fact]
    public void AppSchema_Format10_LocaleKeysAreCodes_ReportNameAndFieldFlag()
    {
        // 名稱多語設計 §8：$defs/i18n 的語系鍵從四語 enum 改成代碼格式的 pattern（同 LocaleCode）；schema 只會比 C# 寬，格式 2–9 的四語限制在伺服器
        var defs = Schema().GetProperty("$defs");
        defs.GetProperty("i18n").GetProperty("propertyNames").GetProperty("$ref").GetString().Should().Be("#/$defs/localeCode");
        var pattern = new System.Text.RegularExpressions.Regex(defs.GetProperty("localeCode").GetProperty("pattern").GetString()!);
        foreach (var ok in new[] { "zh-TW", "ja", "zh-Hant-HK", "es-419" }) pattern.IsMatch(ok).Should().BeTrue(ok);
        foreach (var bad in new[] { "JA", "zh_TW", "x" }) pattern.IsMatch(bad).Should().BeFalse(bad);

        Keys(defs.GetProperty("report").GetProperty("properties")).Should().Contain(Properties(typeof(AppPackageReport)));
        var name = defs.GetProperty("report").GetProperty("properties").GetProperty("name").GetProperty("oneOf").EnumerateArray().ToList();
        name.Select(x => x.GetProperty("type").GetString()).Should().Equal("string", "object");
        name[0].GetProperty("maxLength").GetInt32().Should().Be(DynamicModules.Application.Dynamic.ReportService.Data.ReportRules.MaxNameLength);

        Keys(defs.GetProperty("field").GetProperty("properties")).Should().Contain(Properties(typeof(DynamicModules.Application.Dynamic.TemplateService.Data.TemplateField)));
        defs.GetProperty("field").GetProperty("properties").GetProperty("excludeFromAi").GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void AppSchema_ManifestKind_ListsAppMasterTemplate() =>
        Schema().GetProperty("$defs").GetProperty("manifest").GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).Should().Equal("app", "master", "template");
}
