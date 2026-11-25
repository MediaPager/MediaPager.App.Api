using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaPager.App.Api.Services.Agents;

namespace MediaPager.App.Api.Controllers;

/// <summary>Read-only agent inventory plus manual (forced) agent runs for loaded plugins.</summary>
[ApiController]
public sealed class PluginAgentsController(PluginRegistry registry, AgentRuntimeService runtime) : ControllerBase
{
    [HttpGet("/plugins/{key}/agents")]
    [Authorize]
    public async Task<IActionResult> List(string key, CancellationToken cancellationToken)
    {
        if (FindAgentPlugin(key) is not { } plugin)
            return NotFound(Problem($"No agent plugin '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        try
        {
            var agents = await plugin.GetAgentsAsync(cancellationToken);
            return Ok(agents.Select(agent => new
            {
                id = agent.Id,
                name = agent.Name,
                description = agent.Description,
                enabled = agent.Enabled,
                interval = agent.Interval,
                catalogTypeSlugs = agent.CatalogTypeSlugs,
            }).ToList());
        }
        catch (PluginOperationException exception)
        {
            return PluginErrorResponses.ToProblem(exception.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception exception)
        {
            return Problem($"Could not list agents for '{plugin.Descriptor.Name}': {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("/plugins/{key}/agents/{agentId}/run")]
    [Authorize]
    public async Task<IActionResult> Run(string key, string agentId, CancellationToken cancellationToken)
    {
        if (FindAgentPlugin(key) is not { } plugin)
            return NotFound(Problem($"No agent plugin '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        try
        {
            // The runtime invokes IAgentPlugin.RunAsync directly with Force=true and resets
            // the schedule; the shared path also makes a manual run an observable job.
            var result = await runtime.RunManuallyAsync(plugin, agentId, cancellationToken);
            return Ok(new
            {
                agentId = result.AgentId,
                scanned = result.Scanned,
                added = result.Added,
                updated = result.Updated,
                removed = result.Removed,
                skipped = result.Skipped,
                errors = result.Errors,
                summary = result.Summary,
            });
        }
        catch (PluginOperationException exception)
        {
            return PluginErrorResponses.ToProblem(exception.Error);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(exception.Message, statusCode: StatusCodes.Status404NotFound));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem(exception.Message, statusCode: StatusCodes.Status409Conflict));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception exception)
        {
            return Problem($"Agent '{agentId}' failed: {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private IAgentPlugin? FindAgentPlugin(string key) => registry.Plugins
        .FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Descriptor.Id.Split('.')[^1], key, StringComparison.OrdinalIgnoreCase))
        as IAgentPlugin;
}
