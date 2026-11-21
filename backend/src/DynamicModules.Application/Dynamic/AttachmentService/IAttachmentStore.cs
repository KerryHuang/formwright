namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 附件的檔案儲存。介面只做目前需要的：存、讀、刪、列。儲存名一律是系統產生的 32 碼十六進位 id，
/// 實作要自己再驗一次格式，不信任呼叫端。
/// </summary>
public interface IAttachmentStore
{
    /// <summary>寫入；超過 maxBytes 就中止、刪掉寫到一半的檔案並回 null，否則回實際大小。</summary>
    Task<long?> SaveAsync(string id, Stream content, long maxBytes, CancellationToken ct);

    /// <summary>檔案不存在或 id 格式不對回 null。</summary>
    Stream? OpenRead(string id);

    bool Delete(string id);

    IReadOnlyList<StoredAttachment> List();

    /// <summary>
    /// 單一檔案的大小與最後寫入時間（備份複製前後比對用，備份設計 §5.5）；不存在回 null。沒有預設實作（審查 S6）：
    /// 新的附件儲存忘了實作，備份會把每個附件都當成「變動中」略過而照樣成功，是靜默的資料遺漏。
    /// </summary>
    StoredAttachment? Stat(string id);
}

public sealed record StoredAttachment(string Id, DateTime LastWriteUtc, long Size = 0);
