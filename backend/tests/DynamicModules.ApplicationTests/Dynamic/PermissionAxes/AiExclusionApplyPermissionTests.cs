using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldAiExclusion;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 套件、共用主檔寫入「不提供給 AI」的權限設定檢查（名稱多語設計 §8.1、§24）。persona 見 <see cref="AuthorizationBoundaryTestBase"/>：
/// wang 只有設計器、chen 兩者都有。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class AiExclusionApplyPermissionTests : AuthorizationBoundaryTestBase
{
    private const string Template = """
        { "formatVersion": 2, "key": "t-ai-app", "name": { "zh-TW": "旗標套件" },
          "modules": [ { "code": "ppl", "key": "t-ai-app-ppl", "name": { "zh-TW": "人員" }, "kind": "standalone", "titleField": "title",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "title", "type": "text", "label": { "zh-TW": "姓名" } },
              { "code": "idNo", "type": "text", "label": { "zh-TW": "身分證字號" }, "excludeFromAi": true } ] } ],
            "permissions": [] } ] }
        """;

    private Task<bool> Flag(long fieldId) =>
        Db.Context.Set<Field>().AsNoTracking().Where(f => f.Id == fieldId).Select(f => f.ExcludeFromAi).SingleAsync();

    /// <summary>
    /// P11 的應用層（i3c 審查 🟢6）：開啟套件（app 不是 null）不略過旗標，沒有權限設定時回 403 permissionAdminRequired。
    /// HTTP 層開啟要系統管理員、系統管理員一律有權限設定，這條第二道防線只能在這裡直接呼叫。
    /// </summary>
    [Fact]
    public async Task P11_OpeningPackage_FlaggedField_WithoutPermissionAdmin_403WithCode()
    {
        var (package, error) = TemplateJson.Parse(Template);
        package.Should().NotBeNull(error);
        As("wang");
        using var scope = NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<TemplateInstantiator>().InstantiateAsync(
            package!, new Dictionary<string, InstantiateModuleInput>(), new HashSet<string>(), CancellationToken.None,
            new AppInstantiation(new Dictionary<string, long>(), new Dictionary<string, string>(), [], []));

        result.IsForbidden.Should().BeTrue(result.Message);
        result.Code.Should().Be("permissionAdminRequired");
    }

    /// <summary>共用主檔重匯補旗標用的只改旗標請求（i3c 審查 🟡4）：要權限設定；只加不減，已是 true 不經 gate。</summary>
    [Fact]
    public async Task SetFieldAiExclusion_NeedsPermissionAdmin_AlreadyTrueIsANoOp()
    {
        As("wang");
        var denied = await Send(new SetFieldAiExclusionRequest(Target));
        denied.IsForbidden.Should().BeTrue();
        denied.Code.Should().Be("permissionAdminRequired");
        (await Flag(Target)).Should().BeFalse();

        As("chen");
        var ok = await Send(new SetFieldAiExclusionRequest(Target));
        ok.IsSuccess.Should().BeTrue(ok.Message);
        (await Flag(Target)).Should().BeTrue();

        As("wang");
        (await Send(new SetFieldAiExclusionRequest(Target))).IsSuccess.Should().BeTrue("已是 true：沒有改變，不經 gate");
        (await Flag(Target)).Should().BeTrue();
    }
}
