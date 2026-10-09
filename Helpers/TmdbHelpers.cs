// Shared helpers for TMDB-backed endpoints.
namespace MediaPager.App.Api.Helpers;

public static class TmdbHelpers
{
    // Host-side reads of the TMDB plugin's settings (plugins.tmdb.*). The plugin itself
    // reads the same store; config (plugins:tmdb:*) is the fallback.
    public static async Task<string?> GetApiKeyAsync(IPluginSettingsStore settings, CancellationToken ct) =>
        (await settings.GetAsync(PluginKeys.Tmdb, "apiKey", ct))?.Trim();

    public static async Task<string> GetImageBaseAsync(IPluginSettingsStore settings, CancellationToken ct) =>
        (await settings.GetAsync(PluginKeys.Tmdb, "imageBase", ct) ?? "https://image.tmdb.org/t/p/w500").TrimEnd('/');

    // Build a full image URL from a TMDB image path, passing through absolute URLs.
    public static string? ImageUrl(string imageBase, string? imagePath) => imagePath switch
    {
        null or "" => null,
        _ when imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) => imagePath,
        _ => $"{imageBase}{(imagePath.StartsWith('/') ? imagePath : "/" + imagePath)}"
    };

    public static string? Year(string? date) => date is { Length: >= 4 } d ? d[..4] : null;

    // Controller-friendly variants (MVC actions return IActionResult/ObjectResult, not IResult).
    public static Microsoft.AspNetCore.Mvc.ObjectResult MissingApiKeyResult() => new(new { error =
        "The TMDB API key is not set. An admin must set it in Settings → Plugins → TMDB before movies are available." })
    { StatusCode = StatusCodes.Status503ServiceUnavailable };

    public static Microsoft.AspNetCore.Mvc.ObjectResult TmdbFailureResult(int statusCode, string? reason, string body) => new(new
    { error = $"TMDB returned {statusCode} {reason}. Body starts with: {body[..Math.Min(body.Length, 300)]}" })
    { StatusCode = StatusCodes.Status502BadGateway };

    public static Microsoft.AspNetCore.Mvc.ObjectResult TmdbNotJsonResult(string body) => new(new
    { error = $"TMDB did not return JSON. Body starts with: {body[..Math.Min(body.Length, 300)]}" })
    { StatusCode = StatusCodes.Status502BadGateway };
}
