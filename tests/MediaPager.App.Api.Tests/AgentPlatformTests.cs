using System.Data.Common;
using MediaPager.App.Api.Services;
using MediaPager.App.Api.Services.Agents;
using MediaPager.App.Core.Models;
using MediaPager.App.Core.Plugins;
using MediaPager.App.PluginContracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class AgentPlatformTests
{
    [Fact]
    public void ManifestAcceptsAgentsAndCatalogScanningCapability()
    {
        const string json = """
            {
              "id": "mediapager.agents.sample",
              "name": "Sample Agent",
              "description": "",
              "author": "",
              "types": "agents",
              "sdk": "MediaPager.App.PluginContracts",
              "sdkVersion": "0.1.0",
              "hostCapabilities": ["catalog-scanning"]
            }
            """;

        var parsed = PluginManifestFile.TryParse(json, out var manifest, out var error);

        Assert.True(parsed, error);
        Assert.Contains("agents", manifest!.Types);
        Assert.Contains(PluginHostCapabilityNames.CatalogScanning, manifest.HostCapabilities!);
    }

    [Fact]
    public void AgentPluginDerivesAgentsCategoryAndCapability()
    {
        var plugin = new FakeAgentPlugin();

        Assert.Equal("agents", PluginJobsService.DeriveCategory(plugin));
        Assert.Contains("agents", PluginJobsService.CapabilitiesOf(plugin));
    }

    [Fact]
    public async Task CatalogScanStoreRejectsPathsOutsideTheCatalogFolder()
    {
        await using var fixture = await CatalogStoreFixture.CreateAsync();
        var (store, catalogId) = (fixture.Store, fixture.CatalogId);
        var outside = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CreateAsync(
                new CatalogScanCreateRequest(catalogId, "Escape", "movie", outside, 1)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CreateAsync(
                new CatalogScanCreateRequest(catalogId, "Traversal", "movie",
                    Path.Combine(fixture.Root, "..", "escape.mkv"), 1)));

            if (!OperatingSystem.IsWindows())
            {
                var outsideDirectory = Directory.CreateTempSubdirectory("mediapager-outside-");
                try
                {
                    var link = Path.Combine(fixture.Root, "linked-outside");
                    Directory.CreateSymbolicLink(link, outsideDirectory.FullName);
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CreateAsync(
                        new CatalogScanCreateRequest(catalogId, "Link escape", "movie",
                            Path.Combine(link, "escape.mkv"), 1)));
                }
                catch (UnauthorizedAccessException)
                {
                    // Some sandboxes do not allow creating symlinks; lexical checks above still ran.
                }
                finally
                {
                    outsideDirectory.Delete(recursive: true);
                }
            }

            var inside = Path.Combine(fixture.Root, "inside.mkv");
            await File.WriteAllTextAsync(inside, "content");
            var id = await store.CreateAsync(
                new CatalogScanCreateRequest(catalogId, "Inside", "movie", inside, 7));

            var items = await store.ListItemsAsync(catalogId);
            Assert.Single(items);
            Assert.Equal(id, items[0].Id);
            Assert.Equal("inside.mkv", Path.GetFileName(items[0].StoragePath));
            Assert.Equal(id, (await store.FindByPathAsync(inside))!.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(
                new CatalogScanCreateRequest(catalogId, "Duplicate", "movie", inside, 7)));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task CatalogScanStoreReplacesTagsAndAppliesOnlyNonNullFields()
    {
        await using var fixture = await CatalogStoreFixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "track.mp3");
        await File.WriteAllTextAsync(path, "audio");
        var id = await fixture.Store.CreateAsync(new CatalogScanCreateRequest(
            fixture.CatalogId,
            "Original",
            "track",
            path,
            42,
            new CatalogScanIdentityUpdate(
                Fingerprint: "fp-1",
                Confidence: 0.9,
                Metadata: new CatalogScanMetadataUpdate(
                    Tags: [new CatalogScanTag("genre", "Jazz"), new CatalogScanTag("country", "US")]))));

        await fixture.Store.UpdateAsync(id, new CatalogScanIdentityUpdate(
            LastSeenAt: DateTimeOffset.UtcNow,
            Metadata: new CatalogScanMetadataUpdate(
                Title: "Updated",
                Tags: [new CatalogScanTag("GENRE", "Blues"), new CatalogScanTag("tags", "favorite")])));

        var item = await fixture.FindItemAsync(id);
        Assert.Equal("Updated", item.Title);
        Assert.Equal("fp-1", item.Fingerprint);
        Assert.Equal(0.9, item.Confidence);
        Assert.NotNull(item.LastSeenAt);

        var tags = await fixture.FindTagsAsync(id);
        Assert.Equal(2, tags.Count);
        Assert.Contains(tags, tag => tag.Type == "genre" && tag.Value == "Blues");
        Assert.Contains(tags, tag => tag.Type == "tags" && tag.Value == "favorite");
        Assert.DoesNotContain(tags, tag => tag.Type == "country");
    }

    [Fact]
    public async Task RuntimeHonoursAgentIntervalsAndSkipsDisabledAgents()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:PollIntervalSeconds"] = "0.05",
            })
            .Build();
        var plugin = new FakeAgentPlugin(TimeSpan.FromMilliseconds(20));
        var registry = new PluginRegistry();
        registry.Reset([plugin]);
        var activity = new PluginActivityStore();
        var runtime = new AgentRuntimeService(registry, activity, configuration,
            NullLogger<AgentRuntimeService>.Instance);

        await runtime.RunOnceAsync();
        Assert.Equal(1, plugin.RunCount);
        var job = Assert.Single(activity.SnapshotJobs());
        Assert.Equal("mediapager.agents.fake", job.PluginId);

        await runtime.RunOnceAsync();
        Assert.Equal(1, plugin.RunCount);

        await Task.Delay(30);
        await runtime.RunOnceAsync();
        Assert.Equal(2, plugin.RunCount);
    }

    [Fact]
    public async Task ManualRunForcesImmediatelyAndResetsTheNextScheduledRun()
    {
        var plugin = new FakeAgentPlugin(TimeSpan.FromSeconds(30));
        var registry = new PluginRegistry();
        registry.Reset([plugin]);
        var runtime = new AgentRuntimeService(registry, new PluginActivityStore(),
            new ConfigurationBuilder().Build(), NullLogger<AgentRuntimeService>.Instance);

        await runtime.RunOnceAsync();
        Assert.Equal(1, plugin.RunCount);
        Assert.False(plugin.LastForce);

        var result = await runtime.RunManuallyAsync(plugin, "scan");
        Assert.True(result.Success);
        Assert.Equal(2, plugin.RunCount);
        Assert.True(plugin.LastForce);

        await runtime.RunOnceAsync();
        Assert.Equal(2, plugin.RunCount);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => runtime.RunManuallyAsync(plugin, "missing"));
    }

    private sealed class FakeAgentPlugin(TimeSpan? interval = null) : IAgentPlugin
    {
        public PluginDescriptor Descriptor { get; } = new(
            "mediapager.agents.fake", "Fake Agent", "1.0.0");

        public int RunCount { get; private set; }
        public bool LastForce { get; private set; }

        private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(20);

        public Task<IReadOnlyList<AgentDescriptor>> GetAgentsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDescriptor>>(
            [
                new AgentDescriptor("scan", "Scan", "Scan files", true, _interval, ["movies"]),
                new AgentDescriptor("disabled", "Disabled", "Never runs", false, _interval, []),
            ]);

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastForce = request.Force;
            return Task.FromResult(new AgentRunResult(request.AgentId, 1, 0, 0, 0, 0));
        }
    }

    private sealed class CatalogStoreFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly DbConnection _connection;
        private readonly string _root;

        private CatalogStoreFixture(ServiceProvider provider, DbConnection connection, CatalogScanStore store, string root, int catalogId)
        {
            _provider = provider;
            _connection = connection;
            _root = root;
            Store = store;
            CatalogId = catalogId;
        }

        public CatalogScanStore Store { get; }
        public int CatalogId { get; }
        public string Root => _root;

        public static async Task<CatalogStoreFixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("mediapager-catalog-scan-").FullName;
            DbConnection connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<AuthDbContext>(options => options.UseSqlite(connection));
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                await database.Database.EnsureCreatedAsync();
                var mediaType = new MediaType { Name = "Video", Slug = "video" };
                database.MediaTypes.Add(mediaType);
                await database.SaveChangesAsync();
                var catalogType = new CatalogType { Name = "Movies", Slug = "movies", MediaTypeId = mediaType.Id };
                database.CatalogTypes.Add(catalogType);
                await database.SaveChangesAsync();
                var catalog = new Catalog { Name = "Scan root", Path = root, CatalogTypeId = catalogType.Id };
                database.Catalogs.Add(catalog);
                await database.SaveChangesAsync();
                var catalogId = catalog.Id;
                return new CatalogStoreFixture(provider, connection, new CatalogScanStore(scopeFactory), root, catalogId);
            }
        }

        public async Task<CatalogItem> FindItemAsync(int id)
        {
            await using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            return await database.CatalogItems.AsNoTracking().FirstAsync(item => item.Id == id);
        }

        public async Task<List<CatalogItemTag>> FindTagsAsync(int itemId)
        {
            await using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            return await database.CatalogItemTags.AsNoTracking()
                .Where(tag => tag.CatalogItemId == itemId)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _provider.Dispose();
            await _connection.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
