namespace MediaPager.App.Api.Dtos;

// DTOs returned to the SPA for TV show browsing (TMDB-backed list, details, seasons/episodes).

public record TvShowSummary(long Id, string? Year, string Title, double VoteAverage, string Overview,
    string? PosterUrl, string? BackdropUrl, string? FirstAirDate);

public record TvPagedResponse(int Page, int TotalPages, int TotalResults, List<TvShowSummary> Results);

public record TvSeasonSummary(int SeasonNumber, string? Name, string? Overview, int? EpisodeCount,
    string? AirDate, string? PosterUrl);

public record TvEpisodeSummary(int EpisodeNumber, string Title, string Overview, string? AirDate,
    int? RuntimeMinutes, double VoteAverage, string? StillUrl);

public record TvShowDetails(long Id, string? Year, string Title, double VoteAverage, string Overview,
    string? PosterUrl, string? BackdropUrl, string? FirstAirDate, string? LastAirDate,
    int? NumberOfSeasons, string? Status, List<string> Genres, string? Creator,
    List<CastMember> Cast, List<TvShowSummary> Related, List<TvSeasonSummary> Seasons);

public record TvSeasonDetails(string? Name, string? Overview, string? AirDate, int SeasonNumber,
    List<TvEpisodeSummary> Episodes);