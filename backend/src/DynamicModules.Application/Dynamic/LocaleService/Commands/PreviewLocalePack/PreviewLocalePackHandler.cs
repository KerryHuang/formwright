using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using System.Text.Json;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.PreviewLocalePack;

/// <summary>
/// 交易內：A38 大小 → 解析 → <see cref="LocaleWorldReader"/>（不讀快照；世界與目前版原始列同一次讀取，i4d2 判斷 6）→ 該語系的內容指紋與目前版 Id
/// → 分析（帶平台版本與目前版的覆寫層檢視，判斷 2、7）→ <c>designData</c> 段（i4e：讀可翻譯項目、記下各目標的內容指紋，§23.2）
/// → 會寫新版本時補「這次會清掉的已失效覆寫」（判斷 8）；匯入開放且有計畫時
/// 清掉過期與超量的暫存、新增一筆並 <see cref="IUnitOfWork.CommitAsync"/>（<c>TransactionBehavior</c> 只提交交易、不存檔）。
/// </summary>
public sealed class PreviewLocalePackHandler(
    LocaleWorldReader worlds, ILocaleRepository locales, ILocalePackRepository packs, IPolicyReader policies, IUnitOfWork uow,
    TimeProvider time, LocalePackSettings settings, ICurrentUser current, IOpsEnvironment ops, DesignTextCatalog catalog, ISystemSettingRepository systemSettings)
    : IRequestHandler<PreviewLocalePackRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewLocalePackRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var maxBytes = await policies.IntAsync(PolicyCatalog.LocalePackMaxBytes, null, ct);
        if (request.Content.Length > maxBytes) return Invalid(LocalePackMessages.TooLarge(maxBytes));
        var (pack, parseError) = LocalePackParser.Parse(request.Content.AsMemory());
        if (pack is null) return Invalid(parseError!);

        var maxInstalled = await policies.IntAsync(PolicyCatalog.LocaleMaxInstalled, null, ct);
        var (world, currents) = await worlds.ReadWithCurrentAsync(ct);
        var row = await locales.FindAsync(pack.Code, ct);
        var currentPack = currents.GetValueOrDefault(pack.Code);
        var review = LocaleOverlayReview.Of(currentPack);
        var analysis = LocalePackAnalyzer.Analyze(pack, world, maxInstalled, ops.Version, review);
        if (analysis.Error is not null) return Invalid(analysis.Error);

        // designData（i4e，§23.2）：這次才新安裝的語系整段不套用；原文語系是已安裝的 baseLocale，否則繁中；
        // local: 列只收同一套系統匯出的（pack.instance 與本系統識別相同，兩邊都要有值；審查 r1 🔴3）
        var plan = analysis.Plan!;
        var instance = pack.DesignDataCount == 0 ? null : await systemSettings.GetValueAsync(SystemSetting.InstanceId, ct);
        var sameSystem = instance is { Length: > 0 } && string.Equals(pack.Instance, instance, StringComparison.Ordinal);
        var design = DesignDataAnalyzer.Analyze(pack, pack.DesignDataCount == 0 || plan.IsNew ? [] : await catalog.BuildReadOnlyAsync(ct), pack.Code, plan.IsNew,
            pack.BaseLocale is { } b && LocaleCode.TryNormalize(b, out var nb) && world.Installed.Any(i => i.Code == nb) ? nb : I18nText.FallbackLocale,
            world.DefaultLocale, sameSystem);
        plan = plan with { DesignData = design.Plan };

        var fileName = LocalePackStorage.SafeFileName(request.FileName);
        var fileHash = LocalePackStorage.FileHash(request.Content);
        var report = analysis.Report! with
        {
            FileName = fileName, FileHash = fileHash, ImportEnabled = settings.ImportEnabled, DesignData = design.Report,
            OrphanedDropped = plan.OverlayChanged ? new OrphanedDroppedReport(review.Ui.Orphaned, review.Server.Orphaned) : new OrphanedDroppedReport(0, 0),
        };
        if (!settings.ImportEnabled) return ServiceResult.Success(report);

        await packs.PrunePreviewsAsync(now, LocalePackLimits.MaxPreviews - 1, ct);
        var preview = new LocalePackPreview
        {
            Id = Guid.NewGuid().ToString("N"), UserId = current.UserId, Code = pack.Code, FileHash = fileHash, FileName = fileName,
            LocaleFingerprint = row is null ? null : LocalePackStorage.Fingerprint(row), CurrentPackId = currentPack?.Id,
            CreatedUtc = now, ExpiresUtc = now + LocalePackLimits.PreviewTtl,
        };
        report = report with { PreviewId = preview.Id, ExpiresUtc = preview.ExpiresUtc };
        preview.PlanJson = JsonSerializer.Serialize(plan, LocalePackStorage.Json);
        preview.ReportJson = JsonSerializer.Serialize(report, LocalePackStorage.Json);
        packs.AddPreview(preview);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(report);
    }

    private static ServiceResult Invalid(MessageRef message) =>
        ServiceResult.Invalid(LocalePackMessages.FileField, message, LocalePackMessages.CodeInvalid);
}
