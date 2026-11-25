using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Controllers;

/// <summary>Read-only, paged data for a plugin's own packaged UI. The plugin
/// namespace is resolved from the loaded registry; private collections are never exposed.</summary>
[ApiController]
[Authorize]
[Route("plugins/{key}/data")]
public sealed class PluginDataController(
    PluginRegistry registry,
    IPluginDataRepository dataRepository,
    PluginContentAccessService contentAccess) : ControllerBase
{
    private const int MaximumUiPageSize = 500;
    private const int MaximumUiPageCharacters = 1_500_000;

    [HttpGet("{collection}")]
    public async Task<IActionResult> List(
        string key,
        string collection,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 250,
        CancellationToken cancellationToken = default)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Descriptor.Id.Split('.')[^1], key, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || !HasPackagedUi(plugin) ||
            !collection.StartsWith("public.", StringComparison.OrdinalIgnoreCase) ||
            !TryGetRequiredScope(collection, out var requiredScope))
            return NotFound(new { error = "Plugin data is unavailable." });
        if (requiredScope is not null &&
            !await contentAccess.CanAccessAsync(plugin, requiredScope, User, cancellationToken))
            return NotFound(new { error = "Plugin data is unavailable." });

        if (limit is < 1 or > MaximumUiPageSize || offset < 0)
            return BadRequest(new { error = $"Use a limit from 1 to {MaximumUiPageSize} and a non-negative offset." });

        try
        {
            var page = await dataRepository.ListAsync(plugin.Descriptor.Id, collection, offset, limit, cancellationToken);
            var characters = page.Items.Sum(item => item.Key.Length + item.Json.Length);
            if (characters > MaximumUiPageCharacters)
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = "Reduce the page size to read this plugin data collection." });
            return Ok(page);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("{collection}/{documentKey}")]
    public async Task<IActionResult> Get(
        string key,
        string collection,
        string documentKey,
        CancellationToken cancellationToken = default)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Descriptor.Id.Split('.')[^1], key, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || !HasPackagedUi(plugin) ||
            !collection.StartsWith("public.", StringComparison.OrdinalIgnoreCase) ||
            !TryGetRequiredScope(collection, out var requiredScope))
            return NotFound(new { error = "Plugin data is unavailable." });
        if (requiredScope is not null &&
            !await contentAccess.CanAccessAsync(plugin, requiredScope, User, cancellationToken))
            return NotFound(new { error = "Plugin data is unavailable." });

        try
        {
            var document = await dataRepository.GetAsync(plugin.Descriptor.Id, collection, documentKey, cancellationToken);
            return document is null ? NotFound() : Ok(document);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private static bool HasPackagedUi(IMediaPagerPlugin plugin)
    {
        if (plugin is IPluginNavigation navigation && navigation.MainNav.Count > 0)
            return true;

        return plugin is IStreamProviderPlugin streamProvider &&
            streamProvider.Sources.Any(source => source.CustomUi && !string.IsNullOrWhiteSpace(source.UiUrl));
    }

    private static bool TryGetRequiredScope(string collection, out string? localScopeId)
    {
        const string prefix = "public.scoped.";
        localScopeId = null;
        if (!collection.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        var remainder = collection[prefix.Length..];
        var separator = remainder.IndexOf('.');
        if (separator is <= 0 || separator >= remainder.Length - 1) return false;
        var candidate = remainder[..separator];
        if (candidate.Length > 64 || candidate.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            return false;

        localScopeId = candidate;
        return true;
    }
}
