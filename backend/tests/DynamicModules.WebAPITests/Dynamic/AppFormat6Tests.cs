using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>套件格式 6：共用主檔的結構層與引用層（共用主檔設計 §4、§15.2「格式」）。純函式，不需要資料庫。</summary>
public class AppFormat6Tests
{
    private static AppPackageData Parse(string json)
    {
        var parsed = AppJson.Parse(json);
        parsed.Errors.Should().BeEmpty();
        return parsed.V2!;
    }

    private static readonly IReadOnlyDictionary<string, AppPackageData> Masters =
        new Dictionary<string, AppPackageData> { ["partner-lite"] = Parse(AppTestPackagesV6.Master) };

    /// <summary>有主檔定義（目錄層、匯入）時的錯誤。</summary>
    private static IReadOnlyList<string> Errors(string json) => AppPackageValidator.Validate(Parse(json), Masters).Errors;

    /// <summary>沒有主檔定義時（只看套件）的錯誤。</summary>
    private static IReadOnlyList<string> AloneErrors(string json) => AppPackageValidator.Validate(Parse(json)).Errors;

    private static string Crm(string from, string to)
    {
        AppTestPackagesV6.Crm.Should().Contain(from);
        return AppTestPackagesV6.Crm.Replace(from, to);
    }

    private static string Master(string from, string to)
    {
        AppTestPackagesV6.Master.Should().Contain(from);
        return AppTestPackagesV6.Master.Replace(from, to);
    }

    // ---- 合法的套件 ----

    [Fact]
    public void MasterAndUser_PassWithAndWithoutMasterDefinitions()
    {
        AloneErrors(AppTestPackagesV6.Master).Should().BeEmpty();
        Errors(AppTestPackagesV6.Master).Should().BeEmpty();
        AloneErrors(AppTestPackagesV6.Crm).Should().BeEmpty();
        Errors(AppTestPackagesV6.Crm).Should().BeEmpty();
        var p = Parse(AppTestPackagesV6.Crm);
        p.Contributions.Should().ContainSingle().Which.Fields.Select(f => f.Code).Should().Equal("industry", "fromLead", "credit", "nameLength");
        p.Manifest.Requires.Masters.Should().ContainSingle().Which.Id.Should().Be("partner-lite");
        Parse(AppTestPackagesV6.CrmWithSet).Conversions.Single().Header.Set.Should().ContainSingle().Which.Option.Should().Be("customer");
        Parse(AppTestPackagesV6.Master).Manifest.IsMaster.Should().BeTrue();
    }

    [Theory]
    [InlineData("Crm")]
    [InlineData("Quote")]
    [InlineData("Procure")]
    public void Format2PackagesStillPass_WithMasterDefinitionsGiven(string name)
    {
        var json = (string)typeof(AppTestPackages).GetField(name)!.GetValue(null)!;
        AppPackageValidator.Validate(Parse(json), Masters).Errors.Should().BeEmpty();
    }

    // ---- 格式 6 才收 ----

    [Theory]
    [InlineData("\"id\": \"crm6-lite\",", "\"id\": \"crm6-lite\", \"kind\": \"app\",", "manifest.kind")]
    [InlineData("\"masters\": [ { \"id\": \"partner-lite\", \"modules\": [\"partner\"] } ]", "\"masters\": [ { \"id\": \"partner-lite\", \"modules\": [\"partner\"] } ]", "requires.masters")]
    [InlineData("\"active\": false }", "\"active\": false }", "contributions")]
    public void Format5_WithFormat6Features_IsRejectedWhole(string from, string to, string feature)
    {
        var json = Crm(from, to).Replace("\"formatVersion\": 6", "\"formatVersion\": 5");
        AloneErrors(json).Should().Contain(e => e.Contains(feature) && e.Contains("formatVersion 6 才支援"));
    }

    [Fact]
    public void Format5_WithHeaderSet_IsRejectedWhole()
    {
        var json = AppTestPackagesV6.CrmWithSet.Replace("\"formatVersion\": 6", "\"formatVersion\": 5");
        AloneErrors(json).Should().Contain(e => e.Contains("header.set") && e.Contains("formatVersion 6 才支援"));
    }

    [Fact]
    public void Format5_MasterKindExtensibleAndActive_AreRejected()
    {
        var errors = AloneErrors(AppTestPackagesV6.Master.Replace("\"formatVersion\": 6", "\"formatVersion\": 5"));
        errors.Should().Contain("manifest.kind: formatVersion 6 才支援 kind")
            .And.Contain("partner.extensible: formatVersion 6 才支援 extensible")
            .And.Contain("partner.taxId.active: formatVersion 6 才支援 active");
    }

