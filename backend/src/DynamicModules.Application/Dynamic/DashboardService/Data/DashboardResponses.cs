using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.DashboardService.Data;

/// <summary>指派給我的儀表板一筆；Adjusted 表示我有個人版，HasUpdate 表示管理員在我的個人版之後更新過。</summary>
public record DashboardListItem(long Id, I18nText Name, int Version, bool Adjusted, bool HasUpdate);

public record MyDashboardItem(long Id, I18nText Name);

public record SharedReportItem(long Id, I18nText Name, string OwnerName);

public record DashboardList(List<DashboardListItem> Assigned, List<MyDashboardItem> Mine, List<SharedReportItem> SharedReports);

/// <summary>
/// Kind 是 assigned|mine；Layout 是對這位觀看者生效的版面（有個人版就是個人版）。
/// ReportNames 是卡片引用的報表名稱（key 是報表 id，值是多語名稱），只含觀看者看得到定義的報表，看不到的不洩漏名稱。
/// 名稱一律回多語物件，由前端依目前語系解析（名稱多語設計 §6.2）。
/// </summary>
public record DashboardDetail(
    long Id, I18nText Name, string Kind, int Version, bool Adjusted, bool HasUpdate, DashboardLayout Layout,
    IReadOnlyDictionary<long, I18nText> ReportNames);

public record AdminDashboardItem(long Id, I18nText Name, int Version, int Sequence, List<long> GroupIds, int CardCount);

public record AdminDashboardDetail(long Id, I18nText Name, int Version, int Sequence, List<long> GroupIds, DashboardLayout Layout);

public static class DashboardMapper
{
    public static AdminDashboardDetail ToAdminDetail(this Dashboard d) =>
        new(d.Id, d.NameI18n, d.Version, d.Sequence, d.Assignments.Select(a => a.GroupId).Order().ToList(), DashboardLayout.Parse(d.LayoutJson));

    public static AdminDashboardItem ToAdminItem(this Dashboard d) =>
        new(d.Id, d.NameI18n, d.Version, d.Sequence, d.Assignments.Select(a => a.GroupId).Order().ToList(), DashboardLayout.Parse(d.LayoutJson).Cards.Count);
}
