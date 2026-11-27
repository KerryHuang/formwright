using System.Text.Json;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// mcp2b 執行器的高風險分支（MCP 設計 §6.3.1、§6.4），用正式 <c>delete_record</c> 刪真的記錄：elicitation 預演不帶碼、
/// 碼的發出方式與送回方式不同一律 confirmation_invalid（兩個方向）、requestState 不是本系統發的、高風險政策壞值或讀不到時拒絕寫入。
/// 端到端（SDK client 真的跳對話框）在 WebAPI 的 McpHighRiskToolsApiTests。
/// </summary>
[Trait("Category", "Postgres")]
public class McpHighRiskInvokerTests : McpWriteTestBase
{
    private const string Delete = "delete_record";

    private string RowVersionOf(long id) => Db.Context.Set<Record>().AsNoTracking().Single(r => r.Id == id).RowVersion;

    private bool Exists(long id) => Db.Context.Set<Record>().AsNoTracking().Any(r => r.Id == id && !r.DeleteFlag);

    private string DeleteArgs(long id, string? code = null)
    {
        var args = new Dictionary<string, object> { ["recordId"] = id, ["rowVersion"] = RowVersionOf(id), ["idempotencyKey"] = "k-fixed-delete-" + id };
        if (code is not null) args["confirmationCode"] = code;
        return JsonSerializer.Serialize(args);
    }

    private async Task<McpFormattedResult> Run(string args, McpCallContext context)
    {
        Db.Context.ChangeTracker.Clear();
        try { return await Invoke(Delete, args, context); }
        finally { Db.Context.ChangeTracker.Clear(); }
    }

    private static McpCallContext Eliciting(string? state = null, McpElicitationAnswer? answer = null) =>
        new("test-client", "1.0", null, ElicitationAvailable: true, RequestState: state, Answer: answer);

    private static readonly McpCallContext Plain = new("test-client", "1.0");

    private List<McpConfirmation> Codes() => Db.Context.Set<McpConfirmation>().AsNoTracking().OrderBy(c => c.Id).ToList();

    private async Task<string> ElicitationState()
    {
        var r = await Run(DeleteArgs(Target), Eliciting());
        r.InputRequired.Should().NotBeNull(r.Text);
        return r.InputRequired!.RequestState;
    }

    [Fact]
    public async Task ElicitationPreview_StateHoldsCode_ResultHasNone()
    {
        var r = await Run(DeleteArgs(Target), Eliciting());

        r.InputRequired!.RequestState.Should().StartWith("fwe1.");
        r.InputRequired.Message.Should().Contain(Delete).And.Contain(Target.ToString());
        r.InputRequired.ToString().Should().NotContain(r.InputRequired.RequestState, "ToString 遮掉內容");
        r.Text.Should().NotContain("code");
        var row = Codes().Should().ContainSingle().Subject;
        row.Method.Should().Be(McpConfirmationMethod.Elicitation);
        row.CodeHash.Should().Be(McpSecrets.Hash(McpElicitationState.CodeOf(r.InputRequired.RequestState)!));
        var audit = (await McpRows()).Should().ContainSingle().Subject;
        (audit.ConfirmationOutcome, audit.RiskLevel, audit.ConfirmationId).Should().Be((AuditConfirmationOutcome.Preview, AuditRiskLevel.HighRisk, row.Id));
        audit.SummaryJson.Should().Contain("\"confirm\":\"elicitation\"");
    }

