using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("movies")]
public sealed class MoviesController(IHttpClientFactory httpClientFactory, IPluginSettingsRepository pluginSettings) : ControllerBase
{
    // GET /movies — TMDB popular/search results or discover results when filters are supplied.
    [HttpGet]
    public async Task<IActionResult> ListMovies(
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
            "primary_release_date.desc", "primary_release_date.asc", "title.asc", "title.desc",
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

        // "cast: <name>" searches movies featuring that cast member:
        // resolve the name via search/person, then discover/movie?with_cast={id}.
        const string castPrefix = "cast:";
        if (searchQuery.StartsWith(castPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var personName = searchQuery[castPrefix.Length..].Trim();
            if (personName.Length == 0)
                return Ok(new PagedResponse(1, 1, 0, []));

            var client = httpClientFactory.CreateClient("upstream");
            var personUrl = $"https://api.themoviedb.org/3/search/person?api_key={Uri.EscapeDataString(apiKey)}&language=en-US&page=1&query={Uri.EscapeDataString(personName)}";
            TmdbPersonResponse? personData;
            var personBody = "";
            try
            {
                using var personResponse = await client.GetAsync(personUrl, cancellationToken);
                personBody = await personResponse.Content.ReadAsStringAsync(cancellationToken);

                if (!personResponse.IsSuccessStatusCode)
                    return TmdbHelpers.TmdbFailureResult((int)personResponse.StatusCode, personResponse.ReasonPhrase, personBody);

                personData = JsonSerializer.Deserialize<TmdbPersonResponse>(personBody);
            }
            catch (JsonException)
            {
                return TmdbHelpers.TmdbNotJsonResult(personBody);
            }
            catch (Exception ex)
            {
                return Problem($"TMDB request failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }

            var personId = personData?.Results?.FirstOrDefault()?.Id;
            if (personId is null)
                return Ok(new PagedResponse(1, 1, 0, []));

            var discoverUrl = $"https://api.themoviedb.org/3/discover/movie?api_key={Uri.EscapeDataString(apiKey)}&language=en-US&sort_by=popularity.desc&page={currentPage}&with_cast={personId}";
            if (genreId is int castGenre) discoverUrl += $"&with_genres={castGenre}";
            if (year is int castYear) discoverUrl += $"&primary_release_year={castYear}";
            if (minRating is double castRating)
                discoverUrl += $"&vote_average.gte={castRating.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (minVoteCount is int castVoteCount) discoverUrl += $"&vote_count.gte={castVoteCount}";
            if (language is not null) discoverUrl += $"&with_original_language={Uri.EscapeDataString(language)}";
            if (sortBy is not null) discoverUrl += $"&sort_by={Uri.EscapeDataString(sort)}";
            TmdbResponse? discoverData;
            var discoverBody = "";
            try
            {
                using var discoverResponse = await client.GetAsync(discoverUrl, cancellationToken);
                discoverBody = await discoverResponse.Content.ReadAsStringAsync(cancellationToken);

                if (!discoverResponse.IsSuccessStatusCode)
                    return TmdbHelpers.TmdbFailureResult((int)discoverResponse.StatusCode, discoverResponse.ReasonPhrase, discoverBody);

                discoverData = JsonSerializer.Deserialize<TmdbResponse>(discoverBody);
            }
            catch (JsonException)
            {
                return TmdbHelpers.TmdbNotJsonResult(discoverBody);
            }
            catch (Exception ex)
            {
                return Problem($"TMDB request failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }

            if (discoverData?.Results is null)
                return TmdbHelpers.TmdbNotJsonResult(discoverBody);

            var discoverImageBase = await TmdbHelpers.GetImageBaseAsync(pluginSettings, cancellationToken);
            var discoverResults = discoverData.Results
                .Select(m => new MovieSummary(
                    Id: m.Id,
                    Year: TmdbHelpers.Year(m.ReleaseDate),
                    Title: m.Title ?? "-",
                    VoteAverage: m.VoteAverage,
                    Overview: m.Overview ?? "",
                    PosterUrl: TmdbHelpers.ImageUrl(discoverImageBase, m.PosterPath),
                    BackdropUrl: TmdbHelpers.ImageUrl(discoverImageBase, m.BackdropPath),
                    ReleaseDate: m.ReleaseDate))
                .ToList();

            return Ok(new PagedResponse(discoverData.Page, discoverData.TotalPages, discoverData.TotalResults, discoverResults));
        }

        var endpoint = !string.IsNullOrEmpty(searchQuery)
            ? "search/movie"
            : hasDiscoverFilters ? "discover/movie" : "movie/popular";

        var queryParams = new List<string>
        {
            $"api_key={Uri.EscapeDataString(apiKey)}",
            "language=en-US",
            $"page={currentPage}",
        };
        if (!string.IsNullOrEmpty(searchQuery)) queryParams.Add($"query={Uri.EscapeDataString(searchQuery)}");
        if (endpoint == "discover/movie")
        {
            queryParams.Add($"sort_by={Uri.EscapeDataString(sort)}");
            if (genreId is int selectedGenre) queryParams.Add($"with_genres={selectedGenre}");
            if (year is int releaseYear) queryParams.Add($"primary_release_year={releaseYear}");
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
            .Where(movie => genreId is null || movie.GenreIds?.Contains(genreId.Value) == true)
            .Where(movie => year is null || TmdbHelpers.Year(movie.ReleaseDate ?? movie.FirstAirDate) == year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Where(movie => minRating is null || movie.VoteAverage >= minRating.Value)
            .Where(movie => minVoteCount is null || movie.VoteCount >= minVoteCount.Value)
            .Where(movie => language is null || string.Equals(movie.OriginalLanguage, language, StringComparison.OrdinalIgnoreCase));
        if (endpoint == "search/movie" && sortBy is not null)
        {
            filteredResults = sort switch
            {
                "popularity.asc" => filteredResults.OrderBy(movie => movie.Popularity),
                "vote_average.desc" => filteredResults.OrderByDescending(movie => movie.VoteAverage),
                "vote_average.asc" => filteredResults.OrderBy(movie => movie.VoteAverage),
                "primary_release_date.desc" => filteredResults.OrderByDescending(movie => movie.ReleaseDate),
                "primary_release_date.asc" => filteredResults.OrderBy(movie => movie.ReleaseDate),
                "title.asc" => filteredResults.OrderBy(movie => movie.Title),
                "title.desc" => filteredResults.OrderByDescending(movie => movie.Title),
                _ => filteredResults.OrderByDescending(movie => movie.Popularity),
            };
        }
        var results = filteredResults
            .Select(m => new MovieSummary(
                Id: m.Id,
                Year: TmdbHelpers.Year(m.ReleaseDate ?? m.FirstAirDate),
                Title: m.Title ?? m.Name ?? "-",
                VoteAverage: m.VoteAverage,
                Overview: m.Overview ?? "",
                PosterUrl: TmdbHelpers.ImageUrl(imageBase, m.PosterPath),
                BackdropUrl: TmdbHelpers.ImageUrl(imageBase, m.BackdropPath),
                ReleaseDate: m.ReleaseDate ?? m.FirstAirDate))
            .ToList();

        return Ok(new PagedResponse(data.Page, data.TotalPages, data.TotalResults, results));
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
        var url = $"https://api.themoviedb.org/3/genre/movie/list?api_key={Uri.EscapeDataString(apiKey)}&language=en-US";
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

    // GET /movies/{id}/details — TMDB v3 movie details + credits + similar, for the detail screen.
    [HttpGet("{id:long}/details")]
    public async Task<IActionResult> GetMovieDetails(long id, CancellationToken cancellationToken)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var url = $"https://api.themoviedb.org/3/movie/{id}?api_key={Uri.EscapeDataString(apiKey)}&language=en-US&append_to_response=credits,similar";

        TmdbMovieDetails? data;
        var body = "";
        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.GetAsync(url, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return TmdbHelpers.TmdbFailureResult((int)response.StatusCode, response.ReasonPhrase, body);

            data = JsonSerializer.Deserialize<TmdbMovieDetails>(body);
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

        var director = data.Credits?.Crew?.FirstOrDefault(c => string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase))?.Name;

        var related = (data.Similar?.Results ?? [])
            .Take(12)
            .Select(m => new MovieSummary(
                Id: m.Id,
                Year: TmdbHelpers.Year(m.ReleaseDate),
                Title: m.Title ?? "-",
                VoteAverage: m.VoteAverage,
                Overview: m.Overview ?? "",
                PosterUrl: TmdbHelpers.ImageUrl(imageBase, m.PosterPath),
                BackdropUrl: TmdbHelpers.ImageUrl(imageBase, m.BackdropPath),
                ReleaseDate: m.ReleaseDate))
            .ToList();

        return Ok(new MovieDetails(
            Id: data.Id,
            Year: TmdbHelpers.Year(data.ReleaseDate),
            Title: data.Title ?? "-",
            VoteAverage: data.VoteAverage,
            Overview: data.Overview ?? "",
            PosterUrl: TmdbHelpers.ImageUrl(imageBase, data.PosterPath),
            BackdropUrl: TmdbHelpers.ImageUrl(imageBase, data.BackdropPath),
            ReleaseDate: data.ReleaseDate,
            RuntimeMinutes: data.Runtime,
            Genres: (data.Genres ?? []).Select(g => g.Name ?? "-").ToList(),
            Director: director,
            Cast: cast,
            Related: related,
            ImdbId: data.ImdbId));
    }
}
