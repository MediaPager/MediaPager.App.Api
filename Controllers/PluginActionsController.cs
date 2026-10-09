using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
public sealed class PluginActionsController(PluginRegistry registry) : ControllerBase
{
    [HttpPost("/plugins/{key}/actions/{actionId}")]
    public async Task<IActionResult> Dispatch(
        string key,
        string actionId,
        [FromBody] PluginActionRequest? request,
        CancellationToken cancellationToken)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Descriptor.Id.Split('.')[^1], key, StringComparison.OrdinalIgnoreCase));
        if (plugin is not IPluginActions actions)
            return NotFound(Problem($"No action provider '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        request ??= new PluginActionRequest();
        var declaredActions = await actions.GetActionsAsync(cancellationToken);
        var matchingActions = declaredActions.Where(candidate =>
            string.Equals(candidate.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        var descriptor = request.Kind is { } requestedKind
            ? matchingActions.FirstOrDefault(candidate => candidate.Kinds is null || candidate.Kinds.Contains(requestedKind))
            : matchingActions.FirstOrDefault();
        if (descriptor is null)
            return NotFound(Problem($"Action '{actionId}' is not declared by '{plugin.Descriptor.Name}'.", statusCode: StatusCodes.Status404NotFound));
        if (!descriptor.Enabled)
            return Conflict(Problem(descriptor.AvailabilityMessage ?? $"Action '{descriptor.Label}' is not configured.",
                statusCode: StatusCodes.Status409Conflict));
        if (descriptor.Click != PluginActionClick.Dispatch)
            return BadRequest(Problem("This action opens plugin UI and cannot be dispatched.", statusCode: StatusCodes.Status400BadRequest));

        if (request.Kind is { } kind && descriptor.Kinds is { } kinds && !kinds.Contains(kind))
            return BadRequest(Problem($"Action '{descriptor.Label}' does not support {kind} items.",
                statusCode: StatusCodes.Status400BadRequest));
        var context = new PluginActionContext(
            descriptor.ActionId,
            descriptor.Surface,
            request.CatalogItemId,
            request.CatalogTypeId,
            request.Kind,
            request.SourceKey,
            request.ExternalId,
            request.Title,
            request.Season,
            request.Episode,
            request.ImageUrl,
            request.BackdropUrl,
            request.Overview,
            request.Year);
        try
        {
            await actions.InvokeActionAsync(context, cancellationToken);
            return Ok(new { message = "Action dispatched." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception exception)
        {
            return Problem($"Action '{descriptor.Label}' failed: {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}

public sealed record PluginActionRequest(
    int? CatalogItemId = null,
    int? CatalogTypeId = null,
    MediaKind? Kind = null,
    string? SourceKey = null,
    string? ExternalId = null,
    string? Title = null,
    int? Season = null,
    int? Episode = null,
    string? ImageUrl = null,
    string? BackdropUrl = null,
    string? Overview = null,
    int? Year = null);
