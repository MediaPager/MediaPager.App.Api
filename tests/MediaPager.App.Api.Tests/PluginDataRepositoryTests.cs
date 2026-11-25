using System.Data.Common;
using MediaPager.App.Api.Services;
using MediaPager.App.Core.Models;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class PluginDataRepositoryTests
{
    [Fact]
    public async Task PluginDocumentsAreIsolatedAndCollectionReplacementIsAtomic()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstPlugin = "mediapager.stream.alpha";
        var secondPlugin = "mediapager.stream.beta";

        await fixture.Repository.ReplaceCollectionAsync(firstPlugin, "public.records", [
            new PluginDataWrite("record-a", "{\"label\":\"A\"}"),
            new PluginDataWrite("record-b", "{\"label\":\"B\"}"),
        ]);
        await fixture.Repository.UpsertAsync(secondPlugin, "public.records", "record-a", "{\"label\":\"Other\"}");

        var firstPage = await fixture.Repository.ListAsync(firstPlugin, "public.records", limit: 10);
        var secondPage = await fixture.Repository.ListAsync(secondPlugin, "public.records", limit: 10);
        Assert.Equal(2, firstPage.Total);
        Assert.Single(secondPage.Items);
        Assert.Equal("{\"label\":\"Other\"}", secondPage.Items[0].Json);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.ReplaceCollectionAsync(
            firstPlugin,
            "public.records",
            [new PluginDataWrite("duplicate", "{}"), new PluginDataWrite("duplicate", "{}") ]));

        var unchanged = await fixture.Repository.ListAsync(firstPlugin, "public.records", limit: 10);
        Assert.Equal(2, unchanged.Total);
        Assert.Contains(unchanged.Items, item => item.Key == "record-a");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly DbConnection _connection;

        private Fixture(ServiceProvider provider, DbConnection connection, PluginDataRepository repository)
        {
            _provider = provider;
            _connection = connection;
            Repository = repository;
        }

        public PluginDataRepository Repository { get; }

        public static async Task<Fixture> CreateAsync()
        {
            DbConnection connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<AuthDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IPluginDataValueProtector, TestDataProtector>();
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            await using (var scope = scopeFactory.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync();
            return new Fixture(provider, connection, new PluginDataRepository(scopeFactory,
                provider.GetRequiredService<IPluginDataValueProtector>()));
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDataProtector : IPluginDataValueProtector
    {
        public string Protect(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
