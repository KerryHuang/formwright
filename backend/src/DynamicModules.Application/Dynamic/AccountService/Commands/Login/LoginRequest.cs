using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.Login;

/// <summary>
/// 不是 ITransactionalRequest：舊鎖定到期的歸零在失敗時也要寫入，由 handler 自己提交。
/// <see cref="DeviceToken"/> 是「記住這台裝置」cookie（帳號安全設計 §8.5），有效時已啟用雙因素的帳號不用第二步。
/// </summary>
public record LoginRequest(string UserId, string Password, string? ClientIp = null, string? DeviceToken = null) : IRequest<ServiceResult>;
