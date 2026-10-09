using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

// Unified header search: one endpoint feeds the type-ahead dropdown across every
// media category. Fuses every registered ISearchProviderPlugin (TMDB, local library,
// ...) in parallel and adapts their hits to the SPA's SearchHit wire shape. One
// provider failing must not sink the whole dropdown.
[ApiController]
[Route("search")]
public sealed class SearchController(PluginRegistry registry) : ControllerBase
{
    // GET /search?q=&limit=5 — top N matches fused across all search providers.
    [HttpGet]
    public async Task<IActionResult> UnifiedSearch([FromQuery] string? q, [FromQuery] int limit = 5, CancellationToken cancellationToken = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0)
            return Ok(new List<SearchHit>());

        var resultLimit = Math.Clamp(limit, 1, 10);
        var supportedStreamKinds = registry.ResolveAll<IStreamProviderPlugin>()
            .Where(provider => provider.Mode == StreamProviderMode.Online)
            .SelectMany(provider => provider.SupportedKinds)
            .Distinct()
            .ToArray();
        var providers = registry.ResolveAll<ISearchProviderPlugin>();
        var tasks = providers.Select(async provider =>
        {
            try
            {
                return await provider.SearchAsync(
                    new SearchRequest(query, resultLimit, supportedStreamKinds), cancellationToken);
            }
            catch
            {
                return (IReadOnlyList<ProviderSearchHit>)Array.Empty<ProviderSearchHit>();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var providerHits = tasks.SelectMany(task => task.Result).ToList();
        var localHits = providerHits
            .Where(hit => hit.CatalogItemId is not null)
            .OrderByDescending(hit => hit.VoteAverage ?? 0)
            .ToList();
        var nonLocalHits = providerHits
            .Where(hit => hit.CatalogItemId is null)
            .Where(hit => !localHits.Any(local => IsSameTitle(local, hit)))
            .OrderByDescending(hit => hit.VoteAverage ?? 0);

        var hits = localHits
            .Concat(nonLocalHits)
            .Take(resultLimit)
            .Select(hit => new SearchHit(
                Kind: hit.Kind.ToSlug(),
                Id: hit.CatalogItemId is int localId
                    ? localId
                    : long.TryParse(hit.ExternalId, out var id) ? id : 0,
                Title: hit.Title,
                Year: hit.Year?.ToString(),
                VoteAverage: hit.VoteAverage ?? 0,
                Overview: hit.Overview ?? "",
                ArtworkUrl: hit.ArtworkUrl,
                SourceKey: hit.SourceKey,
                ExternalId: hit.ExternalId,
                CatalogItemId: hit.CatalogItemId,
                CatalogId: hit.CatalogId))
            .ToList();

        return Ok(hits);
    }

    private static bool IsSameTitle(ProviderSearchHit local, ProviderSearchHit remote)
    {
        if (local.Kind != remote.Kind) return false;
        if (string.Equals(local.ExternalId, remote.ExternalId, StringComparison.OrdinalIgnoreCase))
            return true;

        var localTitle = NormalizeTitle(local.Title);
        var remoteTitle = NormalizeTitle(remote.Title);
        return localTitle.Length > 0 && localTitle == remoteTitle &&
               (local.Year is null || remote.Year is null || local.Year == remote.Year);
    }

    private static string NormalizeTitle(string title) =>
        string.Concat(title.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
