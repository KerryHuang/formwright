using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Files;
using DynamicModules.Persistence.Postgres;
using DynamicModules.Persistence.Seed;
using DynamicModules.WebAPI.Attachments;
using DynamicModules.WebAPI.Demo;
using DynamicModules.WebAPI.Mcp;
using DynamicModules.WebAPI.Ops;
using DynamicModules.WebAPI.Security;
using DynamicModules.WebAPI.Setup;
using DynamicModules.WebAPI.Time;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

// Windows 服務啟動時目前目錄是 System32、sc create 沒有工作目錄參數，content root 要明指程式目錄，wwwroot 才找得到
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
// 安裝版設定檔是最高優先的來源，要在讀 Database:Provider 之前加上（安裝版設計 TD-01）
InstallConfig.AddConfigFile(builder.Configuration);
// 不在服務管理員底下時兩者都不做事（dotnet run、Docker、測試不受影響）
builder.Services.AddWindowsService(o => o.ServiceName = "Formwright");
builder.Services.AddSystemd();

// 資料庫種類由 Database:Provider 決定（預設 Sqlite，PostgreSQL provider 設計 §5.1）；只在設定有效時才往下
var databaseProvider = DatabaseProviders.Read(builder.Configuration);
var startupWarnings = new List<string>();
string dynamicConnection;
if (databaseProvider == DatabaseProvider.Sqlite)
{
    // 相對路徑一律以 content root 解析成絕對路徑，建目錄與開檔用同一個位置。
    dynamicConnection = SqliteConnectionStrings.ResolveDataSource(
        builder.Configuration.GetConnectionString("Dynamic")!, builder.Environment.ContentRootPath);
    builder.Configuration["ConnectionStrings:Dynamic"] = dynamicConnection;
    if (!string.IsNullOrWhiteSpace(builder.Configuration[PostgresWriteSettings.LockTimeoutKey]))
        startupWarnings.Add($"{PostgresWriteSettings.LockTimeoutKey} 只在 Database:Provider=Postgres 時生效，SQLite 等鎖的上限看連線字串的 Default Timeout");
}
else
{
    dynamicConnection = builder.Configuration.GetConnectionString("Dynamic") is { Length: > 0 } pg
        ? pg : throw new InvalidOperationException("Database:Provider=Postgres 需要設定 ConnectionStrings:Dynamic");
    // appsettings.json 出廠就有 Sqlite:MmapSizeMb=0（關閉），只有真的開了才提醒
    if (long.TryParse(builder.Configuration["Sqlite:MmapSizeMb"], out var mmapMb) && mmapMb > 0)
        startupWarnings.Add("Sqlite:MmapSizeMb 只在 Database:Provider=Sqlite 時生效，PostgreSQL 不套用");
}
// 資料庫檔案所在目錄（SQLite 檔案型才有；記憶體資料庫是 null）：建目錄、預設金鑰目錄與附件目錄都依它
var databaseDirectory = DatabaseProviders.FileDirectory(databaseProvider, dynamicConnection);
// 聽的位址：Server:Port（安裝版）→ PORT（雲端主機如 Render，聽所有介面）→ 本機開發 127.0.0.1:5180
builder.WebHost.UseUrls(ListenAddress.Resolve(builder.Configuration, Environment.GetEnvironmentVariable("PORT")));
builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);
builder.Services.AddApplication();
DynamicModules.WebAPI.Ops.TaskRuleFaultInjection.Configure(builder.Services, builder.Configuration, builder.Environment, startupWarnings);
builder.Services.AddSingleton(new ReportOptions
{
    MaxRows = builder.Configuration.GetValue("Reports:MaxRows", 50_000),
    MaxLoadRows = builder.Configuration.GetValue("Reports:MaxLoadRows", 200_000),
    Engine = builder.Configuration.GetValue("Reports:Engine", ReportEngineMode.Auto),
    SqlTimeoutSeconds = builder.Configuration.GetValue("Reports:SqlTimeoutSeconds", 30.0),
});
builder.Services.AddHttpContextAccessor();
if (databaseProvider == DatabaseProvider.Postgres) builder.Services.AddPostgresDatabase(builder.Configuration);
else builder.Services.AddSqliteDatabase(builder.Configuration);
// 須在 provider 註冊之後：provider 經 AddPersistenceRepositories 註冊 IPermissionRepository，MCP 通道的 AI 遮罩包在它外面（mcp1a 判斷 10）
builder.Services.AddChannelPermissionMasking();
builder.Services.AddScoped<IRequestChannel, DynamicModules.WebAPI.Mcp.HttpRequestChannel>();
// AI 連線（MCP，mcp1a 判斷 11）：SDK stateless、閘門與 /mcp 端點在下面的管線
builder.Services.AddFormwrightMcp();
if (builder.Configuration.GetValue<bool>("Mcp:AllowHttp"))
    startupWarnings.Add("Mcp:AllowHttp 已開啟：/mcp 接受非 HTTPS 的連線，權杖會以明文傳輸；只能用在內網或測試環境");
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
builder.Services.AddScoped<DynamicModules.Application.Common.ICurrentLocale, HeaderCurrentLocale>();
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<ClientIpResolver>();
builder.Services.AddScoped<DynamicModules.Application.Common.Audit.IRequestOrigin, HttpRequestOrigin>();
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.TaskService.ITaskUndoTokens, DynamicModules.WebAPI.Security.DataProtectionTaskUndoTokens>();
builder.Services.AddSingleton<ClientErrorThrottle>();
// 維運資訊：這個程序的身分與建置／部署資訊
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.IOpsEnvironment, OpsEnvironment>();
builder.Services.AddSingleton(sp => DynamicModules.Application.Dynamic.OpsService.OpsInstance.Create(sp.GetRequiredService<TimeProvider>()));
// 背景排程器：工作登記在 AddApplication；Ops:Scheduler:Enabled=false 時不跑（多實例只讓其中一台跑、除錯用）
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.Scheduling.JobRunner>();
builder.Services.AddHostedService<SchedulerHostedService>();
// 看門狗與事件收件匣（維運設計 §17.2.3、§17.2.4）：每個實例都跑；Ops:Watchdog:Enabled=false 只停看門狗，收件匣照常 flush
builder.Services.AddHostedService<OpsWatchdogHostedService>();
builder.Services.AddHostedService<IncidentInboxFlusher>();
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.Health.IDiskSpace, DriveInfoDiskSpace>();
// 寄信（維運設計 §17.2.6、帳號安全設計 §6）：MailKit 寄 SMTP、Data Protection 加密密碼。站台網址 App:PublicUrl（沒設時讀舊鍵 Ops:PublicUrl）
// 是信件連結的網址前綴：告警信沒設就不放連結，帳號信沒設就寄不出帶連結的信（不用請求的 Host 標頭）
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.MailService.IMailSender, MailKitMailSender>();
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.Alerts.IAlertSecretProtector, DataProtectionAlertSecretProtector>();
builder.Services.AddSingleton(new DynamicModules.Application.Dynamic.OpsService.Alerts.AlertMailOptions(SitePublicUrl.Read(builder.Configuration)));
startupWarnings.AddRange(SitePublicUrl.Warnings(builder.Configuration, builder.Environment.IsDevelopment()));
builder.Services.AddHostedService<MailDeliveryHostedService>();
builder.Services.AddSingleton(new AuthSettings(builder.Configuration.GetValue<bool>("Demo:LoginEnabled")));
// 語系包匯入（語系包 i4d1 計畫判斷 15）：沒明確設定時公開 Demo 站（Demo:LoginEnabled）關閉，只能預覽報告
builder.Services.AddSingleton(new DynamicModules.Application.Dynamic.LocaleService.Packs.LocalePackSettings(
    builder.Configuration.GetValue<bool?>("Localization:PackImportEnabled") ?? !builder.Configuration.GetValue<bool>("Demo:LoginEnabled")));
