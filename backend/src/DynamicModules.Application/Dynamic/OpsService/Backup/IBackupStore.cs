namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>儲存位置裡的一個檔案；<see cref="Path"/> 以 <c>/</c> 分隔、相對於位置根目錄。</summary>
public sealed record BackupObject(string Path, long Size, DateTime LastWriteUtc);

/// <summary>寫入的目標已經存在（儲存只寫一次、不覆寫，備份設計 §7.1）。</summary>
public sealed class BackupObjectExistsException(string path) : IOException($"備份位置已經有 {path}，不覆寫");

/// <summary>
/// 備份的儲存介面（備份設計 §7.1、T5）。bk1 只有本機目錄實作；之後的雲端隔離儲存用自己的 <see cref="Kind"/>，
/// <see cref="Isolated"/> 為真、<see cref="CanDelete"/> 為假（§7.2）。
/// 資料庫快照先寫在本機暫存目錄（<see cref="PrepareStaging"/>），完成後 <see cref="PublishAsync"/> 成一份 set。
/// </summary>
public interface IBackupStore
{
    /// <summary>儲存種類代碼（本機目錄是 <c>local</c>）。</summary>
    string Kind { get; }
    /// <summary>給畫面看的位置（雲端實作不含憑證）。</summary>
    string DisplayLocation { get; }
    bool Isolated { get; }
    bool CanDelete { get; }
    /// <summary>量剩餘空間用的本機目錄；量不到（雲端）時 null。</summary>
    string? LocalRoot { get; }

    Task<bool> ExistsAsync(string path, CancellationToken ct);
    /// <summary>只寫一次：目標已存在丟 <see cref="BackupObjectExistsException"/>。先寫 <c>.partial</c> 再改名，不留半個檔。</summary>
    Task PutAsync(string path, Stream content, CancellationToken ct);
    /// <summary>不存在回 null。</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct);
    /// <summary>前綴底下的所有檔案（遞迴），不含暫存目錄。</summary>
    Task<IReadOnlyList<BackupObject>> ListAsync(string prefix, CancellationToken ct);
    /// <summary>刪檔或整個目錄；<see cref="CanDelete"/> 為假時一律丟例外。</summary>
    Task DeleteAsync(string path, CancellationToken ct);

    /// <summary>建立這次執行的本機暫存目錄並回傳路徑。</summary>
    string PrepareStaging(string runKey);
    /// <summary>暫存目錄整個搬成 <c>sets/&lt;setName&gt;/</c>（本機是同一檔案系統內的改名，<c>sets/</c> 下不會有半份）。</summary>
    Task PublishAsync(string runKey, string setName, CancellationToken ct);
    /// <summary>清掉暫存：<paramref name="runKey"/> 給了只清那一份，否則清掉除了 <paramref name="keepRunKey"/> 以外的全部。</summary>
    void ClearStaging(string? runKey = null, string? keepRunKey = null);
}
