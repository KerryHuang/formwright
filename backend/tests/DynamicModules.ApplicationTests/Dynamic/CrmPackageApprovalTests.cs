using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>內建 CRM（apps/crm-sales.app.json）帶的簽核流程：設計案「客戶確認中 → 待主管確認」需簽核，流程是負責業務的一層主管（簽核 a5）。</summary>
public class CrmPackageApprovalTests
{
    private static readonly Lazy<AppPackageData> Package = new(() =>
        AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns.Single(p => p.Manifest.Id == "crm-sales"));

    [Fact]
    public void Package_DeclaresDesignCaseReviewFlow()
    {
        var p = Package.Value;
        p.FormatVersion.Should().Be(10);
        AppPackageValidator.Validate(p).Errors.Should().BeEmpty();

        var flow = p.ApprovalFlows.Should().ContainSingle().Subject;
        flow.Code.Should().Be("crm-design-case-review");
        (flow.Module, flow.To, flow.RejectTo, flow.Shape).Should().Be(("design-case", "manager-review", "designing", "graph"), "格式 10 起簽核流程一律是圖（名稱多語設計 §24）");
        (flow.Active, flow.Priority).Should().Be((true, 1));
        flow.When.Should().BeNull();
        flow.FallbackApprovers.Should().BeNull("省略時由政策預設（出廠為系統管理員）頂替");
        // 手冊 §22 的劇本照這兩個繁中名稱寫，改名要連手冊一起改
        flow.Name.Resolve("zh-TW").Should().Be("設計案主管確認");
        var stage = flow.Nodes!.Where(n => n.Kind == "approval").Should().ContainSingle().Subject;
        stage.Name!.Resolve("zh-TW").Should().Be("負責業務主管");
        foreach (var locale in new[] { "zh-TW", "en-US", "th", "vi" })
        {
            flow.Name.Resolve(locale).Should().NotBeNullOrWhiteSpace(locale);
            stage.Name!.Resolve(locale).Should().NotBeNullOrWhiteSpace(locale);
        }

        flow.Stages.Should().BeNull();
        flow.Edges!.Select(e => $"{e.From}>{e.To}").Should().Equal("start>owner-manager", "owner-manager>end");
        stage.Id.Should().Be("owner-manager");
        (stage.Approvers!.Source, stage.Approvers.Level).Should().Be(("manager", 1));
        (stage.Mode, stage.OnEmpty, stage.DueHours).Should().Be(("any", "fallback", null));
        stage.EditableFields.Should().BeNullOrEmpty();
        stage.When.Should().BeNull();
    }

    [Fact]
    public void Package_OnlyReviewEntryNeedsApproval()
    {
        var wf = Package.Value.Workflows.Single(w => w.Module == "design-case");
        var needs = wf.Transitions.Where(t => t.Approval is not null).ToList();
        var only = needs.Should().ContainSingle().Subject;
        (only.From, only.To).Should().Be(("customer-review", "manager-review"));
        (only.Approval!.Required, only.Approval.NoMatch).Should().Be((true, "block"));
        Package.Value.Workflows.Where(w => w.Module != "design-case")
            .SelectMany(w => w.Transitions).Should().OnlyContain(t => t.Approval == null);
    }
}
