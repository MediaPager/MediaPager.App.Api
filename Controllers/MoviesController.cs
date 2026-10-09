using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("movies")]
public sealed class MoviesController(IHttpClientFactory httpClientFactory, IPluginSettingsStore pluginSettings) : ControllerBase
{
    // GET /movies?q=&page=1 — TMDB v3. movie/popular when no search, search/movie when searching.
    [HttpGet]
    public async Task<IActionResult> ListMovies([FromQuery(Name = "q")] string? q, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        var apiKey = await TmdbHelpers.GetApiKeyAsync(pluginSettings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return TmdbHelpers.MissingApiKeyResult();

        var searchQuery = (q ?? "").Trim();
        var currentPage = page > 0 ? page : 1;

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

        var endpoint = string.IsNullOrEmpty(searchQuery) ? "movie/popular" : "search/movie";

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
            Related: related));
    }
}
