using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// <see cref="Unwritable"/>：沒有標記檔、寫不進去，而且這個資料庫從沒採用過位置。執行器採用時寫不進標記檔，
/// 或只讀（新鮮度、畫面、立即備份）時探測檔寫不進去，都回這個（複核 N1）。
/// </summary>
public enum BackupLocationStatus { NotConfigured, Invalid, Mismatch, Unwritable, Ok }

/// <summary>位置檢查的結果；<see cref="LocationId"/> 是目前採用的位置識別碼（Ok 時才有，還沒採用過可能是 null）。</summary>
public sealed record BackupLocationState(BackupLocationStatus Status, string? LocationId = null)
{
    public bool IsOk => Status == BackupLocationStatus.Ok;

    /// <summary>API 與新鮮度用的代碼。</summary>
    public string Code => Status switch
    {
        BackupLocationStatus.NotConfigured => "notConfigured",
        BackupLocationStatus.Invalid => "locationInvalid",
        BackupLocationStatus.Mismatch => "locationMismatch",
        BackupLocationStatus.Unwritable => "destinationUnwritable",
        _ => "ok",
    };
}

/// <summary>
/// 位置與位置標記檔（備份設計 §7.4，T20）。標記檔 <c>.formwright-backup-location</c> 放位置識別碼，<c>Dynamic_BackupState</c> 記目前採用的：
/// 沒有標記檔就建一個新的並採用（後寫的覆蓋先寫的，記 Warning「備份位置換新」）；相同照常；不同就是 <c>locationMismatch</c>，這台不備份。
/// 只讀（新鮮度）時不建標記檔、不改採用；但沒有標記檔又從沒採用過時，會寫一個探測檔再刪掉，確認位置寫得進去。
/// </summary>
public sealed class BackupLocator(BackupSettings settings, IServiceProvider services, IBackupRepository runs, TimeProvider time, ILogger<BackupLocator> logger)
{
    public const string MarkerPath = ".formwright-backup-location";

    public async Task<BackupLocationState> ResolveAsync(bool adopt, string instanceId, CancellationToken ct)
    {
        if (!settings.Configured || services.GetService<IBackupStore>() is not { } store) return new(BackupLocationStatus.NotConfigured);
        if (settings.Invalid) return new(BackupLocationStatus.Invalid);

        var adopted = (await runs.GetStateAsync(ct))?.LocationId;
        // 第二輪：建標記檔時撞到別台剛寫好的，重讀一次照「有標記檔」比對
        for (var attempt = 0; ; attempt++)
        {
            var marker = await ReadMarkerAsync(store, ct);
            if (marker is not null)
            {
                if (adopted == marker) return new(BackupLocationStatus.Ok, marker);
                if (adopted is not null) return new(BackupLocationStatus.Mismatch);
                // 還沒採用過任何位置（新的資料庫）：採用標記檔上的
                if (adopt) await runs.AdoptLocationAsync(marker, Now, instanceId, ct);
                return new(BackupLocationStatus.Ok, marker);
            }
            if (!adopt)
            {
                // 只讀、沒有標記檔、從沒採用過（新安裝）：探一次寫入權限，寫不進去要讓新鮮度判嚴重、畫面不說「準備中」（複核 N1）
                if (adopted is null && !await CanWriteAsync(store, ct)) return new(BackupLocationStatus.Unwritable);
                return new(BackupLocationStatus.Ok, adopted);
            }

            var fresh = Guid.NewGuid().ToString("N");
            try
            {
                using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fresh));
                await store.PutAsync(MarkerPath, content, ct);
            }
            catch (BackupObjectExistsException) when (attempt == 0)
            {
                continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 寫不進去：只採用真的寫在標記檔裡的識別碼（M1）。採用過就沿用（這一份會以 destinationUnwritable 失敗），沒採用過就不備份
                logger.LogWarning(e, "備份位置 {Root} 寫不進位置標記檔", settings.Root);
                return adopted is not null ? new(BackupLocationStatus.Ok, adopted) : new(BackupLocationStatus.Unwritable);
            }
            await runs.AdoptLocationAsync(fresh, Now, instanceId, ct);
            logger.LogWarning("備份位置換新：{Root} 沒有位置標記檔，建立新的位置 {LocationId}；之後只看這個位置的備份紀錄", settings.Root, fresh);
            return new(BackupLocationStatus.Ok, fresh);
        }
    }

    /// <summary>寫一個探測檔再刪掉；不能刪的儲存不探（之後雲端儲存另訂）。</summary>
    private static async Task<bool> CanWriteAsync(IBackupStore store, CancellationToken ct)
    {
        if (!store.CanDelete) return true;
        var probe = $"{ProbePrefix}{Guid.NewGuid():N}";
        try
        {
            using (var content = new MemoryStream([]))
                await store.PutAsync(probe, content, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            await store.DeleteAsync(probe, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 寫得進、刪不掉：仍算可寫；殘檔由 .partial／探測檔清理收（N4）
        }
        return true;
    }

    /// <summary>寫入權限探測檔的檔名前綴（位置根目錄，不在任何清單裡）。</summary>
    public const string ProbePrefix = ".formwright-write-probe-";

    private DateTime Now => Common.StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);

    private static async Task<string?> ReadMarkerAsync(IBackupStore store, CancellationToken ct)
    {
        try
        {
            await using var stream = await store.OpenReadAsync(MarkerPath, ct);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            var text = (await reader.ReadToEndAsync(ct)).Trim();
            return text.Length is > 0 and <= Domain.Entities.Dynamic.BackupRun.LocationIdMax ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
