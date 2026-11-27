using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Mcp;

/// <summary>mcp2b：高風險工具清單（多選代碼政策）與實際級別（MCP 設計 §6.4）。</summary>
public class McpHighRiskPolicyTests
{
    private static readonly McpToolDescriptor Delete = McpToolCatalog.Find("delete_record")!;

    [Fact]
    public void Factory_DeleteApproveReject_OptionsOnlyWriteTools()
    {
        PolicyCatalog.McpHighRiskTools.Kind.Should().Be(PolicyKind.CodeSet);
        PolicyCatalog.McpHighRiskTools.Factory.Should().Be("delete_record,approve,reject");
        PolicyCatalog.McpHighRiskTools.Options.Should().Equal(McpToolCatalog.HighRiskOptions);
        // 唯讀工具不能選
        McpToolCatalog.HighRiskOptions.Should().NotContain(["whoami", "query_records", "get_record"]);
        McpToolCatalog.HighRiskOptions.Should().Contain(["create_record", "update_record", "delete_record", "approve", "reject"]);
        McpToolCatalog.HighRiskOptions.Should().HaveCount(10);
        PolicyCatalog.McpHighRiskTools.Normalize(PolicyCatalog.McpHighRiskTools.Factory, McpToolCatalog.HighRiskOptions)
            .Should().Be("delete_record,approve,reject");
    }

    [Fact]
    public void Normalize_ReadToolOrUnknown_Invalid()
    {
        var def = PolicyCatalog.McpHighRiskTools;
        def.Normalize("delete_record,query_records", McpToolCatalog.HighRiskOptions).Should().BeNull();
        def.Normalize("drop_database", McpToolCatalog.HighRiskOptions).Should().BeNull();
    }

    [Fact]
    public void Normalize_EmptyAllowed_SortedByOptionOrder()
    {
        var def = PolicyCatalog.McpHighRiskTools;
        // 地板是空的：可以全部移出
        def.Normalize("", McpToolCatalog.HighRiskOptions).Should().Be("");
        def.Normalize("reject, create_record", McpToolCatalog.HighRiskOptions).Should().Be("create_record,reject");
    }

    [Fact]
    public void Normalize_CodeSetWithFloor_FloorCannotBeRemoved()
    {
        // 正式目錄的地板是空的（地板工具隨 mcp3、mcpO2 加），用測試登記驗機制
        var def = new PolicyDefinition("test.codeSet", "test", PolicyKind.CodeSet, PolicyScope.System, "a,b",
            Options: ["a", "b", "c"], Required: ["a"]);
        def.Normalize("b", def.Options).Should().BeNull();
        def.Normalize("", def.Options).Should().BeNull();
        def.Normalize("c,a", def.Options).Should().Be("a,c");
        def.Normalize("a", def.Options).Should().Be("a");
    }

    [Fact]
    public void WithoutElicitation_FactoryCode_TwoOptions()
    {
        var def = PolicyCatalog.McpHighRiskWithoutElicitation;
        (def.Kind, def.Factory, def.Scope).Should().Be((PolicyKind.Choice, "code", PolicyScope.System));
        def.Options.Should().Equal("code", "reject");
        def.Normalize("reject", def.Options).Should().Be("reject");
        def.Normalize("allow", def.Options).Should().BeNull();
    }

    [Fact]
    public void EffectiveRisk_FollowsList()
    {
        McpToolCatalog.EffectiveRisk(Delete, ["delete_record"]).Should().Be(McpRisk.HighRisk);
        // 移出清單：改成一般寫入
        McpToolCatalog.EffectiveRisk(Delete, []).Should().Be(McpRisk.Write);
        // 一般寫入工具加進清單：高風險
        McpToolCatalog.EffectiveRisk(McpToolCatalog.Find("create_record")!, ["create_record"]).Should().Be(McpRisk.HighRisk);
        // 唯讀一律唯讀（就算清單裡有，政策驗證也不會讓它進來）
        McpToolCatalog.EffectiveRisk(McpToolCatalog.Find("query_records")!, ["query_records"]).Should().Be(McpRisk.Read);
    }

    [Fact]
    public void EffectiveRisk_FloorAlwaysHigh_TestToolsKeepDeclared()
    {
        var floor = new McpToolDescriptor("purge_everything", "mcp.purge", McpScopes.BusinessWrite, McpRisk.HighRisk, HighRiskFloor: true);
        McpToolCatalog.EffectiveRisk(floor, []).Should().Be(McpRisk.HighRisk);
        var testTool = new McpToolDescriptor("fake_danger", "mcp.fake", McpScopes.BusinessWrite, McpRisk.HighRisk);
        McpToolCatalog.EffectiveRisk(testTool, []).Should().Be(McpRisk.HighRisk);
    }
}
