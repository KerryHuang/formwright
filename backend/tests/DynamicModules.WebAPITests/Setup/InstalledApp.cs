using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>
/// 安裝版的一般模式主機：只給 <c>Install:ConfigFile</c>，其餘（資料庫、金鑰、備份位置）全部來自設定檔，用真實 cookie 登入。
/// 「選用時強制系統管理員啟用雙因素」的出廠值換成否（同 <c>TestApp</c>）：正式出廠值下剛裝好的 admin 在綁定雙因素前打不了管理 API，
/// 那不是這組測試要驗的事。
/// </summary>
public sealed class InstalledApp(string configFile) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(DynamicModules.WebAPI.Setup.InstallConfig.ConfigFileKey, configFile);
        builder.UseEnvironment("Production");
        builder.ConfigureTestServices(s =>
        {
            if (s.LastOrDefault(d => d.ServiceType == typeof(PolicyEnvironmentDefaults))?.ImplementationInstance is not PolicyEnvironmentDefaults defaults) return;
            var values = PolicyCatalog.All.ToDictionary(d => d.Code, defaults.FactoryOf, StringComparer.Ordinal);
            values[PolicyCatalog.AuthTwoFactorRequireAdmins.Code] = "false";
            s.AddSingleton(new PolicyEnvironmentDefaults(values));
        });
    }
}
