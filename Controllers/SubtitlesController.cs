using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("subtitles")]
public sealed class SubtitlesController(
    IRuntimeSettings runtimeSettings,
    IPluginSettingsStore pluginSettings,
    PluginRegistry registry) : ControllerBase
{
    // GET /subtitles?tmdb=1377237&lang=en (or ?imdb=tt31349844 or ?query=movie%20title).
    // TV episodes: add &season=N&episode=N.
    [HttpGet]
    public async Task<IActionResult> SearchSubtitles(
        [FromQuery] string? imdb,
        [FromQuery] string? tmdb,
        [FromQuery] string? query,
        [FromQuery] string? lang,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        CancellationToken cancellationToken)
    {
        var titleQuery = query?.Trim();
        var lookupCount = new[]
        {
            !string.IsNullOrWhiteSpace(imdb),
            !string.IsNullOrWhiteSpace(tmdb),
            !string.IsNullOrWhiteSpace(titleQuery),
        }.Count(isSet => isSet);
        if (lookupCount != 1)
            return BadRequest(new { error = "Pass exactly one of imdb, tmdb, or query." });
        if (titleQuery is { Length: > 200 })
            return BadRequest(new { error = "Title searches must be 200 characters or fewer." });
        if ((season != null) != (episode != null))
            return BadRequest(new { error = "TV requests need both season and episode." });
        if (season is not null && (season < 1 || episode < 1))
            return BadRequest(new { error = "Season and episode must be positive integers." });

        var provider = await ActiveSubtitleProviderAsync(cancellationToken);
        if (provider is null)
            return Problem("No subtitle provider is loaded. Install one from Settings → Plugins.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            var results = await provider.SearchAsync(new SubtitleRequest(
                (lang ?? "en").Trim(), imdb, tmdb, titleQuery, season, episode), cancellationToken);
            return Ok(results.Select(hit => new
            {
                fileId = EncodeFileId(provider, hit.FileId),
                fileName = hit.FileName,
                release = hit.Release,
                language = hit.Language,
                hearingImpaired = hit.HearingImpaired,
                popularity = hit.Popularity,
            }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Problem($"{provider.Descriptor.Name} subtitle search failed: {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // GET /subtitles/file?id={fileId} — fetch provider-native content and convert SRT to
    // WebVTT for browser tracks. The file id includes its provider so changing the default
    // provider between search and selection cannot send an id to the wrong API.
    [HttpGet("file")]
    public async Task<IActionResult> FetchSubtitle([FromQuery] string id, CancellationToken cancellationToken)
    {
        var (provider, fileId) = await ProviderForFileAsync(id, cancellationToken);
        if (provider is null)
            return NotFound(new { error = "The subtitle provider is no longer loaded." });

        try
        {
            var document = await provider.FetchAsync(new SubtitleFetchRequest(fileId), cancellationToken);
            if (document is null)
                return NotFound(new { error = "The subtitle file was not found." });

            var content = document.Format == SubtitleFormat.Vtt
                ? document.Content
                : SrtToVtt(document.Content);
            return Content(content, "text/vtt");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Problem($"{provider.Descriptor.Name} subtitle fetch failed: {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<ISubtitleProviderPlugin?> ActiveSubtitleProviderAsync(CancellationToken cancellationToken)
    {
        var providers = registry.ResolveAll<ISubtitleProviderPlugin>();
        if (providers.Count == 0) return null;

        var wanted = (await runtimeSettings.GetAsync(RuntimeSettingKeys.SubtitlesProvider, cancellationToken))?.Trim();
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var selected = providers.FirstOrDefault(provider =>
                string.Equals(provider.Descriptor.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider.Descriptor.Id.Split('.')[^1], wanted, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) return selected;
        }

        foreach (var provider in providers)
        {
            if (await IsConfiguredAsync(provider, cancellationToken)) return provider;
        }
        return providers[0];
    }

    private async Task<bool> IsConfiguredAsync(ISubtitleProviderPlugin provider, CancellationToken cancellationToken)
    {
        if (provider is not IPluginSettingsSchema schema) return true;
        var settingsKey = provider.Descriptor.Id.Split('.')[^1];
        foreach (var setting in schema.Settings.Where(setting => setting.Required))
        {
            if (string.IsNullOrWhiteSpace(await pluginSettings.GetAsync(settingsKey, setting.Key, cancellationToken)))
                return false;
        }
        return true;
    }

    private async Task<(ISubtitleProviderPlugin? Provider, string FileId)> ProviderForFileAsync(
        string encodedId,
        CancellationToken cancellationToken)
    {
        var separator = encodedId.IndexOf('|');
        if (separator > 0)
        {
            var providerId = encodedId[..separator];
            var fileId = encodedId[(separator + 1)..];
            var provider = registry.ResolveAll<ISubtitleProviderPlugin>().FirstOrDefault(candidate =>
                string.Equals(candidate.Descriptor.Id, providerId, StringComparison.OrdinalIgnoreCase));
            return (provider, fileId);
        }

        // Accept older unqualified file ids using the currently selected provider.
        return (await ActiveSubtitleProviderAsync(cancellationToken), encodedId);
    }

    private static string EncodeFileId(ISubtitleProviderPlugin provider, string fileId) =>
        $"{provider.Descriptor.Id}|{fileId}";

    private static string SrtToVtt(string srt) =>
        "WEBVTT\n\n" + Regex.Replace(
            srt.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('﻿'),
            @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2");
}
