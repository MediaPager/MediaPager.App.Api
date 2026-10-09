namespace MediaPager.App.Api.Dtos;

// DTOs returned to the SPA.

public record MovieSummary(long Id, string? Year, string Title, double VoteAverage, string Overview,
    string? PosterUrl, string? BackdropUrl, string? ReleaseDate);

public record PagedResponse(int Page, int TotalPages, int TotalResults, List<MovieSummary> Results);

public record CastMember(string Name, string? Character, string? ProfileUrl);

public record MovieDetails(long Id, string? Year, string Title, double VoteAverage, string Overview,
    string? PosterUrl, string? BackdropUrl, string? ReleaseDate, int? RuntimeMinutes,
    List<string> Genres, string? Director, List<CastMember> Cast, List<MovieSummary> Related);
