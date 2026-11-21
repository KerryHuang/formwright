using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// 一份備份的 <c>manifest.json</c>（備份設計 §5.1，格式版本 1），最後寫：有它才算完整的 set。
/// 保留清理只看這份檔（<see cref="Trigger"/>、<see cref="SnapshotUtc"/>、<see cref="LocationId"/>），不看歷程表（§8）。
/// </summary>
public sealed record BackupManifest(
    int FormatVersion, string RunKey, string Trigger, string LocationId, DateTime SnapshotUtc, string AppVersion, string Commit, string Edition,
    string Provider, string MigrationId, BackupManifest.DatabasePart Database, BackupManifest.AttachmentsPart Attachments, bool IncludesKeys)
{
    public const int CurrentFormat = 1;
    public const string FileName = "manifest.json", AttachmentListName = "attachments.txt";

    public sealed record DatabasePart(string File, long Bytes, string Sha256);

    public sealed record AttachmentsPart(long Count, long Bytes, string List, string ListSha256);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>讀不懂（壞掉或不是這個格式）回 null。</summary>
    public static BackupManifest? Parse(Stream stream)
    {
        try
        {
            var m = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
            return m is { RunKey.Length: > 0, Trigger.Length: > 0 } ? m with { SnapshotUtc = DateTime.SpecifyKind(m.SnapshotUtc, DateTimeKind.Utc) } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>檔案的 SHA-256（小寫十六進位）。</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }
}
