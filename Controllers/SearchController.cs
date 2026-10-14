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
    public async Task<IActionResult> UnifiedSearch(
        [FromQuery] string? q,
        [FromQuery] int limit = 5,
        [FromQuery] string? mediaTypeSlug = null,
        [FromQuery] string? catalogTypeSlug = null,
        CancellationToken cancellationToken = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0)
            return Ok(new List<SearchHit>());

        var resultLimit = Math.Clamp(limit, 1, 10);
        mediaTypeSlug = NormalizeScope(mediaTypeSlug);
        catalogTypeSlug = NormalizeScope(catalogTypeSlug);
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
                    new SearchRequest(query, resultLimit, supportedStreamKinds, mediaTypeSlug, catalogTypeSlug), cancellationToken);
            }
            catch
            {
                return (IReadOnlyList<ProviderSearchHit>)Array.Empty<ProviderSearchHit>();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var providerHits = tasks.SelectMany(task => task.Result)
            .Where(hit => MatchesScope(hit, mediaTypeSlug, catalogTypeSlug))
            .ToList();
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
                CatalogId: hit.CatalogId)
            {
                Metadata = hit.Metadata,
            })
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

    private static bool MatchesScope(ProviderSearchHit hit, string? mediaTypeSlug, string? catalogTypeSlug)
    {
        if (catalogTypeSlug is not null &&
            !string.Equals(hit.Kind.ToCatalogTypeSlug(), catalogTypeSlug, StringComparison.OrdinalIgnoreCase))
            return false;

        if (mediaTypeSlug is null) return true;
        var hitMediaType = hit.Kind switch
        {
            MediaKind.Movie or MediaKind.Tv => "video",
            MediaKind.Music or MediaKind.Podcast or MediaKind.Audiobook => "audio",
            MediaKind.Book => "book",
            MediaKind.Document => "documents",
            _ => "",
        };
        return string.Equals(hitMediaType, mediaTypeSlug, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
