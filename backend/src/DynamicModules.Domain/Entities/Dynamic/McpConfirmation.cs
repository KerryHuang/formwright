using DynamicModules.Domain.Mcp;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 高風險工具的確認碼一列（MCP 設計 §6.3、§13.3 <c>Dynamic_McpConfirmation</c>）：碼本身只回傳不存，<c>CodeHash</c> 存雜湊。
/// 單次有效，綁權杖、使用者、工具、參數雜湊與目標版本。<c>VoidedUtc</c> 是「同一把權杖超過上限時最舊的作廢」，作廢仍留列，
/// 清理時才寫「逾時未確認」稽核。不設外鍵。
/// </summary>
public class McpConfirmation
{
    public long Id { get; set; }
    public string CodeHash { get; set; } = "";
    public long TokenId { get; set; }
    public string UserId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ArgumentsHash { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string PreviewSummary { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    public DateTime? VoidedUtc { get; set; }
    /// <summary>確認方式（mcp2b，設計 §6.3.1 第 5 點）：確認碼回給 AI，或放在 elicitation 的 requestState 由人在 client 對話框按。</summary>
    public McpConfirmationMethod Method { get; set; } = McpConfirmationMethod.Code;
    /// <summary>使用者在 elicitation 對話框按拒絕或取消的時間；同時設 <see cref="VoidedUtc"/>，清理時不再寫「逾時未確認」。</summary>
    public DateTime? DeclinedUtc { get; set; }

    // 技術限制（計畫判斷 2）；摘要上限的單一來源是 McpLimits
    public const int ToolNameMax = 64, TargetVersionMax = 100, PreviewSummaryMax = McpLimits.PreviewSummaryMax;
}

/// <summary>確認方式：確認碼（<c>confirmationCode</c> 參數送回）或 elicitation（requestState 送回、人在 client 對話框按）。兩條路不能混用。</summary>
public enum McpConfirmationMethod { Code, Elicitation }
