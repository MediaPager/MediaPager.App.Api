using System.Text.Json;
using MediaPager.App.Api.Controllers;
using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class SubtitlesControllerTests
{
    [Fact]
    public async Task Search_combines_provider_results_and_prioritizes_configured_provider()
    {
        var subdl = new FakeSubtitleProvider(
            "mediapager.subtitles.subdl",
            "Subdl",
            (_, _) => Task.FromResult<IReadOnlyList<SubtitleHit>>([
                new SubtitleHit("subdl-file", "en", "shared.srt", "release-a", Popularity: 10),
            ]));
        var openSubtitles = new FakeSubtitleProvider(
            "mediapager.subtitles.opensubtitles",
            "OpenSubtitles",
            (_, _) => Task.FromResult<IReadOnlyList<SubtitleHit>>([
                new SubtitleHit("opensubs-file", "en", "shared.srt", "release-a", Popularity: 10),
            ]));
        var registry = new PluginRegistry();
        registry.Reset([subdl, openSubtitles]);
        var controller = CreateController(registry, "opensubtitles");

        var response = await controller.SearchSubtitles(
            imdb: null,
            tmdb: "123",
            query: null,
            lang: "en",
            season: null,
            episode: null,
            CancellationToken.None);

        var hits = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(2, hits.GetArrayLength());
        Assert.Equal("OpenSubtitles", hits[0].GetProperty("providerName").GetString());
        Assert.Equal("mediapager.subtitles.opensubtitles|opensubs-file", hits[0].GetProperty("fileId").GetString());
        Assert.Equal("Subdl", hits[1].GetProperty("providerName").GetString());
        Assert.Equal("mediapager.subtitles.subdl|subdl-file", hits[1].GetProperty("fileId").GetString());
    }

    [Fact]
    public async Task Search_keeps_successful_results_when_one_provider_fails()
    {
        var failed = new FakeSubtitleProvider(
            "mediapager.subtitles.broken",
            "Broken",
            (_, _) => throw new InvalidOperationException("provider failure"));
        var working = new FakeSubtitleProvider(
            "mediapager.subtitles.working",
            "Working",
            (_, _) => Task.FromResult<IReadOnlyList<SubtitleHit>>([
                new SubtitleHit("subtitle-1", "en", "movie.srt"),
            ]));
        var registry = new PluginRegistry();
        registry.Reset([failed, working]);

        var response = await CreateController(registry).SearchSubtitles(
            imdb: null,
            tmdb: "123",
            query: null,
            lang: "en",
            season: null,
            episode: null,
            CancellationToken.None);

        var hits = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Single(hits.EnumerateArray());
        Assert.Equal("Working", hits[0].GetProperty("providerName").GetString());
    }

    [Fact]
    public async Task Search_returns_bad_gateway_when_every_provider_fails()
    {
        var provider = new FakeSubtitleProvider(
            "mediapager.subtitles.broken",
            "Broken",
            (_, _) => throw new InvalidOperationException("provider failure"));
        var registry = new PluginRegistry();
        registry.Reset([provider]);

        var response = await CreateController(registry).SearchSubtitles(
            imdb: null,
            tmdb: "123",
            query: null,
            lang: "en",
            season: null,
            episode: null,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(response).StatusCode);
    }

    private static SubtitlesController CreateController(PluginRegistry registry, string? preferredProvider = null) =>
        new(new FakeRuntimeSettings(preferredProvider), new FakePluginSettingsRepository(),
            registry, NullLogger<SubtitlesController>.Instance);

    private sealed class FakeSubtitleProvider(
        string id,
        string name,
        Func<SubtitleRequest, CancellationToken, Task<IReadOnlyList<SubtitleHit>>> search) : ISubtitleProviderPlugin
    {
        public PluginDescriptor Descriptor { get; } = new(id, name, "1.0.0");

        public Task<IReadOnlyList<SubtitleHit>> SearchAsync(
            SubtitleRequest request,
            CancellationToken cancellationToken) => search(request, cancellationToken);

        public Task<SubtitleDocument?> FetchAsync(SubtitleFetchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<SubtitleDocument?>(null);
    }

    private sealed class FakeRuntimeSettings(string? preferredProvider) : IRuntimeSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == RuntimeSettingKeys.SubtitlesProvider ? preferredProvider : null);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetAllAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SeedMissingFromConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveDatabaseSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePluginSettingsRepository : IPluginSettingsRepository
    {
        public Task<string?> GetAsync(string pluginId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string pluginId, string name, string? value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(string pluginId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SeedSchemaDefaultsAsync(
            IMediaPagerPlugin plugin,
            IPluginSettingsSchema schema,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
