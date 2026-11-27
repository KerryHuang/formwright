using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DynamicModules.WebAPI.Mcp;

/// <summary>
/// SDK 的 <c>tools/list</c>、<c>tools/call</c> 接點（mcp1a 計畫判斷 11）：只轉接，工具定義與執行全在 Application 的 <see cref="McpToolInvoker"/>。
/// 範圍與通道從 <see cref="IRequestChannel"/>（讀閘門換上的 claim）取，不靠 SDK 的 scope。
/// </summary>
public static class McpSdkAdapter
{
    /// <summary>不可信的任務標籤（先保留給 mcpO1），只進稽核摘要。</summary>
    public const string JobHeader = "X-Formwright-Job";

    public static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken ct)
    {
        var services = context.Services!;
        var scopes = services.GetRequiredService<IRequestChannel>().Scopes;
        var tools = services.GetRequiredService<McpToolInvoker>().ListFor(scopes).Select(t => new Tool
        {
            Name = t.Descriptor.Name,
            Description = t.Description,
            InputSchema = JsonDocument.Parse(t.InputSchemaJson).RootElement.Clone(),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = t.Descriptor.Risk == McpRisk.Read,
                // 一般寫入不具破壞性、冪等鍵必填所以一律冪等（mcp2a 判斷 5、10）；高風險標破壞性
                DestructiveHint = t.Descriptor.Risk == McpRisk.HighRisk,
                IdempotentHint = t.Descriptor.Risk == McpRisk.Write ? true : null,
                OpenWorldHint = false,
            },
        }).ToList();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = tools, TimeToLive = TimeSpan.FromSeconds(McpLimits.ListToolsTtlSeconds), CacheScope = CacheScope.Private,
        });
    }

    public static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var services = context.Services!;
        var name = context.Params?.Name ?? "";
        JsonElement? args = context.Params?.Arguments is { } dict ? JsonSerializer.SerializeToElement(dict) : null;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var server = context.Server;
        var client = server?.ClientInfo;
        // elicitation 選路（mcp2b，設計 §6.3.1 第 1 點）：MRTR 與 form elicitation 兩項都要；缺一走確認碼。
        // 丟 InputRequiredException 前一定要確認 MRTR，否則只會變成 JSON-RPC internal error
        var elicitation = server is { IsMrtrSupported: true } && server.ClientCapabilities?.Elicitation?.Form is not null;
        var call = new McpCallContext(client?.Name, client?.Version, http?.Request.Headers[JobHeader].FirstOrDefault(),
            elicitation, context.Params?.RequestState, AnswerOf(context.Params?.InputResponses));
        var result = await services.GetRequiredService<McpToolInvoker>().InvokeAsync(name, args, call, ct);
        if (result.InputRequired is { } input)
            throw new InputRequiredException(new Dictionary<string, InputRequest>
            {
                [McpInputRequired.Key] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Message = input.Message,
                    // 只收一個布林（§6.3.1 第 2 點）：規範規定敏感資訊不可走 form，這裡也不收其他輸入
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        {
                            [McpInputRequired.Key] = new ElicitRequestParams.BooleanSchema { Title = input.ConfirmTitle },
                        },
                        Required = [McpInputRequired.Key],
                    },
                }),
            }, input.RequestState);
        return new CallToolResult
        {
            IsError = result.IsError,
            Content = [new TextContentBlock { Text = result.Text }],
            StructuredContent = result.Structured,
        };
    }

    /// <summary>重送時對話框的回答（<see cref="McpInputRequired.Key"/>）；沒有或讀不出來回 null（執行器當成取消）。</summary>
    private static McpElicitationAnswer? AnswerOf(IDictionary<string, InputResponse>? responses)
    {
        if (responses is null || !responses.TryGetValue(McpInputRequired.Key, out var response)) return null;
        try
        {
            if (response.Deserialize(InputResponse.ElicitResultJsonTypeInfo) is not { } elicit) return null;
            bool? confirmed = elicit.Content is { } content && content.TryGetValue(McpInputRequired.Key, out var v)
                ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null;
            return new McpElicitationAnswer(elicit.Action, confirmed);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