    [Fact]
    public void UnknownKind_IsRejected() =>
        AloneErrors(Master("\"kind\": \"master\"", "\"kind\": \"shared\"")).Should().Contain("manifest.kind: 必須是 app、master 或 template");

    // ---- 範本套件（kind: template，範本套件設計 §5.3 V1–V3） ----

    private const string KindInvalid = "manifest.kind: 必須是 app、master 或 template";

    [Fact]
    public void TemplateKind_Format8_IsValid() => AloneErrors(AppTestPackagesTemplate.TplA).Should().BeEmpty();

    [Fact]
    public void UnknownKind_ListsThreeValues_TP02() =>
        AloneErrors(AppTestPackagesTemplate.Template("tpl-a", kind: "tmpl")).Should().Contain(KindInvalid);

    [Fact]
    public void TemplateKind_BeforeFormat6_IsRejected_TP03() =>
        AloneErrors(AppTestPackagesTemplate.Template("tpl-a", formatVersion: 5)).Should().Contain("manifest.kind: formatVersion 6 才支援 kind");

    [Fact]
    public async Task Template_WithIntegrations_IsRejected_TP04()
    {
        var json = AppTestPackagesTemplate.Template("tpl-a", withApp: "app-b");
        AloneErrors(json).Should().Contain("integrations: 範本套件不收 integrations");

        using var app = new TestApp();
        var res = await app.AsUser().PostAsync($"{Base}/apps/packages/import", new StringContent(json, Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("integrations: 範本套件不收 integrations");
    }

    [Fact]
    public void Template_ExtensibleOrActive_IsRejected_V3()
    {
        var json = AppTestPackagesTemplate.TplA
            .Replace("\"titleField\": \"name\",", "\"titleField\": \"name\", \"extensible\": true,")
            .Replace("\"config\": { \"required\": true } }", "\"config\": { \"required\": true }, \"active\": false }");
        AloneErrors(json).Should().Contain("item.extensible: 只有主檔套件的模組可以設 extensible")
            .And.Contain("item.name.active: 只有主檔套件的模組與 contributions 的欄位可以設 active");
    }

    [Theory]
    [InlineData("Template")]
    [InlineData("TEMPLATE")]
    [InlineData("")]
    [InlineData(" template")]
    public void Kind_NearMisses_AreRejected(string kind) =>
        AloneErrors(AppTestPackagesTemplate.Template("tpl-a", kind: kind)).Should().Contain(KindInvalid);

    /// <summary>V1、V2 經匯入端點依 X-Locale 回應：英文、泰文、越南文都不得是繁中。</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("th")]
    [InlineData("vi")]
    public async Task KindAndIntegrationErrors_AreLocalized(string locale)
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        foreach (var json in new[] { AppTestPackagesTemplate.Template("tpl-a", kind: "tmpl"), AppTestPackagesTemplate.Template("tpl-a", withApp: "app-b") })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/apps/packages/import") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Locale", locale);
            var res = await admin.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var package = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("errors").GetProperty("package")
                .EnumerateArray().Select(e => e.GetString()!).ToList();
            package.Should().NotBeEmpty().And.OnlyContain(m => !Regex.IsMatch(m, "[一-鿿]"));
            if (locale == "en-US")
                package.Should().Contain(m => m == "manifest.kind: must be app, master or template" || m == "integrations: a template package cannot contain integrations");
        }
    }

    // ---- 主檔套件的限制（§4.1） ----

    [Fact]
    public void Master_WithFlowSectionsOrApps_IsRejected()
    {
        var json = Master("\"name\": { \"zh-TW\": \"往來對象（測試）\" } },",
                "\"name\": { \"zh-TW\": \"往來對象（測試）\" }, \"requires\": { \"apps\": [ { \"id\": \"crm-lite\", \"modules\": [\"customer\"] } ] } },")
            .Replace("\"groups\": [", "\"workflows\": [ { \"module\": \"partner\", \"statusField\": \"role\", \"initial\": \"customer\", \"states\": [ { \"option\": \"customer\" } ], \"transitions\": [] } ], \"groups\": [");
        AloneErrors(json).Should().Contain("workflows: 主檔套件不收 workflows")
            .And.Contain("manifest.requires: 主檔套件的 requires 只能有 masters");
    }

    [Fact]
    public void Master_NeedsAnExtensibleModuleAndMasterPrefixedKey()
    {
        var errors = AloneErrors(Master("\"extensible\": true", "\"extensible\": false").Replace("master-partner-lite", "partner-lite-main"));
        errors.Should().Contain("modules: 主檔套件至少要有一個模組標 extensible")
            .And.Contain("partner.key: 主檔模組的 key 必須以 master- 開頭");
    }

    [Fact]
    public void App_CannotMarkExtensibleOrSetActiveOnItsOwnFields()
    {
        var p = Parse(AppTestPackagesV6.Crm);
        var lead = p.Modules[0];
        var block = lead.Blocks[0];
        var bad = p with
        {
            Modules = [lead with { Extensible = true, Blocks = [block with { Fields = [block.Fields[0] with { Active = false }, .. block.Fields.Skip(1)] }] }, .. p.Modules.Skip(1)]
        };
        var errors = AppPackageValidator.Validate(bad).Errors;
        errors.Should().Contain("lead.extensible: 只有主檔套件的模組可以設 extensible")
            .And.Contain("lead.company.active: 只有主檔套件的模組與 contributions 的欄位可以設 active");
    }

    // ---- requires.masters（§4.2） ----

    [Fact]
    public void RequiresMasters_SameIdInAppsAndMasters_IsRejected()
    {
        var json = Crm("\"apps\": [],", "\"apps\": [ { \"id\": \"partner-lite\", \"modules\": [\"partner\"] } ],");
        AloneErrors(json).Should().Contain("manifest.requires.masters[0]: partner-lite 不能同時列在 requires.apps 與 requires.masters");
    }

    [Fact]
    public void RequiresApps_ListingAKnownMaster_IsRejected()
    {
        // 主檔只能列在 requires.masters：requires.apps 列了目錄裡的主檔就整份拒絕（只看套件時不知道它是主檔）
        var json = AppTestPackages.Quote.Replace("\"id\": \"crm-lite\", \"modules\": [\"customer\"]", "\"id\": \"partner-lite\", \"modules\": [\"partner\"]")
            .Replace("\"key\": \"customer\", \"app\": \"crm-lite\"", "\"key\": \"partner\", \"app\": \"partner-lite\"");
        AppPackageValidator.Validate(Parse(json), Masters).Errors
            .Should().Contain("manifest.requires.apps[0]: partner-lite 是共用主檔，要列在 requires.masters");
    }

    [Fact]
    public void RequiresMasters_DuplicateAndTooMany_AreRejected()
    {
        var six = string.Join(", ", Enumerable.Range(1, 6).Select(i => $"{{ \"id\": \"m{i}-x\", \"modules\": [\"a\"] }}"));
        AloneErrors(Crm("\"masters\": [ { \"id\": \"partner-lite\", \"modules\": [\"partner\"] } ]", $"\"masters\": [ {six} ]"))
            .Should().Contain("manifest.requires.masters: 最多 5 個");
        AloneErrors(Crm("\"masters\": [ { \"id\": \"partner-lite\", \"modules\": [\"partner\"] } ]",
                "\"masters\": [ { \"id\": \"partner-lite\", \"modules\": [\"partner\"] }, { \"id\": \"partner-lite\", \"modules\": [\"partner\"] } ]"))
            .Should().Contain("manifest.requires.masters[1]: 主檔 partner-lite 重複");
    }

    // ---- 主檔符號（§4.4） ----

    [Fact]
    public void MasterSymbol_FirstSegmentMustBeInRequiresMasters()
    {
        var json = CrmSet("\"from\": \"partner-lite/partner.phone\"", "\"from\": \"other-master/partner.phone\"")
            .Replace("\"set\": [ { \"to\": \"partner-lite/partner.role\"", "\"set\": [ { \"to\": \"other-master/partner.role\"");
        var errors = AloneErrors(json);
        errors.Should().Contain(e => e.StartsWith("order.customer.fill.map") && e.Contains("other-master/partner.phone"));
        errors.Should().Contain(e => e.StartsWith("conversions[0].header.set") && e.Contains("other-master/partner.role"));
    }

    [Fact]
    public void MasterSymbol_ModuleMustBeListed()
    {
        var json = Crm("\"refTarget\": { \"kind\": \"module\", \"key\": \"partner\", \"app\": \"partner-lite\" }",
            "\"refTarget\": { \"kind\": \"module\", \"key\": \"contact\", \"app\": \"partner-lite\" }");
        AloneErrors(json).Should().Contain("order.customer: 主檔模組 partner-lite/contact 沒有列在 requires.masters 的 modules");
    }

    [Fact]
    public void MasterSymbol_UnknownBaseField_IsRejectedOnlyWhenTheMasterIsKnown()
    {
        var json = Crm("\"from\": \"partner-lite/partner.phone\"", "\"from\": \"partner-lite/partner.fax\"");
        AloneErrors(json).Should().BeEmpty("主檔定義未知時只檢查前綴，欄位層級留給開啟時解析");
        Errors(json).Should().Contain(e => e.Contains("partner-lite/partner.fax 不是主檔 partner-lite 的基本欄位，也不是本套件貢獻的欄位"));
    }

    [Fact]
    public void MasterSymbol_AnotherAppsContribution_CannotBeReferenced()
    {
        // 別的應用程式的貢獻不在本套件的符號空間：groupBy 一個不是基本也不是自己貢獻的欄位 → 擋下
        var json = Crm("\"field\": \"partner-lite/partner.industry\"", "\"field\": \"partner-lite/partner.stockClass\"");
        Errors(json).Should().Contain(e => e.StartsWith("reports[0].definition") && e.Contains("partner-lite/partner.stockClass"));
    }

    [Fact]
    public void RefGuard_OnMasterModule_ChecksTheOptionsWhenKnown()
    {
        var json = Crm("\"in\": [\"customer\", \"both\"]", "\"in\": [\"customer\", \"vendor\"]");
        AloneErrors(json).Should().BeEmpty();
        Errors(json).Should().Contain(e => e.StartsWith("order.customer.refGuard.in") && e.Contains("vendor"));
    }

    private static string CrmSet(string from, string to)
    {
        AppTestPackagesV6.CrmWithSet.Should().Contain(from);
        return AppTestPackagesV6.CrmWithSet.Replace(from, to);
    }

    [Fact]
    public void HeaderSet_OnlyEnumFieldsAndTheirOptions()
    {
        Errors(CrmSet("\"option\": \"customer\"", "\"option\": \"vendor\""))
            .Should().Contain("conversions[0].header.set: vendor 不是 partner-lite/partner.role 的選項代號");
        Errors(CrmSet("\"set\": [ { \"to\": \"partner-lite/partner.role\"", "\"set\": [ { \"to\": \"partner-lite/partner.phone\""))
            .Should().Contain("conversions[0].header.set: 固定值只能寫入下拉欄位，partner-lite/partner.phone 不是");
    }

    /// <summary>開啟流程會建立固定值（ConversionFixedValueTests），目標是一般模組或主檔模組都接受。</summary>
    [Fact]
    public void HeaderSet_IsAccepted_ForPlainAndMasterTargets()
    {
        AloneErrors(AppTestPackagesV6.LocalSet).Should().BeEmpty();
        Errors(AppTestPackagesV6.CrmWithSet).Should().BeEmpty();
        AloneErrors(AppTestPackagesV6.LocalSet.Replace("\"set\": [ { \"to\": \"customer.role\", \"option\": \"customer\" } ]", "\"set\": []"))
            .Should().BeEmpty();
    }

    [Fact]
    public void HeaderSet_AFieldOnlyOnce_NotTheTraceFieldOrACopyTarget()
    {
        const string one = "\"set\": [ { \"to\": \"customer.role\", \"option\": \"customer\" } ]";
        AppTestPackagesV6.LocalSet.Should().Contain(one);
        AloneErrors(AppTestPackagesV6.LocalSet.Replace(one,
                "\"set\": [ { \"to\": \"customer.role\", \"option\": \"customer\" }, { \"to\": \"customer.role\", \"option\": \"supplier\" } ]"))
            .Should().Contain(e => e.StartsWith("conversions[0].header.set: 欄位 customer.role 重複"));
    }

    // ---- contributions（§4.3） ----

    [Fact]
    public void Contribution_MasterMustBeInRequiresMasters_AndModuleExtensible()
    {
        AloneErrors(Crm("\"master\": \"partner-lite\", \"module\": \"partner\"", "\"master\": \"item-lite\", \"module\": \"partner\""))
            .Should().Contain("contributions[0]: 主檔 item-lite 沒有列在 requires.masters");
        var notExtensible = new Dictionary<string, AppPackageData>
        {
            ["partner-lite"] = Parse(AppTestPackagesV6.Master.Replace("\"extensible\": true", "\"extensible\": false"))
        };
        AppPackageValidator.Validate(Parse(AppTestPackagesV6.Crm), notExtensible).Errors
            .Should().Contain("contributions[0]: 主檔模組 partner-lite/partner 不接受貢獻（沒有標 extensible）");
    }

    /// <summary>審查反例：貢獻欄位的選項寫成 null，結構層要給可定位的錯誤，不能走到引用層的 NullReferenceException。</summary>
    [Fact]
    public void Contribution_NullOption_IsAStructureError()
    {
        var json = Crm("\"options\": [ { \"code\": \"manufacturing\", \"label\": { \"zh-TW\": \"製造\" } } ]", "\"options\": [null]");
        AloneErrors(json).Should().Equal("contributions[0].fields[industry].options: 不能有 null");
        Errors(json).Should().Equal("contributions[0].fields[industry].options: 不能有 null");
    }

    [Fact]
    public void Contribution_FieldsCannotBeRequired()
    {
        AloneErrors(Crm("\"config\": { \"isFilterable\": true },", "\"config\": { \"isFilterable\": true, \"required\": true },"))
            .Should().Contain("contributions[0].fields[industry]: 貢獻欄位不得必填（別的積木包建立主檔記錄時不該被擋住）");
    }

    [Theory]
    [InlineData("autonumber")]
    [InlineData("multienum")]
    [InlineData("attachment")]
    [InlineData("rollup")]
    public void Contribution_DisallowedTypes_AreRejected(string type)
    {
        var json = Crm("{ \"code\": \"credit\", \"type\": \"currency\"", $"{{ \"code\": \"credit\", \"type\": \"{type}\"");
        AloneErrors(json).Should().Contain(e => e.StartsWith("contributions[0].fields[credit].type: 貢獻欄位不能是"));
    }

    [Fact]
    public void Contribution_ReferenceCascade_IsRejected_AndOwnModuleNeedsSetNull()
    {
        AloneErrors(Crm("\"onDelete\": \"setNull\"", "\"onDelete\": \"cascade\""))
            .Should().Contain(e => e.StartsWith("contributions[0].fields[fromLead].onDelete: 貢獻的參照欄位不能是 cascade"));
        AloneErrors(Crm("\"onDelete\": \"setNull\"", "\"onDelete\": \"restrict\""))
            .Should().Contain("contributions[0].fields[fromLead].onDelete: 指向本積木包模組的貢獻參照欄位必須是 setNull");
    }

    [Fact]
    public void Contribution_ReferenceToAnotherApp_IsRejected()
    {
        var json = Crm("\"refTarget\": { \"kind\": \"module\", \"key\": \"lead\" }", "\"refTarget\": { \"kind\": \"module\", \"key\": \"customer\", \"app\": \"crm-lite\" }");
        AloneErrors(json).Should().Contain(e => e.StartsWith("contributions[0].fields[fromLead]: 貢獻的參照欄位只能指向本積木包的模組或 requires.masters 的主檔模組"));
    }

    [Fact]
    public void Contribution_FormulaCanOnlyUseBaseAndOwnFields()
    {
        Errors(Crm("\"formula\": \"{credit} * 2\"", "\"formula\": \"{credit} + {phone}\"")).Should().BeEmpty("phone 是主檔的基本欄位");
        Errors(Crm("\"formula\": \"{credit} * 2\"", "\"formula\": \"{stockValue} * 2\""))
            .Should().Contain("contributions[0].fields[nameLength]: 公式引用了 stockValue，貢獻的公式只能引用主檔的基本欄位與本貢獻的欄位");
    }

    /// <summary>#93：公式欄位的結果型別要在設計器的白名單內，貢獻欄位在套件驗證擋下（模組欄位見 SharedMasterImportTests）。</summary>
    [Theory]
    [InlineData("decimal")]
    [InlineData("text")]
    public void Formula_WithUnsupportedResultType_IsRejected(string resultType)
    {
        Errors(Crm("\"formula\": \"{credit} * 2\", \"resultType\": \"number\"", $"\"formula\": \"{{credit}} * 2\", \"resultType\": \"{resultType}\""))
            .Should().Contain("contributions[0].fields[nameLength].config.resultType: 結果型別必須是 number、currency 或 date");
    }

    /// <summary>#99：公式算出的型別和 resultType 不符（日期公式標 number、數值公式標 date），套件驗證比照設計器擋下。</summary>
    [Fact]
    public void Formula_ResultTypeNotMatchingComputedType_IsRejected()
    {
        Errors(Crm("\"formula\": \"{credit} * 2\", \"resultType\": \"number\"", "\"formula\": \"{credit} * 2\", \"resultType\": \"date\""))
            .Should().Contain("contributions[0].fields[nameLength].config.resultType: 公式不正確：結果型別與設定不符");
        const string phone = "{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": {} },";
        var json = Master(phone, phone
            + " { \"code\": \"since\", \"type\": \"date\", \"label\": { \"zh-TW\": \"起始日\" }, \"config\": {} },"
            + " { \"code\": \"due\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"到期\" }, \"config\": { \"formula\": \"DATEADD({since}, 30)\", \"resultType\": \"number\" } },");
        AloneErrors(json).Should().Contain(e => e.Contains("partner.due") && e.Contains("結果型別與設定不符"));
        AloneErrors(json.Replace("\"resultType\": \"number\"", "\"resultType\": \"date\"")).Should().BeEmpty();
    }

    private const string PhoneField = "{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": {} },";
    private const string SinceField = " { \"code\": \"since\", \"type\": \"date\", \"label\": { \"zh-TW\": \"起始日\" }, \"config\": {} },";

    /// <summary>#99 審查：彙總欄位的結果型別開啟時由彙總對象重推導，套件寫的不準，引用它的公式不比對（不擋原本開得起來的套件）。</summary>
    [Fact]
    public void Formula_ReferencingARollup_IsNotCheckedAgainstTheWrittenResultType()
    {
        var json = Master(PhoneField, PhoneField
            + " { \"code\": \"cnt\", \"type\": \"rollup\", \"label\": { \"zh-TW\": \"筆數\" }, \"config\": { \"resultType\": \"date\", \"rollup\": { \"sourceField\": \"partner.no\", \"function\": \"count\" } } },"
            + " { \"code\": \"dbl\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"兩倍\" }, \"config\": { \"formula\": \"DATEADD({cnt}, 1)\", \"resultType\": \"number\" } },");
        AloneErrors(json).Should().NotContain(e => e.Contains("結果型別與設定不符"));
    }

    /// <summary>#99 審查：公式引用停用欄位時只報停用那一句，不再多報型別不符。</summary>
    [Fact]
    public void Formula_ReferencingAnInactiveField_DoesNotAlsoReportResultTypeMismatch()
    {
        var json = Master(PhoneField, PhoneField
            + SinceField.Replace("\"config\": {} }", "\"config\": {}, \"active\": false }")
            + " { \"code\": \"due\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"到期\" }, \"config\": { \"formula\": \"DATEADD({since}, 30)\", \"resultType\": \"number\" } },");
        var errors = AloneErrors(json);
        errors.Should().Contain(e => e.Contains("被公式或規則引用，不能停用"));
        errors.Should().NotContain(e => e.Contains("結果型別與設定不符"));
    }

    /// <summary>#99 審查：貢獻公式引用主檔基本欄位時，型別不符要報；主檔定義未知時只看本貢獻、不報。</summary>
    [Fact]
    public void ContributionFormula_ReferencingABaseField_IsCheckedOnlyWhenTheMasterIsKnown()
    {
        var master = Parse(Master(PhoneField, PhoneField + SinceField));
        var masters = new Dictionary<string, AppPackageData> { ["partner-lite"] = master };
        var json = Crm("\"formula\": \"{credit} * 2\", \"resultType\": \"number\"", "\"formula\": \"DATEADD({since}, 30)\", \"resultType\": \"number\"");
        AppPackageValidator.Validate(Parse(json), masters).Errors
            .Should().Contain("contributions[0].fields[nameLength].config.resultType: 公式不正確：結果型別與設定不符");
        AloneErrors(json).Should().NotContain(e => e.Contains("結果型別與設定不符"));
        AppPackageValidator.Validate(Parse(json.Replace("\"resultType\": \"number\" }, \"active\": false", "\"resultType\": \"date\" }, \"active\": false")), masters).Errors
            .Should().NotContain(e => e.Contains("結果型別與設定不符"));
    }

    /// <summary>#99：欄位 config 是字串或陣列時，格式層直接報錯，不是跳過所有 config 檢查。</summary>
    [Theory]
    [InlineData("\"oops\"")]
    [InlineData("[]")]
    public void FieldConfig_NotAnObject_IsRejected(string config)
    {
        var errors = AloneErrors(Crm("{ \"code\": \"credit\", \"type\": \"currency\", \"label\": { \"zh-TW\": \"信用額度\" }, \"config\": {} }",
            "{ \"code\": \"credit\", \"type\": \"currency\", \"label\": { \"zh-TW\": \"信用額度\" }, \"config\": " + config + " }"));
        errors.Should().Contain(e => e.Contains("credit") && e.Contains("config 必須是物件"));
        var master = AloneErrors(Master("\"config\": { \"format\": \"BP-{0000}\", \"showInList\": true }", "\"config\": " + config));
        master.Should().Contain(e => e.Contains("partner.no") && e.Contains("config 必須是物件"));
    }

    /// <summary>停用的貢獻欄位被啟用中的公式引用：開啟時停用會被設計器的規則擋下，所以匯入時就擋，訊息照設計器。</summary>
    [Fact]
    public void Contribution_AnInactiveFieldUsedByAnActiveFormula_IsRejected()
    {
        const string credit = "{ \"code\": \"credit\", \"type\": \"currency\", \"label\": { \"zh-TW\": \"信用額度\" }, \"config\": {} }";
        var json = Crm(credit, credit.Replace("\"config\": {} }", "\"config\": {}, \"active\": false }"))
            .Replace("\"resultType\": \"number\" }, \"active\": false }", "\"resultType\": \"number\" } }");
        AloneErrors(json).Should().Contain("contributions[0].fields[credit].active: 此欄位被公式或規則引用，不能停用（nameLength）");
        AloneErrors(AppTestPackagesV6.Crm).Should().BeEmpty("公式本身也停用時可以");
    }

    private static string CrmWithCopyEvent(string field, string allow) => AppTestPackagesV6.Crm.Replace("  \"contributions\": [",
        "  \"events\": [ { \"code\": \"crm6-copy\", \"name\": { \"zh-TW\": \"抄\" }, \"active\": true, \"source\": \"order\", \"trigger\": { \"on\": \"create\" }, \"when\": null,\n"
        + "      \"effects\": [ { \"code\": \"mk\", \"kind\": \"create\", \"target\": \"lead\", \"set\": [ { \"to\": \"lead.company\", \"from\": { \"side\": \"header\", \"field\": \"" + field + "\" }" + allow + " } ] } ] } ],\n"
        + "  \"contributions\": [");

    /// <summary>審查待決 7：限定可見的貢獻欄位（和引用它們的貢獻公式）和隱藏欄位一樣，事件複製要明確 allowHiddenSource，與執行期一致。</summary>
    [Theory]
    [InlineData("partner-lite/partner.credit")]
    [InlineData("partner-lite/partner.nameLength")]
    public void Event_CopyingARestrictedContributionField_NeedsAllowHiddenSource(string field)
    {
        Parse(CrmWithCopyEvent(field, "")).Events.Should().ContainSingle("事件確實被加進套件");
        AloneErrors(CrmWithCopyEvent(field, "")).Should().Contain(e => e.Contains("allowHiddenSource") && e.Contains(field));
        AloneErrors(CrmWithCopyEvent(field, ", \"allowHiddenSource\": true")).Should().NotContain(e => e.Contains("allowHiddenSource"));
        AloneErrors(CrmWithCopyEvent("lead.company", "")).Should().NotContain(e => e.Contains("allowHiddenSource"));
    }

    /// <summary>第三輪 5：貢獻內 fieldAccess 標成 hidden 的欄位，事件複製同樣要 allowHiddenSource（貢獻欄位本來就都限定可見，這裡釘住兩條規則一致）。</summary>
    [Fact]
    public void Event_CopyingAContributionFieldMarkedHidden_NeedsAllowHiddenSource()
    {
        const string access = "\"fieldAccess\": [ { \"field\": \"partner-lite/partner.credit\", \"group\": \"sales\", \"access\": \"readOnly\" } ]";
        var json = CrmWithCopyEvent("partner-lite/partner.credit", "").Replace(access, access.Replace("\"readOnly\"", "\"hidden\""));
        json.Should().Contain("\"access\": \"hidden\"");
        AloneErrors(json).Should().Contain(e => e.Contains("allowHiddenSource") && e.Contains("partner-lite/partner.credit"));
        AloneErrors(json.Replace("\"from\": { \"side\": \"header\"", "\"allowHiddenSource\": true, \"from\": { \"side\": \"header\""))
            .Should().NotContain(e => e.Contains("allowHiddenSource"));
    }

    [Fact]
    public void Contribution_CodeAndOptionCodeCannotEqualBaseCodes()
    {
        Errors(Crm("{ \"code\": \"credit\", \"type\": \"currency\"", "{ \"code\": \"phone\", \"type\": \"currency\"")
                .Replace("{credit}", "{phone}").Replace("partner-lite/partner.credit", "partner-lite/partner.phone"))
            .Should().Contain("contributions[0].fields[phone].code: 與主檔 partner-lite 的基本欄位代號相同");
        Errors(Crm("{ \"code\": \"manufacturing\"", "{ \"code\": \"customer\""))
            .Should().Contain("contributions[0].fields[industry]: 選項代號 customer 與主檔 partner-lite 的基本選項代號相同");
    }

    [Fact]
    public void Contribution_PermissionGroupMustBeDeclared()
    {
        var p = Parse(AppTestPackagesV6.Crm);
        var c = p.Contributions[0];
        var bad = p with { Contributions = [c with { Permissions = [c.Permissions[0] with { Group = "buyer" }] }] };
        AppPackageValidator.Validate(bad).Errors.Should().Contain("contributions[0].permissions: 群組 buyer 沒有在 groups 宣告");
    }

    [Fact]
    public void Contribution_FieldAccess_OnlyOwnContributionFields()
    {
        AloneErrors(Crm("\"field\": \"partner-lite/partner.credit\"", "\"field\": \"partner-lite/partner.phone\""))
            .Should().Contain(e => e.StartsWith("contributions[0].fieldAccess[0].field: 只能限制本貢獻自己的欄位"));
        AloneErrors(Crm("\"field\": \"partner-lite/partner.credit\", \"group\": \"sales\"", "\"field\": \"partner-lite/partner.credit\", \"group\": \"buyer\""))
            .Should().Contain("contributions[0].fieldAccess[0].group: 群組 buyer 沒有在 groups 宣告");
    }

    [Fact]
    public void Contribution_DuplicateTarget_IsRejected()
    {
        var p = Parse(AppTestPackagesV6.Crm);
        var twice = p with { Contributions = [p.Contributions[0], p.Contributions[0] with { Block = new AppContributionBlock("crm2", p.Contributions[0].Block.Name) }] };
        AppPackageValidator.Validate(twice).Errors.Should().Contain("contributions[1]: 對 partner-lite/partner 的貢獻重複（同一個主檔模組至多一筆）");
    }

    // ---- 標題樣板（格式 6 一起帶進來的核心能力） ----

    private const string LeadTitle = "\"titleField\": \"company\",";

    [Fact]
    public void TitleTemplate_OfOwnTextFields_Passes()
    {
        var json = Crm(LeadTitle, LeadTitle + " \"titleTemplate\": \"{company}（{phone}）\",");
        Errors(json).Should().BeEmpty();
        Parse(json).Modules.Single(m => m.Code == "lead").TitleTemplate.Should().Be("{company}（{phone}）");
    }

    [Theory]
    [InlineData("{company} {nope}", "lead: 標題樣板引用了不存在的欄位 nope")]
    [InlineData("{company", "lead: 標題樣板的大括號沒有成對")]
    [InlineData("潛在客戶", "lead: 標題樣板至少要引用一個欄位")]
    public void TitleTemplate_BadTemplates_AreRejected(string template, string error)
    {
        AloneErrors(Crm(LeadTitle, LeadTitle + $" \"titleTemplate\": \"{template}\",")).Should().Contain(error);
    }

    [Fact]
    public void TitleTemplate_ReferenceField_IsRejected()
    {
        var json = Crm("\"titleField\": \"no\",", "\"titleField\": \"no\", \"titleTemplate\": \"{no} {customer}\",");
        AloneErrors(json).Should().Contain("order: 標題樣板不能引用 customer（只能用文字、電子郵件、電話、網址、自動編號、日期或單選下拉欄位）");
    }

    [Fact]
    public void TitleTemplate_FieldHiddenByFieldAccess_IsRejected()
    {
        var json = Crm(LeadTitle, LeadTitle + " \"titleTemplate\": \"{company}（{phone}）\",")
            .Replace("\"contributions\": [", "\"fieldAccess\": [ { \"field\": \"lead.phone\", \"group\": \"sales\", \"access\": \"hidden\" } ],\n  \"contributions\": [");
        AloneErrors(json).Should().Contain("fieldAccess[0]: 標題樣板用到的欄位不能是隱藏");
    }

    [Fact]
    public void TitleTemplate_InFormat5_IsRejected()
    {
        var json = AppTestPackages.Crm.Replace("\"formatVersion\": 2", "\"formatVersion\": 5")
            .Replace("\"titleField\": \"name\",", "\"titleField\": \"name\", \"titleTemplate\": \"{name} {phone}\",");
        AloneErrors(json).Should().Contain("customer.titleTemplate: formatVersion 6 才支援標題樣板");
    }
}
