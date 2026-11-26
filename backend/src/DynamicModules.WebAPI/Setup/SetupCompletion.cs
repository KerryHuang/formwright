using System.Text.Json.Nodes;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Domain.Policies;

namespace DynamicModules.WebAPI.Setup;

public sealed record SetupCompleteBody(string? Provider, string? ConnectionString, string? AdminPassword, string? SiteName, string? BackupRoot);

/// <summary>欄位驗證錯誤：只有代碼與參數，不帶句子（設定模式沒有 IMessageCatalog）。</summary>
public sealed record SetupFieldError(string Code, IReadOnlyDictionary<string, object>? Params = null);

/// <summary>
/// 結束程序的縫：回應送完後以非 0 結束碼直接結束。不用 <c>StopApplication</c>，因為 Windows SCM 對「回報已停止」的正常停止
/// 不跑失敗復原，服務會停在已停止；程序以非 0 結束碼結束才算失敗而被重啟（Review Focus 1）。測試換成記錄碼的替身。
/// </summary>
public sealed class SetupRestart(Action<int> exit)
{
    public void Exit(int code) => exit(code);

    public static SetupRestart Process => new(code => { Environment.ExitCode = code; Environment.Exit(code); });
}

/// <summary>精靈完成：驗欄位、重跑檢查、依序寫入（當機時能重來）、改設定檔、作廢設定碼。</summary>
public static class SetupCompletion
{
    /// <summary>
    /// 密碼最短長度：設定模式沒有資料庫政策，取一般模式沒有資料庫覆寫時的生效值。
    /// <c>PolicyEnvironmentDefaults</c> 只涵蓋附件政策（<c>AttachmentSettingsFactory</c>），不含 <c>auth.passwordMinLength</c>，所以就是 <see cref="PolicyCatalog"/> 的出廠值。
    /// </summary>
    public static int MinPasswordLength => int.Parse(PolicyCatalog.AuthPasswordMinLength.Factory, System.Globalization.CultureInfo.InvariantCulture);

    public static Dictionary<string, SetupFieldError[]> Validate(SetupCompleteBody body, out string siteName)
    {
        var errors = new Dictionary<string, SetupFieldError[]>();
        var min = MinPasswordLength;
        if ((body.AdminPassword?.Length ?? 0) < min)
            errors["adminPassword"] = [new("passwordTooShort", new Dictionary<string, object> { ["min"] = min })];

        siteName = body.SiteName?.Trim() ?? "";
        if (siteName.Length == 0) errors["siteName"] = [new("siteNameRequired")];
        else if (SiteName.Problem(siteName) is { } problem)
            errors["siteName"] = [problem == Application.Common.MessageText.TooLong
                ? new("siteNameTooLong", new Dictionary<string, object> { ["max"] = SiteName.MaxLength })
                : new SetupFieldError("siteNameControlChars")];
        return errors;
    }

    public static async Task<IResult> RunAsync(
        HttpContext http, SetupCompleteBody? body, IConfiguration configuration, SetupChecks checks, IPasswordService passwords, SetupRestart restart,
        string dataDirectory, CancellationToken ct)
    {
        body ??= new SetupCompleteBody(null, null, null, null, null);
        var errors = Validate(body, out var siteName);
        if (errors.Count > 0)
            return Results.Json(new { code = "invalid", errors }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var draft = new SetupDraft(body.Provider, body.ConnectionString, body.BackupRoot);
        var items = await checks.RunAsync(draft, ct);
        if (items.Any(i => !i.Ok))
            return Results.Json(new { code = "checksFailed", items }, statusCode: StatusCodes.Status422UnprocessableEntity);

        var configFile = InstallConfig.ConfigFile(configuration)!;
        var config = InstallConfigWriter.Read(configFile);
        var postgres = string.Equals(body.Provider, "postgres", StringComparison.OrdinalIgnoreCase);

        // 寫入順序：先 payload、再設定檔、最後作廢設定碼；任一步當機，設定檔還在 Pending，重跑一次就好
        SetupPayload.Write(dataDirectory, new SetupPayload(passwords.Hash(body.AdminPassword!), siteName));

        if (body.BackupRoot is { Length: > 0 } backupRoot) Section(config, "Backup")["Root"] = backupRoot;
        if (postgres)
        {
            Section(config, "Database")["Provider"] = "Postgres";
            Section(config, "ConnectionStrings")["Dynamic"] = body.ConnectionString;
        }
        config.Remove("Setup");
        InstallConfigWriter.WriteAtomic(configFile, config);

        SetupCode.Delete(dataDirectory);

        // 回應送完才結束，客戶端收得到 200
        http.Response.OnCompleted(() =>
        {
            restart.Exit(SetupHost.RestartExitCode);
            return Task.CompletedTask;
        });
        return Results.Json(new { restarting = true });
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        root[name] = created;
        return created;
    }
}
