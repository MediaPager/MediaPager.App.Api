using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

// TMDB-backed TV show browsing. Endpoints mirror MoviesController (lowercase route, no
// api/ prefix) with the same API-key/error handling; playback is not wired up yet, so
// this is browse + details (seasons & episodes) for now.
[ApiController]
[Route("tv-shows")]
public sealed class TvShowsController(IHttpClientFactory httpClientFactory, IPluginSettingsRepository pluginSettings) : ControllerBase
{
    // GET /tv-shows — TMDB popular/search results or discover results when filters are supplied.
    [HttpGet]
    public async Task<IActionResult> ListTvShows(
        [FromQuery(Name = "q")] string? q,
        [FromQuery] int? genreId = null,
        [FromQuery] int? year = null,
        [FromQuery] double? minRating = null,
        [FromQuery] int? minVoteCount = null,
        [FromQuery] string? language = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var searchQuery = (q ?? "").Trim();
        var currentPage = page > 0 ? page : 1;
        var allowedSorts = new[]
        {
            "popularity.desc", "popularity.asc", "vote_average.desc", "vote_average.asc",
            "first_air_date.desc", "first_air_date.asc", "name.asc", "name.desc",
        };
        var sort = sortBy is not null && allowedSorts.Contains(sortBy, StringComparer.OrdinalIgnoreCase)
            ? sortBy
            : "popularity.desc";
        if (year is < 1870 or > 2200) return BadRequest(new { error = "Year must be between 1870 and 2200." });
        if (minRating is < 0 or > 10) return BadRequest(new { error = "Minimum rating must be between 0 and 10." });
        if (minVoteCount is < 0 or > 1_000_000) return BadRequest(new { error = "Minimum votes must be between 0 and 1000000." });
        language = NormalizeLanguage(language);
        var hasDiscoverFilters = genreId is not null || year is not null || minRating is not null ||
                                 minVoteCount is not null || language is not null || sortBy is not null;
        var endpoint = !string.IsNullOrEmpty(searchQuery)
            ? "search/tv"
            : hasDiscoverFilters ? "discover/tv" : "tv/popular";

        var queryParams = new List<string>
        {
            $"api_key={Uri.EscapeDataString(apiKey)}",
            "language=en-US",
            $"page={currentPage}",
        };
        if (!string.IsNullOrEmpty(searchQuery))
            queryParams.Add($"query={Uri.EscapeDataString(searchQuery)}");
        if (endpoint == "discover/tv")
        {
            queryParams.Add($"sort_by={Uri.EscapeDataString(sort)}");
            if (genreId is int selectedGenre) queryParams.Add($"with_genres={selectedGenre}");
            if (year is int firstAirYear) queryParams.Add($"first_air_date_year={firstAirYear}");
            if (minRating is double rating)
                queryParams.Add($"vote_average.gte={rating.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (minVoteCount is int voteCount) queryParams.Add($"vote_count.gte={voteCount}");
            if (language is not null) queryParams.Add($"with_original_language={Uri.EscapeDataString(language)}");
        }

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
        IEnumerable<UpstreamMovie> filteredResults = data.Results
            .Where(show => genreId is null || show.GenreIds?.Contains(genreId.Value) == true)
            .Where(show => year is null || TmdbHelpers.Year(show.FirstAirDate ?? show.ReleaseDate) == year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Where(show => minRating is null || show.VoteAverage >= minRating.Value)
            .Where(show => minVoteCount is null || show.VoteCount >= minVoteCount.Value)
            .Where(show => language is null || string.Equals(show.OriginalLanguage, language, StringComparison.OrdinalIgnoreCase));
        if (endpoint == "search/tv" && sortBy is not null)
        {
            filteredResults = sort switch
            {
                "popularity.asc" => filteredResults.OrderBy(show => show.Popularity),
                "vote_average.desc" => filteredResults.OrderByDescending(show => show.VoteAverage),
                "vote_average.asc" => filteredResults.OrderBy(show => show.VoteAverage),
                "first_air_date.desc" => filteredResults.OrderByDescending(show => show.FirstAirDate),
                "first_air_date.asc" => filteredResults.OrderBy(show => show.FirstAirDate),
                "name.asc" => filteredResults.OrderBy(show => show.Name),
                "name.desc" => filteredResults.OrderByDescending(show => show.Name),
                _ => filteredResults.OrderByDescending(show => show.Popularity),
            };
        }
        var results = filteredResults
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

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var normalized = language.Trim().ToLowerInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter) ? normalized : null;
    }

    [HttpGet("genres")]
    public async Task<IActionResult> ListGenres(CancellationToken cancellationToken)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey)) return TmdbHelpers.MissingApiKeyResult();
        var url = $"https://api.themoviedb.org/3/genre/tv/list?api_key={Uri.EscapeDataString(apiKey)}&language=en-US";
        var body = "";
        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.GetAsync(url, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return TmdbHelpers.TmdbFailureResult((int)response.StatusCode, response.ReasonPhrase, body);
            var result = JsonSerializer.Deserialize<TmdbGenreList>(body);
            return result?.Genres is null
                ? TmdbHelpers.TmdbNotJsonResult(body)
                : Ok(result.Genres.OrderBy(genre => genre.Name).Select(genre => new { genre.Id, genre.Name }));
        }
        catch (JsonException)
        {
            return TmdbHelpers.TmdbNotJsonResult(body);
        }
        catch (Exception exception)
        {
            return Problem($"TMDB request failed: {exception.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // GET /tv-shows/{id}/details — TMDB tv details + credits + similar + season list.
    [HttpGet("{id:long}/details")]
    public async Task<IActionResult> GetTvShowDetails(long id, CancellationToken cancellationToken)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var url = $"https://api.themoviedb.org/3/tv/{id}?api_key={Uri.EscapeDataString(apiKey)}&language=en-US&append_to_response=credits,similar,external_ids";

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
            Seasons: seasons,
            ImdbId: data.ExternalIds?.ImdbId));
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
