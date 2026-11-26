using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskViewService;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>存起來的任務檢視（看板）與分享（看板設計 §6、§12）。設定用 JsonElement 接，交給 handler 驗證回 400。</summary>
[ApiController]
[Route("api/v3/dynamic/task-views")]
public class TaskViewsController(IMediator mediator) : ControllerBase
{
    /// <summary>Name 收多語物件，或字串（＝只有繁中，舊畫面相容，名稱多語設計 §6.1）。</summary>
    public sealed record CreateBody(string? Kind, [property: JsonConverter(typeof(I18nTextOrStringJsonConverter))] I18nText? Name, JsonElement? Config);

    public sealed record UpdateBody([property: JsonConverter(typeof(I18nTextOrStringJsonConverter))] I18nText? Name, JsonElement? Config, string? RowVersion);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string kind = "board") =>
        (await mediator.Send(new GetTaskViewsRequest(kind))).ToActionResult(this);

    [HttpGet("share-targets")]
    public async Task<IActionResult> ShareTargets() =>
        (await mediator.Send(new GetTaskViewShareTargetsRequest())).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(CreateBody body) =>
        (await mediator.Send(new CreateTaskViewRequest(body.Kind ?? "board", body.Name, body.Config))).ToActionResult(this);

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) =>
        (await mediator.Send(new GetTaskViewRequest(id))).ToActionResult(this);

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateBody body) =>
        (await mediator.Send(new UpdateTaskViewRequest(id, body.Name, body.Config, body.RowVersion))).ToActionResult(this);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) =>
        (await mediator.Send(new DeleteTaskViewRequest(id))).ToActionResult(this);

    [HttpPost("{id:long}/copy")]
    public async Task<IActionResult> Copy(long id) =>
        (await mediator.Send(new CopyTaskViewRequest(id))).ToActionResult(this);

    /// <summary>整批取代：[{ kind: user|group|department, id }]。</summary>
    [HttpPut("{id:long}/shares")]
    public async Task<IActionResult> SetShares(long id, List<TaskViewShareInput>? body) =>
        (await mediator.Send(new SetTaskViewSharesRequest(id, body ?? []))).ToActionResult(this);
}
