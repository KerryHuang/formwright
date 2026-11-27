namespace DynamicModules.Domain.Mcp;

/// <summary>工具級別（設計 §6.1）：唯讀、寫入、高風險（要確認碼）。</summary>
public enum McpRisk { Read, Write, HighRisk }

/// <summary>
/// 一個 MCP 工具的宣告。<paramref name="Name"/> 是 snake_case 的協定名稱；<paramref name="AuditCode"/> 是 camelCase 的稽核動作
/// （<c>mcp.queryRecords</c>）；<paramref name="Scope"/> 是 null 代表不需範圍（<c>whoami</c>）。<paramref name="HighRiskFloor"/> 給 mcp2b 用。
/// <paramref name="LoopTargetArg"/> 是寫入迴圈偵測的目標記錄參數名（mcp2a 判斷 8）；null 代表不偵測。
/// </summary>
public sealed record McpToolDescriptor(string Name, string AuditCode, string? Scope, McpRisk Risk, bool HighRiskFloor = false,
    string? LoopTargetArg = null);

/// <summary>正式的工具清單（設計 §7.1、§7.2）：平台功能清單，寫在程式、不是政策（§5.2、§15）。mcp1a 12 個唯讀、mcp2a 7 個一般寫入、mcp2b 3 個出廠高風險。</summary>
public static class McpToolCatalog
{
    /// <summary>呼叫不存在的工具時的稽核動作。</summary>
    public const string UnknownToolAuditCode = "mcp.unknownTool";

    private static McpToolDescriptor Read(string name, string auditCode, string? scope = McpScopes.BusinessRead) =>
        new(name, auditCode, scope, McpRisk.Read);

    private static McpToolDescriptor Write(string name, string auditCode, string? loopTargetArg = null) =>
        new(name, auditCode, McpScopes.BusinessWrite, McpRisk.Write, LoopTargetArg: loopTargetArg);

    /// <summary>出廠是高風險、管理員可移出清單的寫入工具（mcp2b，設計 §6.4）。</summary>
    private static McpToolDescriptor HighRisk(string name, string auditCode) =>
        new(name, auditCode, McpScopes.BusinessWrite, McpRisk.HighRisk);

    public static IReadOnlyList<McpToolDescriptor> Production { get; } =
    [
        Read("whoami", "mcp.whoami", scope: null),
        Read("list_modules", "mcp.listModules"),
        Read("describe_module", "mcp.describeModule"),
        Read("query_records", "mcp.queryRecords"),
        Read("get_record", "mcp.getRecord"),
        Read("get_record_history", "mcp.getRecordHistory"),
        Read("list_reports", "mcp.listReports"),
        Read("run_report", "mcp.runReport"),
        Read("list_my_tasks", "mcp.listMyTasks"),
        Read("get_task", "mcp.getTask"),
        Read("list_approval_tasks", "mcp.listApprovalTasks"),
        Read("get_record_approval", "mcp.getRecordApproval"),
        Write("create_record", "mcp.createRecord"),
        Write("update_record", "mcp.updateRecord", loopTargetArg: "recordId"),
        Write("create_task", "mcp.createTask"),
        Write("update_task", "mcp.updateTask"),
        Write("add_task_comment", "mcp.addTaskComment"),
        Write("submit_approval", "mcp.submitApproval", loopTargetArg: "recordId"),
        Write("withdraw_approval", "mcp.withdrawApproval"),
        HighRisk("delete_record", "mcp.deleteRecord"),
        HighRisk("approve", "mcp.approve"),
        HighRisk("reject", "mcp.reject"),
    ];

    /// <summary>
    /// 政策 <c>mcp.highRiskTools</c> 能選的工具（設計 §6.4）：正式目錄裡的寫入類工具（一般寫入與高風險），依目錄順序。唯讀工具不能設成高風險。
    /// </summary>
    public static IReadOnlyList<string> HighRiskOptions { get; } =
        Production.Where(t => t.Risk != McpRisk.Read).Select(t => t.Name).ToList();

    /// <summary>地板（依 C7 使用者裁決，寫死）：一律高風險、移不出清單。mcp2b 的正式工具都不在地板；地板工具隨 mcp3、mcpO2 加。</summary>
    public static IReadOnlyList<string> HighRiskFloor { get; } =
        Production.Where(t => t.HighRiskFloor).Select(t => t.Name).ToList();

    /// <summary>政策出廠值：目錄宣告為高風險的工具加上地板，依目錄順序、逗號分隔。</summary>
    public static string HighRiskFactory { get; } =
        string.Join(",", Production.Where(t => t.Risk == McpRisk.HighRisk || t.HighRiskFloor).Select(t => t.Name));

    /// <summary>
    /// 這次呼叫的實際級別（設計 §6.4）：唯讀一律唯讀；地板一律高風險；正式目錄的寫入類工具看政策清單 <paramref name="highRisk"/>
    /// （在清單是高風險、不在是一般寫入）；不在正式目錄的工具（測試工具）照自己的宣告。
    /// </summary>
    public static McpRisk EffectiveRisk(McpToolDescriptor tool, IReadOnlyCollection<string> highRisk)
    {
        if (tool.Risk == McpRisk.Read) return McpRisk.Read;
        if (tool.HighRiskFloor) return McpRisk.HighRisk;
        if (!HighRiskOptions.Contains(tool.Name, StringComparer.Ordinal)) return tool.Risk;
        return highRisk.Contains(tool.Name, StringComparer.Ordinal) ? McpRisk.HighRisk : McpRisk.Write;
    }

    /// <summary>依協定名稱找工具（區分大小寫）；找不到回 null。</summary>
    public static McpToolDescriptor? Find(string name) => Production.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
