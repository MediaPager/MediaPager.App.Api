namespace MediaPager.App.Api.Dtos;

// DTOs returned to the SPA for the unified header search (type-ahead across all
// media categories). Kinds today: "movie" | "tv" | "podcast" | "audiobook" | "book" | "music".

public record SearchHit(
    string Kind,
    long Id,
    string Title,
    string? Year,
    double VoteAverage,
    string Overview,
    string? ArtworkUrl,
    string SourceKey,
    string ExternalId,
    int? CatalogItemId = null,
    int? CatalogId = null);