    [Fact]
    public async Task Accept_Executes()
    {
        var state = await ElicitationState();

        var r = await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("accept", true)));

        r.IsError.Should().BeFalse(r.Text);
        Exists(Target).Should().BeFalse();
        Codes().Single().UsedUtc.Should().NotBeNull();
    }

    /// <summary>elicitation 發的碼改用 confirmationCode 送（§6.3.1 第 4 點）：confirmation_invalid、碼不消耗、不刪。</summary>
    [Fact]
    public async Task ElicitationCode_AsConfirmationCode_Invalid()
    {
        var code = McpElicitationState.CodeOf(await ElicitationState())!;

        var r = await Run(DeleteArgs(Target, code), Plain);

        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationInvalid);
        Exists(Target).Should().BeTrue();
        Codes().Single().UsedUtc.Should().BeNull();
        (await McpRows()).Last().ConfirmationOutcome.Should().Be(AuditConfirmationOutcome.Mismatch);
    }

    /// <summary>反方向：確認碼路徑發的碼包成 requestState 送，同意或拒絕都是 confirmation_invalid，碼不作廢（不能拿來作廢別人的碼）。</summary>
    [Theory]
    [InlineData("accept", true)]
    [InlineData("decline", null)]
    public async Task CodePathCode_AsRequestState_Invalid_NotVoided(string action, bool? confirmed)
    {
        var preview = await Run(DeleteArgs(Target), Plain);
        var code = preview.Structured.GetProperty("meta").GetProperty("confirmation").GetProperty("code").GetString()!;

        var r = await Run(DeleteArgs(Target), Eliciting(McpElicitationState.Of(code), new McpElicitationAnswer(action, confirmed)));

        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationInvalid);
        Exists(Target).Should().BeTrue();
        var row = Codes().Single();
        (row.UsedUtc, row.VoidedUtc, row.DeclinedUtc).Should().Be((null, null, null));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("fwe1.")]
    [InlineData("fwe1.not-a-code")]
    public async Task ForeignRequestState_Invalid(string state)
    {
        var r = await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("accept", true)));

        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationInvalid);
        Exists(Target).Should().BeTrue();
    }

    /// <summary>同一個 elicitation 碼拒絕後不能再拿來同意。</summary>
    [Fact]
    public async Task Declined_ThenAccept_Invalid()
    {
        var state = await ElicitationState();
        CodeOf(await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("cancel", null)))).Should().Be(McpErrorCodes.ConfirmationDeclined);

        var r = await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("accept", true)));

        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationInvalid);
        Exists(Target).Should().BeTrue();
    }

    /// <summary>移出清單後：帶了確認碼也忽略，直接執行（一般寫入）；稽核級別 Write。</summary>
    [Fact]
    public async Task RemovedFromList_CodeIgnored_DirectWrite()
    {
        await SetSystem(PolicyCatalog.McpHighRiskTools, "approve,reject");

        var r = await Run(DeleteArgs(Target, "ignored-code"), Eliciting());

        r.IsError.Should().BeFalse(r.Text);
        r.InputRequired.Should().BeNull();
        Exists(Target).Should().BeFalse();
        Codes().Should().BeEmpty();
        var audit = (await Db.Context.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == "mcp.deleteRecord").ToListAsync()).Should().ContainSingle().Subject;
        (audit.RiskLevel, audit.ConfirmationOutcome).Should().Be((AuditRiskLevel.Write, null));
    }

    /// <summary>
    /// R1：對話框還沒回答時管理員把工具移出清單，使用者拒絕、取消、沒勾、沒回答：一律 confirmation_declined、不刪、碼作廢；
    /// 帶 requestState 的請求不因現在降為一般寫入就略過回答驗證。
    /// </summary>
    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    [InlineData(null, null)]
    public async Task RemovedWhilePending_NotApproved_Declined_NoWrite(string? action, bool? confirmed)
    {
        var state = await ElicitationState();
        await SetSystem(PolicyCatalog.McpHighRiskTools, "approve,reject");

        var r = await Run(DeleteArgs(Target), Eliciting(state, action is null ? null : new McpElicitationAnswer(action, confirmed)));

        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationDeclined, r.Text);
        Exists(Target).Should().BeTrue();
        Codes().Single().DeclinedUtc.Should().NotBeNull();
    }

    /// <summary>R1：拒絕後移出清單，再拿同一個 state 送同意：不得寫入（state 一律驗）。</summary>
    [Fact]
    public async Task Declined_ThenRemoved_ThenAccept_NoWrite()
    {
        var state = await ElicitationState();
        CodeOf(await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("decline", null)))).Should().Be(McpErrorCodes.ConfirmationDeclined);
        await SetSystem(PolicyCatalog.McpHighRiskTools, "approve,reject");

        var r = await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("accept", true)));

        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationInvalid, r.Text);
        Exists(Target).Should().BeTrue();
    }

    /// <summary>R1：移出清單後同意待回答的對話框：照確認原本的安全要求驗碼後執行，稽核仍記高風險與 elicitation。</summary>
    [Fact]
    public async Task RemovedWhilePending_Accept_ConfirmedThenExecutes()
    {
        var state = await ElicitationState();
        await SetSystem(PolicyCatalog.McpHighRiskTools, "approve,reject");

        var r = await Run(DeleteArgs(Target), Eliciting(state, new McpElicitationAnswer("accept", true)));

        r.IsError.Should().BeFalse(r.Text);
        Exists(Target).Should().BeFalse();
        Codes().Single().UsedUtc.Should().NotBeNull();
        var audit = (await McpRows()).Last();
        (audit.RiskLevel, audit.ConfirmationOutcome).Should().Be((AuditRiskLevel.HighRisk, AuditConfirmationOutcome.Confirmed));
    }

    /// <summary>
    /// R2：管理員加嚴（update_record 加進清單、不支援對話框時拒絕）之後，兩項高風險政策任一存了壞值或讀取丟例外：
    /// 寫入類呼叫一律拒絕（不退回出廠清單、不放寬成確認碼），不發碼、不寫；唯讀照常。
    /// </summary>
    [Theory]
    [InlineData("toolsBad")]
    [InlineData("choiceBad")]
    [InlineData("readThrows")]
    public async Task HighRiskPolicyUnusable_WritesRejected_ReadsWork(string fault)
    {
        await SetSystem(PolicyCatalog.McpHighRiskTools, "update_record,delete_record,approve,reject");
        await SetSystem(PolicyCatalog.McpHighRiskWithoutElicitation, PolicyCatalog.McpWithoutElicitationReject);
        switch (fault)
        {
            case "toolsBad": await SetSystem(PolicyCatalog.McpHighRiskTools, "update_record,query_records"); break;
            case "choiceBad": await SetSystem(PolicyCatalog.McpHighRiskWithoutElicitation, "allow"); break;
            default: _policyFault.On = true; break;
        }
        var before = ValuesOf(Target);

        var delete = await Run(DeleteArgs(Target), Plain);
        var write = await Run2(TestWriteTool.Name, WriteArgs());
        var read = await Run2("fake_list", "{}");

        CodeOf(delete).Should().Be(McpErrorCodes.Forbidden, delete.Text);
        CodeOf(write).Should().Be(McpErrorCodes.Forbidden, write.Text);
        Exists(Target).Should().BeTrue();
        ValuesOf(Target).Should().Be(before);
        Codes().Should().BeEmpty("不發確認碼");
        read.IsError.Should().BeFalse(read.Text);
    }

    /// <summary>R2：兩項都沒設定時照出廠值（delete_record 高風險、沒對話框退回確認碼）。</summary>
    [Fact]
    public async Task HighRiskPolicyUnset_FactoryApplies()
    {
        var r = await Run(DeleteArgs(Target), Plain);

        r.IsError.Should().BeFalse(r.Text);
        r.Structured.GetProperty("meta").TryGetProperty("confirmation", out _).Should().BeTrue();
        Exists(Target).Should().BeTrue();
    }

    private async Task<McpFormattedResult> Run2(string tool, string args)
    {
        Db.Context.ChangeTracker.Clear();
        try { return await Invoke(tool, args, Plain); }
        finally { Db.Context.ChangeTracker.Clear(); }
    }

    private readonly HighRiskPolicyFault _policyFault = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_policyFault);
        services.AddScoped<PolicyReader>();
        services.AddScoped<IPolicyReader>(sp => new FaultyHighRiskReader(sp.GetRequiredService<PolicyReader>(), _policyFault));
    }

    public sealed class HighRiskPolicyFault { public bool On { get; set; } }

    /// <summary>讀兩項高風險政策時丟例外（模擬資料庫讀取失敗），其他政策照常。</summary>
    private sealed class FaultyHighRiskReader(IPolicyReader inner, HighRiskPolicyFault fault) : IPolicyReader
    {
        private void Check(PolicyDefinition def)
        {
            if (fault.On && (def == PolicyCatalog.McpHighRiskTools || def == PolicyCatalog.McpHighRiskWithoutElicitation))
                throw new InvalidOperationException("模擬讀取失敗");
        }

        public Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { Check(def); return inner.ResolveAsync(def, moduleId, ct); }
        public Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { Check(def); return inner.IntAsync(def, moduleId, ct); }
        public Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { Check(def); return inner.BoolAsync(def, moduleId, ct); }
        public Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { Check(def); return inner.ChoiceAsync(def, moduleId, ct); }
        public Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { Check(def); return inner.ListAsync(def, moduleId, ct); }
        public void Invalidate() => inner.Invalidate();
    }
}
