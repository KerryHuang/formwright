using DynamicModules.Domain.Approvals;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>通知節點的收件人解析（流程圖設計 §12.1、§12.4；計畫 fg5a）。</summary>
public class ApprovalNotifyRecipientsTests
{
    private static readonly IReadOnlySet<string> Active = new HashSet<string> { "amy", "ben", "cara", "dan", "eve", "mgr1", "mgr2" };

    private static NotifyAudienceInput Input(Dictionary<long, IReadOnlyList<string>>? groups = null, Func<int, string?>? manager = null,
        IReadOnlySet<string>? active = null, string initiator = "amy") =>
        new(initiator, active ?? Active, groups ?? new(), manager ?? (level => level switch { 1 => "mgr1", 2 => "mgr2", _ => null }));

    private static NotifyRecipientDef Users(params string[] ids) => new(ApprovalNotifyRecipientKinds.Users, UserIds: [.. ids]);
    private static NotifyRecipientDef Group(params long[] ids) => new(ApprovalNotifyRecipientKinds.Group, GroupIds: [.. ids]);
    private static readonly NotifyRecipientDef Initiator = new(ApprovalNotifyRecipientKinds.Initiator);
    private static NotifyRecipientDef Manager(int? level) => new(ApprovalNotifyRecipientKinds.InitiatorManager, Level: level);

    [Fact]
    public void FourKinds_InSettingOrder()
    {
        var r = ApprovalNotifyRecipients.Resolve([Manager(2), Users("dan", "ben"), Initiator, Group(7)],
            Input(new() { [7] = ["eve", "cara"] }));
        r.UserIds.Should().Equal("mgr2", "dan", "ben", "amy", "cara", "eve");
        r.Total.Should().Be(6);
        r.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Duplicates_KeepFirstOccurrence()
    {
        var r = ApprovalNotifyRecipients.Resolve([Users("ben"), Group(1), Initiator, Users("amy", "ben")], Input(new() { [1] = ["cara", "ben"] }));
        r.UserIds.Should().Equal("ben", "cara", "amy");
    }

    [Fact]
    public void InactiveUsers_Removed()
    {
        var r = ApprovalNotifyRecipients.Resolve([Users("ben", "ghost"), Group(1)], Input(new() { [1] = ["left", "cara"] }));
        r.UserIds.Should().Equal("ben", "cara");
    }

    [Fact]
    public void InitiatorWithoutManager_Empty()
    {
        var r = ApprovalNotifyRecipients.Resolve([Manager(1)], Input(manager: _ => null));
        r.UserIds.Should().BeEmpty();
        r.Total.Should().Be(0);
    }

    [Fact]
    public void EmptyGroup_Empty()
    {
        ApprovalNotifyRecipients.Resolve([Group(9)], Input(new() { [9] = [] })).UserIds.Should().BeEmpty();
        ApprovalNotifyRecipients.Resolve([Group(404)], Input()).UserIds.Should().BeEmpty();   // 群組不在了
    }

    [Fact]
    public void ManagerWithoutLevel_IsLevelOne()
    {
        var asked = new List<int>();
        var r = ApprovalNotifyRecipients.Resolve([Manager(null)], Input(manager: l => { asked.Add(l); return "mgr1"; }));
        asked.Should().Equal(1);
        r.UserIds.Should().Equal("mgr1");
    }

    [Fact]
    public void Initiator_IsRecipient_NoSelfApprovalPolicy()
    {
        ApprovalNotifyRecipients.Resolve([Initiator], Input()).UserIds.Should().Equal("amy");
    }

    [Fact]
    public void Over200_TruncatedTo200()
    {
        var users = Enumerable.Range(0, 201).Select(i => $"u{i:D3}").ToList();
        var r = ApprovalNotifyRecipients.Resolve([Group(1)], Input(new() { [1] = users }, active: users.ToHashSet()));
        r.UserIds.Should().HaveCount(ApprovalGraphLimits.MaxResolvedRecipients);
        r.UserIds[0].Should().Be("u000");
        r.UserIds[^1].Should().Be("u199");
        r.Total.Should().Be(201);
        r.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Exactly200_NotTruncated()
    {
        var users = Enumerable.Range(0, 200).Select(i => $"u{i:D3}").ToList();
        var r = ApprovalNotifyRecipients.Resolve([Group(1)], Input(new() { [1] = users }, active: users.ToHashSet()));
        r.UserIds.Should().HaveCount(200);
        r.Truncated.Should().BeFalse();
    }

    [Fact]
    public void GroupOrder_IndependentOfInputOrder()
    {
        var a = ApprovalNotifyRecipients.Resolve([Group(1)], Input(new() { [1] = ["eve", "ben", "dan"] }));
        var b = ApprovalNotifyRecipients.Resolve([Group(1)], Input(new() { [1] = ["dan", "eve", "ben"] }));
        a.UserIds.Should().Equal("ben", "dan", "eve");
        b.UserIds.Should().Equal(a.UserIds);
    }

    [Fact]
    public void UnknownKind_NobodyResolved()
    {
        ApprovalNotifyRecipients.Resolve([new NotifyRecipientDef("everyone")], Input()).UserIds.Should().BeEmpty();
    }
}
