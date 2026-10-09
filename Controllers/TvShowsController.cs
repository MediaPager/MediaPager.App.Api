using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

// TMDB-backed TV show browsing. Endpoints mirror MoviesController (lowercase route, no
// api/ prefix) with the same API-key/error handling; playback is not wired up yet, so
// this is browse + details (seasons & episodes) for now.
[ApiController]
[Route("tv-shows")]
public sealed class TvShowsController(IHttpClientFactory httpClientFactory, IPluginSettingsStore pluginSettings) : ControllerBase
{
    // GET /tv-shows?q=&page=1 — tv/popular when no search, search/tv when searching.
    [HttpGet]
    public async Task<IActionResult> ListTvShows([FromQuery(Name = "q")] string? q, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var searchQuery = (q ?? "").Trim();
        var currentPage = page > 0 ? page : 1;
        var endpoint = string.IsNullOrEmpty(searchQuery) ? "tv/popular" : "search/tv";

        var queryParams = new List<string>
        {
            $"api_key={Uri.EscapeDataString(apiKey)}",
            "language=en-US",
            $"page={currentPage}",
        };
        if (!string.IsNullOrEmpty(searchQuery))
            queryParams.Add($"query={Uri.EscapeDataString(searchQuery)}");

        var url = $"https://api.themoviedb.org/3/{endpoint}?{string.Join("&", queryParams)}";

        TmdbResponse? data;
        var body = "";
        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.GetAsync(url, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return TmdbHelpers.TmdbFailureResult((int)response.StatusCode, response.ReasonPhrase, body);

            data = JsonSerializer.Deserialize<TmdbResponse>(body);
        }
        catch (JsonException)
        {
            return TmdbHelpers.TmdbNotJsonResult(body);
        }
        catch (Exception ex)
        {
            return Problem($"TMDB request failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }

        if (data?.Results is null)
            return TmdbHelpers.TmdbNotJsonResult(body);

        var imageBase = await TmdbHelpers.GetImageBaseAsync(pluginSettings, cancellationToken);
        var results = data.Results
            .Select(m => new TvShowSummary(
                Id: m.Id,
                Year: TmdbHelpers.Year(m.FirstAirDate ?? m.ReleaseDate),
                Title: m.Name ?? m.Title ?? "-",
                VoteAverage: m.VoteAverage,
                Overview: m.Overview ?? "",
                PosterUrl: TmdbHelpers.ImageUrl(imageBase, m.PosterPath),
                BackdropUrl: TmdbHelpers.ImageUrl(imageBase, m.BackdropPath),
                FirstAirDate: m.FirstAirDate ?? m.ReleaseDate))
            .ToList();

        return Ok(new TvPagedResponse(data.Page, data.TotalPages, data.TotalResults, results));
    }

    // GET /tv-shows/{id}/details — TMDB tv details + credits + similar + season list.
    [HttpGet("{id:long}/details")]
    public async Task<IActionResult> GetTvShowDetails(long id, CancellationToken cancellationToken)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var url = $"https://api.themoviedb.org/3/tv/{id}?api_key={Uri.EscapeDataString(apiKey)}&language=en-US&append_to_response=credits,similar";

        TmdbTvDetails? data;
        var body = "";
        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.GetAsync(url, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return TmdbHelpers.TmdbFailureResult((int)response.StatusCode, response.ReasonPhrase, body);

            data = JsonSerializer.Deserialize<TmdbTvDetails>(body);
        }
        catch (JsonException)
        {
            return TmdbHelpers.TmdbNotJsonResult(body);
        }
        catch (Exception ex)
        {
            return Problem($"TMDB request failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }

        if (data is null)
            return Problem("TMDB returned an unexpected response.", statusCode: StatusCodes.Status502BadGateway);

        var imageBase = await TmdbHelpers.GetImageBaseAsync(pluginSettings, cancellationToken);

        var cast = (data.Credits?.Cast ?? [])
            .OrderBy(c => c.Order)
            .Take(12)
            .Select(c => new CastMember(c.Name ?? "-", c.Character, TmdbHelpers.ImageUrl(imageBase, c.ProfilePath)))
            .ToList();

        var creator = data.CreatedBy?.FirstOrDefault()?.Name;

        var related = (data.Similar?.Results ?? [])
            .Take(12)
            .Select(m => new TvShowSummary(
                Id: m.Id,
                Year: TmdbHelpers.Year(m.FirstAirDate ?? m.ReleaseDate),
                Title: m.Name ?? m.Title ?? "-",
                VoteAverage: m.VoteAverage,
                Overview: m.Overview ?? "",
                PosterUrl: TmdbHelpers.ImageUrl(imageBase, m.PosterPath),
                BackdropUrl: TmdbHelpers.ImageUrl(imageBase, m.BackdropPath),
                FirstAirDate: m.FirstAirDate ?? m.ReleaseDate))
            .ToList();

        var seasons = (data.Seasons ?? [])
            .Where(s => s.SeasonNumber > 0 && s.EpisodeCount > 0)
            .OrderBy(s => s.SeasonNumber)
            .Select(s => new TvSeasonSummary(
                SeasonNumber: s.SeasonNumber,
                Name: s.Name,
                Overview: s.Overview ?? "",
                EpisodeCount: s.EpisodeCount,
                AirDate: s.AirDate,
                PosterUrl: TmdbHelpers.ImageUrl(imageBase, s.PosterPath)))
            .ToList();

        return Ok(new TvShowDetails(
            Id: data.Id,
            Year: TmdbHelpers.Year(data.FirstAirDate),
            Title: data.Name ?? "-",
            VoteAverage: data.VoteAverage,
            Overview: data.Overview ?? "",
            PosterUrl: TmdbHelpers.ImageUrl(imageBase, data.PosterPath),
            BackdropUrl: TmdbHelpers.ImageUrl(imageBase, data.BackdropPath),
            FirstAirDate: data.FirstAirDate,
            LastAirDate: data.LastAirDate,
            NumberOfSeasons: data.NumberOfSeasons,
            Status: data.Status,
            Genres: (data.Genres ?? []).Select(g => g.Name ?? "-").ToList(),
            Creator: creator,
            Cast: cast,
            Related: related,
            Seasons: seasons));
    }

    // GET /tv-shows/{id}/season/{seasonNumber} — TMDB tv season with its episode list.
    [HttpGet("{id:long}/season/{seasonNumber:int}")]
    public async Task<IActionResult> GetSeason(long id, int seasonNumber, CancellationToken cancellationToken)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var url = $"https://api.themoviedb.org/3/tv/{id}/season/{seasonNumber}?api_key={Uri.EscapeDataString(apiKey)}&language=en-US";

        TmdbSeasonDetails? data;
        var body = "";
        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.GetAsync(url, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return TmdbHelpers.TmdbFailureResult((int)response.StatusCode, response.ReasonPhrase, body);

            data = JsonSerializer.Deserialize<TmdbSeasonDetails>(body);
        }
        catch (JsonException)
        {
            return TmdbHelpers.TmdbNotJsonResult(body);
        }
        catch (Exception ex)
        {
            return Problem($"TMDB request failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }

        if (data is null)
            return Problem("TMDB returned an unexpected response.", statusCode: StatusCodes.Status502BadGateway);

        var imageBase = await TmdbHelpers.GetImageBaseAsync(pluginSettings, cancellationToken);
        var episodes = (data.Episodes ?? [])
            .OrderBy(e => e.EpisodeNumber)
            .Select(e => new TvEpisodeSummary(
                EpisodeNumber: e.EpisodeNumber,
                Title: e.Name ?? $"- {e.EpisodeNumber}",
                Overview: e.Overview ?? "",
                AirDate: e.AirDate,
                RuntimeMinutes: e.Runtime,
                VoteAverage: e.VoteAverage,
                StillUrl: TmdbHelpers.ImageUrl(imageBase, e.StillPath)))
            .ToList();

        return Ok(new TvSeasonDetails(
            Name: data.Name,
            Overview: data.Overview ?? "",
            AirDate: data.AirDate,
            SeasonNumber: data.SeasonNumber ?? seasonNumber,
            Episodes: episodes));
    }
}