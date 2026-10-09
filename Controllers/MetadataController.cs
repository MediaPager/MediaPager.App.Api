using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("metadata")]
public sealed class MetadataController(PluginRegistry registry) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] string kind,
        [FromQuery] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0) return Ok(Array.Empty<object>());
        if (query.Length > 200) return BadRequest(new { error = "Search text must be 200 characters or fewer." });
        if (limit is < 1 or > 20) limit = 8;

        MediaKind mediaKind;
        try
        {
            mediaKind = MediaKindExtensions.FromSlug(kind);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new { error = $"Unknown media kind '{kind}'." });
        }

        var providers = registry.ResolveAll<ISearchProviderPlugin>()
            .Where(search => registry.GetPlugin(search.Descriptor.Id) is IMetadataProviderPlugin metadata &&
                             metadata.Handles(mediaKind))
            .ToList();
        var tasks = providers.Select(async provider =>
        {
            try
            {
                var hits = await provider.SearchAsync(new SearchRequest(query, limit), cancellationToken);
                return (Provider: provider, Hits: hits);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (Provider: provider, Hits: (IReadOnlyList<ProviderSearchHit>)Array.Empty<ProviderSearchHit>());
            }
        }).ToList();

        await Task.WhenAll(tasks);
        var results = tasks
            .SelectMany(task => task.Result.Hits
                .Where(hit => hit.Kind == mediaKind)
                .Select(hit => new
                {
                    providerId = task.Result.Provider.Descriptor.Id,
                    kind = hit.Kind.ToSlug(),
                    externalId = hit.ExternalId,
                    title = hit.Title,
                    year = hit.Year,
                    overview = hit.Overview,
                    artworkUrl = hit.ArtworkUrl,
                    voteAverage = hit.VoteAverage,
                }))
            .OrderByDescending(hit => hit.voteAverage ?? 0)
            .Take(limit)
            .ToList();
        return Ok(results);
    }

    [HttpGet("{providerId}/{kind}/{externalId}")]
    public async Task<IActionResult> Enrich(
        string providerId,
        string kind,
        string externalId,
        CancellationToken cancellationToken)
    {
        MediaKind mediaKind;
        try
        {
            mediaKind = MediaKindExtensions.FromSlug(kind);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new { error = $"Unknown media kind '{kind}'." });
        }

        if (registry.GetPlugin(providerId) is not IMetadataProviderPlugin provider || !provider.Handles(mediaKind))
            return NotFound(new { error = $"No metadata provider '{providerId}' handles {kind}." });

        try
        {
            var metadata = await provider.EnrichAsync(externalId, mediaKind, cancellationToken);
            return metadata is null ? NotFound() : Ok(metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Problem($"{provider.Descriptor.Name} metadata lookup failed: {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
