using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>簽核的站內通知（設計 §12）：同一個交易內寫入，只帶標題快照與連結；通知節點例外，另帶 DataJson（流程圖設計 §12.5）。</summary>
public sealed class ApprovalNotifier(INotificationRepository notifications, IBusinessTime time, IModuleRepository modules)
{
    public const string SourceType = "approval";

    private readonly Dictionary<long, string?> _moduleKeys = [];   // 同一個請求內同一模組只查一次

    /// <summary>去重；exceptUserId（這次動作的人）不通知。title 省略時用實例的標題快照；withLink 為 false 時不帶連結（收件人已無讀取權）。</summary>
    public async Task NotifyAsync(string kind, ApprovalInstance inst, IEnumerable<string> userIds, string? exceptUserId, CancellationToken ct,
        string? title = null, bool withLink = true)
    {
        var recipients = userIds.Where(u => u != exceptUserId).Distinct(StringComparer.Ordinal).ToList();
        if (recipients.Count == 0) return;
        if (!_moduleKeys.TryGetValue(inst.ModuleId, out var moduleKey))
            _moduleKeys[inst.ModuleId] = moduleKey = (await modules.GetByIdAsync(inst.ModuleId, includeChildren: false, ct))?.Key;
        foreach (var userId in recipients)
            notifications.Add(new Notification
            {
                UserId = userId, Kind = kind, TitleSnapshot = title ?? inst.TitleSnapshot, Link = withLink ? $"/dynamic/{moduleKey}/{inst.RecordId}" : "",
                SourceType = SourceType, SourceId = inst.Id, CreatedTime = time.Now
            });
    }

    /// <summary>
    /// 通知節點：每人一列 approvalNotify，標題快照、連結、DataJson 都已由 <see cref="ApprovalNotifyComposer"/> 依收件人遮蔽。
    /// 不經 <see cref="NotifyAsync"/> 的排除邏輯：觸發這次推進的人在收件人裡照收（U10）。
    /// </summary>
    public void NotifyPrepared(ApprovalInstance inst, IEnumerable<NotifyMessage> messages)
    {
        foreach (var m in messages)
            notifications.Add(new Notification
            {
                UserId = m.UserId, Kind = NotificationKinds.ApprovalNotify, TitleSnapshot = m.TitleSnapshot, Link = m.Link, DataJson = m.DataJson,
                SourceType = SourceType, SourceId = inst.Id, CreatedTime = time.Now
            });
    }
}
