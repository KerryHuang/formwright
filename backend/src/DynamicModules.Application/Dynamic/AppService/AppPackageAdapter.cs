using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 把 v2 套件包成一個 TemplatePackage，讓既有的 TemplateValidator、TemplateGraph、TemplateInstantiator 原封不動。
/// 範本格式對應套件格式的語系鍵規則（名稱多語設計 §9 第 5 點）：套件格式 2–9 包成範本格式 1（只收四語、不收 excludeFromAi），
/// 格式 10 起包成範本格式 2。
/// </summary>
public static class AppPackageAdapter
{
    public static TemplatePackage ToTemplate(AppPackageData p) =>
        new(TemplateFormatOf(p.FormatVersion), p.Manifest.Id, p.Manifest.Name, p.Manifest.Description, p.Modules, null);

    public static int TemplateFormatOf(int appFormatVersion) =>
        appFormatVersion >= AppPackageValidator.LocaleCodeFormatVersion ? TemplatePackage.CurrentFormatVersion : TemplatePackage.MinFormatVersion;

    /// <summary>
    /// 範本檔當應用程式套件開啟時（v1 升級）升成的套件格式：範本格式 1 升成格式 9（只收四語、不收 excludeFromAi），
    /// 範本格式 2 升成目前格式。語系鍵與旗標的規則因此只寫在套件驗證器一處。
    /// </summary>
    public static int AppFormatOf(int templateFormatVersion) =>
        templateFormatVersion >= TemplatePackage.CurrentFormatVersion ? AppPackageData.CurrentFormatVersion : AppPackageValidator.LocaleCodeFormatVersion - 1;
}
