using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin;

/// <summary>不是 ITransactionalRequest：平常完全不寫資料庫，只有清除強制改密碼旗標時才自行提交。<see cref="ClientIp"/> 給來源節流用；<see cref="DeviceToken"/> 是記住這台裝置的 cookie（§8.5）。</summary>
public record DemoLoginRequest(string UserId, string? ClientIp = null, string? DeviceToken = null) : IRequest<ServiceResult>;