// 附件：預設放在資料庫旁的 attachments 目錄（記憶體資料庫則放暫存目錄）。Demo 登入開啟時預設不開放上傳，可用 Attachments:UploadEnabled 明確覆寫。
var (attachmentSettings, policyDefaults, attachmentWarnings) = AttachmentSettingsFactory.Create(builder.Configuration, databaseDirectory);
builder.Services.AddSingleton(attachmentSettings);
builder.Services.AddSingleton(policyDefaults);
builder.Services.AddSingleton<IAttachmentStore>(new LocalAttachmentStore(attachmentSettings.Root));
if (databaseProvider == DatabaseProvider.Postgres && string.IsNullOrWhiteSpace(builder.Configuration["Attachments:Root"]))
    startupWarnings.Add($"Database:Provider=Postgres 沒有設定 Attachments:Root，附件暫存在 {attachmentSettings.Root}，重開機可能遺失；正式環境請設定");
// 鎖定判斷用真實時間；IBusinessTime 會被 ActingScope 覆寫，不能用
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IBusinessTime, ZoneBusinessTime>();
// 須在 ICurrentUser、IBusinessTime 註冊之後：Demo 資料產生器靠它在範圍內切換身分與時間
builder.Services.AddActingOverrides();
builder.Services.AddSingleton<IDemoDataRunner, DemoDataRunner>();
// 建置 Demo 映像（--generate-demo-data）時不寫 mfa01 的 TOTP 密文：建置後金鑰會被刪掉（帳號安全設計 §13.3）
builder.Services.AddSingleton(new DemoBuildMode(args.Contains("--generate-demo-data")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
// 站台在反向代理後面（Render）時才信任 X-Forwarded-Proto，否則 Secure cookie 不會生效
var trustForwardedHeaders = builder.Configuration.GetValue<bool>("Auth:TrustForwardedHeaders");
if (trustForwardedHeaders)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
// DataProtection 金鑰落地，重開站台後登入狀態不失效：有設 DataProtection:KeysDirectory 就用它（相對路徑以 content root 解析），
// 否則資料庫是檔案時放在同一個目錄的 keys；都沒有（記憶體資料庫）就不落地
var keysDirectory = builder.Configuration["DataProtection:KeysDirectory"] is { Length: > 0 } configuredKeys
    ? Path.GetFullPath(configuredKeys, builder.Environment.ContentRootPath)
    : databaseDirectory is null ? null : Path.Combine(databaseDirectory, "keys");
// 非 Development 環境金鑰不落地就拒絕啟動，不分資料庫種類（帳號安全設計 §8.7，T27、複審 N3、N7）：在 Build 之前、還沒連資料庫
if (keysDirectory is null && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("非開發環境必須設定 DataProtection:KeysDirectory（否則重啟後登入、寄信密碼與雙因素全部失效）");
// 在 Data Protection 第一次使用前看金鑰環是不是空的（這次會產生新金鑰）：infra.keysMayBeLost 的判斷用（複審建議 B）
var keyRingWasEmpty = keysDirectory is not null
    && (!Directory.Exists(keysDirectory) || !Directory.EnumerateFiles(keysDirectory, "key-*.xml").Any());
builder.Services.AddSingleton(new DynamicModules.Application.Common.Security.KeyPersistenceStatus(keysDirectory is not null, keyRingWasEmpty));
if (keysDirectory is not null)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
else
    startupWarnings.Add("沒有設定 DataProtection:KeysDirectory，金鑰不落地（只有 Development 環境允許）：雙因素驗證不能啟用，站台重開後要重新登入");
// 雙因素（§8.1、§8.3）：TOTP 金鑰與第二步憑證各用自己的 Data Protection purpose
builder.Services.AddSingleton<DynamicModules.Application.Common.Security.ITotpSecretProtector, DataProtectionTotpSecretProtector>();
builder.Services.AddSingleton<DynamicModules.Application.Common.Security.ISecondStepTicketCodec, DataProtectionSecondStepTicketCodec>();
// 產品內備份（備份設計 §7.4、§13.2）：Backup:Root 沒設就不備份；和資料庫、附件、金鑰、wwwroot 重疊的位置不收（記 Warning、新鮮度判嚴重）
var (backupSettings, backupWarning) = DynamicModules.Application.Dynamic.OpsService.Backup.BackupSettings.Create(builder.Configuration["Backup:Root"],
    builder.Environment.ContentRootPath, [databaseDirectory, attachmentSettings.Root, keysDirectory, Path.Combine(builder.Environment.ContentRootPath, "wwwroot")]);
if (backupWarning is not null) startupWarnings.Add(backupWarning);
// PostgreSQL 的備份要 pg_dump／pg_restore：啟動時先找一次，找不到記 Warning（不擋啟動），不必等第一份失敗才知道（bk1b 審查 B1）
if (databaseProvider == DatabaseProvider.Postgres && backupSettings.Configured && !backupSettings.Invalid
    && PostgresDatabaseBackup.ToolsProblem(new PgDumpSettings(builder.Configuration[PgDumpSettings.Key])) is { } pgDumpProblem)
    startupWarnings.Add(pgDumpProblem);
backupSettings = backupSettings with
{
    AttachmentsRoot = Path.GetFullPath(attachmentSettings.Root, builder.Environment.ContentRootPath),
    DatabaseDirectory = databaseDirectory is null ? null : Path.GetFullPath(databaseDirectory, builder.Environment.ContentRootPath),
};
builder.Services.AddSingleton(backupSettings);
if (backupSettings.Root is { } backupRoot)
{
    builder.Services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.Backup.IBackupStore>(sp =>
        new LocalDirectoryBackupStore(backupRoot, sp.GetRequiredService<ILogger<LocalDirectoryBackupStore>>()));
    // 雲端版的備份應放在隔離儲存（T13）；bk1 只有本機目錄，只提示、不擋
    if (builder.Configuration["Ops:Edition"] == "Cloud" && !backupSettings.Invalid)
        startupWarnings.Add("Ops:Edition=Cloud 但備份位置是本機目錄（不是隔離儲存）；雲端隔離儲存上線前，O4–O6 不得開工");
}
builder.Services.AddSingleton<DynamicModules.Application.Dynamic.OpsService.Backup.BackupWorker>();
builder.Services.AddHostedService<BackupHostedService>();
// storage.diskFree 要量的本機目錄：資料庫檔目錄、附件根目錄、金鑰目錄、備份位置（PostgreSQL 沒有資料庫檔目錄；§17.2.4、備份設計 §20 第 11 點）
builder.Services.AddSingleton(new DynamicModules.Application.Dynamic.OpsService.Health.OpsStoragePaths(
    new[] { databaseDirectory, attachmentSettings.Root, keysDirectory, backupSettings.Invalid ? null : backupSettings.Root }
        .OfType<string>().Distinct(StringComparer.Ordinal).ToList()));
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // 須在全域 JsonStringEnumConverter 之前：options.Converters 優先於型別 attribute，先到先贏
    o.JsonSerializerOptions.Converters.Add(new LowercaseEnumConverter<FieldType>());
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddExceptionHandler<DynamicModules.WebAPI.Errors.ConcurrencyExceptionHandler>();
builder.Services.AddExceptionHandler<DynamicModules.WebAPI.Errors.FrozenFieldExceptionHandler>();
builder.Services.AddExceptionHandler<DynamicModules.WebAPI.Errors.DatabaseBusyExceptionHandler>();
builder.Services.AddExceptionHandler<DynamicModules.WebAPI.Errors.DatabaseUnwritableExceptionHandler>();
// 最後一道：必須註冊在其他 handler 之後（依註冊順序執行）
builder.Services.AddExceptionHandler<DynamicModules.WebAPI.Errors.UnhandledExceptionHandler>();
// 所有 ProblemDetails 的 traceId 與 X-Request-Id 一致（框架預設用 Activity.Id，格式不同）
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
    c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier);
// 預設不採用外來的 traceparent（匿名使用者不該能指定 request id）；內部串接才開
if (!builder.Configuration.GetValue<bool>("Ops:TrustIncomingTraceParent"))
    builder.Services.AddSingleton<System.Diagnostics.DistributedContextPropagator>(new IgnoreIncomingTraceContext());
builder.Services.AddHealthChecks().AddDbContextCheck<DynamicDbContext>("database", tags: ["ready"]);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
foreach (var warning in attachmentWarnings.Concat(startupWarnings)) app.Logger.LogWarning("{Warning}", warning);

// 內建範本有錯就拒絕啟動（spec §13.1）：在碰資料庫之前先解析 singleton
app.Services.GetRequiredService<TemplateCatalog>();
app.Services.GetRequiredService<DynamicModules.Application.Dynamic.AppService.AppCatalog>();
// 內建語系資源不齊就拒絕啟動（語系包設計 §8.1）：在 --generate-demo-data 之前，Dockerfile.demo 建映像時就會跑到
var localeProblems = BuiltinLocaleResources.Verify(BuiltinLocaleResources.Load());
if (localeProblems.Count > 0)
{
    foreach (var problem in localeProblems) app.Logger.LogCritical("內建語系資源不齊：{Problem}", problem);
    throw new InvalidOperationException("內建語系資源不齊：" + string.Join("；", localeProblems));
}
await InitializeDatabaseAsync(app, databaseDirectory);
// 建置 Demo 映像時用（Dockerfile.demo）：產生完 Demo 資料就結束，失敗回非 0 讓建置失敗
if (args.Contains("--generate-demo-data"))
{
    var runner = app.Services.GetRequiredService<IDemoDataRunner>();
    runner.TryStart();
    while (runner.Current.State == DemoRunState.Running) await Task.Delay(200);
    if (runner.Current.State == DemoRunState.Succeeded) return 0;
    app.Logger.LogError("Demo 資料產生失敗：{State} {Message}", runner.Current.State, runner.Current.Message);
    return 1;
}
// Demo 站每次一般啟動用目前的金鑰把 mfa01 的固定 TOTP 金鑰加密寫回（§13.3）：排在 --generate-demo-data 之後，建置那次不寫
if (app.Services.GetRequiredService<AuthSettings>().DemoLoginEnabled)
{
    using var demoScope = app.Services.CreateScope();
    await new MediatR.Mediator(demoScope.ServiceProvider).Send(
        new DynamicModules.Application.Dynamic.AccountService.TwoFactor.EnsureDemoMfaSecretRequest());
}
// Demo 站台（Render）啟動後在背景產生 Demo 資料；結果只記 log，不擋啟動也不擋 /api/health
if (app.Configuration.GetValue<bool>("Demo:SeedOnStartup"))
    app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<IDemoDataRunner>().TryStart(startup: true));

if (app.Services.GetRequiredService<AuthSettings>().DemoLoginEnabled)
    app.Logger.LogWarning("Demo:LoginEnabled 已開啟：任何人都能不輸入密碼以示範帳號或 admin 登入，只能用在公開的 Demo 站");

// 第一個：例外處理之前就定好 request id
app.UseMiddleware<RequestContextMiddleware>();
// 緊接在 RequestContext 之後：重載的 log 才帶 requestId
app.UseMiddleware<SystemPolicyRefreshMiddleware>();
if (trustForwardedHeaders) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseMiddleware<SessionMiddleware>();
// /mcp 只收 Bearer 權杖：在 SessionMiddleware 之後換成權杖身分，RequestScopeMiddleware 的 userId 才是權杖擁有者
app.UseFormwrightMcpGate();
// 在 SessionMiddleware 之後：cookie 失效降成匿名時，log scope 的 userId 才不會是舊帳號
app.UseMiddleware<RequestScopeMiddleware>();
app.UseMiddleware<IdempotencyKeyMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapOpsEndpoints();
// 在 wwwroot 區塊之前：GET /mcp 回 405，不掉進 SPA 的 fallback（B19）
app.MapFormwrightMcp();

// 部署時前端建置結果放在 wwwroot（Dockerfile），由同一個站台提供；本機開發沒有 wwwroot，前端走 quasar dev。
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    // 不存在的 API 照樣回 404，其餘路徑交給前端的 history 路由
    app.Map("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
}

app.Run();
return 0;

static async Task InitializeDatabaseAsync(WebApplication app, string? databaseDirectory)
{
    if (databaseDirectory is not null) Directory.CreateDirectory(databaseDirectory);

    // 多個實例同時啟動時整段初始化依序執行（PostgreSQL 用 advisory lock；SQLite 不做事，設計 §5.8）
    await using var startupLock = await app.Services.GetRequiredService<IDatabaseStartupLock>().AcquireAsync(CancellationToken.None);
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
    await db.Database.MigrateAsync();
    // provider 專屬調校（SQLite 切 WAL，#20）；放在其他初始化步驟之前
    await app.Services.GetRequiredService<IDatabaseStartupTuning>().ApplyAsync(db, CancellationToken.None);
    // 內建語系同步成 Dynamic_Locale 的 builtin 列；完成後才載入語系快照與政策快照（語系包設計 §4.1）
    await BuiltinLocaleSync.EnsureAsync(db, app.Services.GetRequiredService<TimeProvider>(), CancellationToken.None);
    await app.Services.GetRequiredService<LocaleRegistry>().EnsureFreshAsync(CancellationToken.None);
    // 全系統政策快照（營運時區等）在索引回填與 Demo 資料之前載入；讀不到時沿用部署出廠值，不擋啟動
    await app.Services.GetRequiredService<CachedSystemPolicy>().EnsureFreshAsync(CancellationToken.None);
    await SeedData.EnsureSeededAsync(db);
    var systemPolicy = app.Services.GetRequiredService<CachedSystemPolicy>();
    var passwordEffects = scope.ServiceProvider.GetRequiredService<DynamicModules.Application.Dynamic.AccountService.PasswordReset.PasswordChangeEffects>();
    await AdminBootstrap.EnsureAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordService>(), app.Configuration,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminBootstrap)),
        int.Parse(systemPolicy.Get(DynamicModules.Domain.Policies.PolicyCatalog.AuthPasswordMinLength), System.Globalization.CultureInfo.InvariantCulture),
        (admin, ct) => passwordEffects.ApplyAsync(admin, systemPolicy.DefaultLocale, ct),
        (admin, ct) => DynamicModules.Application.Dynamic.AccountService.TwoFactor.TwoFactorClear.ApplyAsync(admin,
            scope.ServiceProvider.GetRequiredService<DynamicModules.Domain.Repositories.Dynamic.ITwoFactorRepository>(), ct));
    // 救援設定沒移除、金鑰不落地或可能遺失：開事件提醒（帳號安全設計 §8.7、§8.11）。啟動步驟直接建 Mediator（照樣走管線與交易），
    // 不經可被替換的 IMediator 註冊（測試替身以呼叫次數決定丟例外，不該算到啟動步驟）
    await new MediatR.Mediator(scope.ServiceProvider).Send(
        new DynamicModules.Application.Dynamic.OpsService.Incidents.StartupSecurityCheckRequest(
            !string.IsNullOrEmpty(app.Configuration["Auth:ResetAdminPassword"])));
    // 升級前的索引補上精確換算與截斷標記；版本達標就是空操作。完成前報表走記憶體路徑，結果仍正確
    if (await scope.ServiceProvider.GetRequiredService<IndexBackfillJob>().RunAsync(CancellationToken.None))
        app.Logger.LogInformation("索引回填完成（版本 {Version}）", RecordIndexer.BackfillVersion);
    // 既有簽核流程的目前版本是關卡格式的轉成流程圖（流程圖設計 §13 第 4 點）；已全部是流程圖時不寫任何東西。
    // 單條流程失敗已在裡面記 Warning、繼續；連列出流程都失敗時也只記 Warning、不擋啟動，下次啟動再試
    try
    {
        await scope.ServiceProvider.GetRequiredService<DynamicModules.Application.Dynamic.ApprovalService.ApprovalGraphBatchConversion>().RunAsync(CancellationToken.None);
    }
    catch (Exception e)
    {
        app.Logger.LogWarning(e, "簽核流程批次轉成流程圖沒有跑完，下次啟動再試");
    }
    // 共用主檔完整性檢查（主檔狀態符合使用者閉包、主檔模組的權限列等於重算結果）：不一致只記 Warning，不擋啟動、不自動修
    try
    {
        var problems = await scope.ServiceProvider.GetRequiredService<MasterLifecycle>().CheckAsync(CancellationToken.None);
        foreach (var problem in problems) app.Logger.LogWarning("共用主檔完整性檢查：{Problem}", problem);
    }
    catch (Exception e)
    {
        app.Logger.LogWarning(e, "共用主檔完整性檢查沒有跑完");
    }
}

public partial class Program { }
