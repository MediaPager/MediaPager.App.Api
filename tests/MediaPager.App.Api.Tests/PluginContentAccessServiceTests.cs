using System.Security.Claims;
using MediaPager.App.Api.Controllers;
using MediaPager.App.Api.Services;
using MediaPager.App.Core.Models;
using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class PluginContentAccessServiceTests
{
    [Fact]
    public async Task ScopeOrGlobalGrantIsRequiredAndUserHidePreferenceOverridesBoth()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options;
        await using var database = new AuthDbContext(options);
        await database.Database.EnsureCreatedAsync();

        var plugin = new ScopedPlugin();
        var registry = new PluginRegistry();
        registry.Reset([plugin]);
        var scopes = ScopeSecurityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "scope-security.json"));
        var settings = new MemorySettingsRepository();
        var service = new PluginContentAccessService(registry, scopes, settings, database);

        database.UserSettings.Add(new UserSetting
        {
            UserId = "user-1",
            Key = UserSettingsController.PluginUserSettingKey("mediapager.stream.sample", "hideAdult"),
            Value = "true",
        });
        await database.SaveChangesAsync();
        var scopedUser = Principal("user-1", "sample:adult-content");

        Assert.False(await service.CanAccessAsync(plugin, "adult-content", scopedUser));

        var preference = await database.UserSettings.SingleAsync();
        preference.Value = "false";
        await database.SaveChangesAsync();
        Assert.True(await service.CanAccessAsync(plugin, "adult-content", scopedUser));

        database.UserSettings.Add(new UserSetting
        {
            UserId = "user-2",
            Key = UserSettingsController.PluginUserSettingKey("mediapager.stream.sample", "hideAdult"),
            Value = "false",
        });
        await database.SaveChangesAsync();
        var unscopedUser = Principal("user-2");
        Assert.False(await service.CanAccessAsync(plugin, "adult-content", unscopedUser));
        settings.Values[GlobalSettingKey] = "true";
        Assert.True(await service.CanAccessAsync(plugin, "adult-content", unscopedUser));
    }

    private static ClaimsPrincipal Principal(string userId, params string[] scopes) =>
        new(new ClaimsIdentity(
            [new Claim("sub", userId), .. scopes.Select(scope => new Claim("scope", scope))],
            authenticationType: "test"));

    private const string GlobalSettingKey = "enableAdultChannels";

    private sealed class ScopedPlugin : IMediaPagerPlugin, IPluginScopeSchema, IPluginUserSettingsSchema
    {
        public PluginDescriptor Descriptor { get; } = new("mediapager.stream.sample", "Test", "1.0.0");
        public IReadOnlyList<PluginScopeDefinition> Scopes { get; } =
        [
            new PluginScopeDefinition("AdultContent", "Restricted content", "Test scope.")
            {
                GlobalAccessSettingKey = "enableAdultChannels",
            },
        ];
        public IReadOnlyList<PluginUserPreferenceDefinition> UserSettings { get; } =
        [
            new PluginUserPreferenceDefinition("hideAdult", "Hide restricted content", "Test preference.", DefaultValue: true)
            {
                HidesScope = "adult-content",
            },
        ];
    }

    private sealed class MemorySettingsRepository : IPluginSettingsRepository
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string pluginId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(name));

        public Task SetAsync(string pluginId, string name, string? value, CancellationToken cancellationToken = default)
        {
            Values[name] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(string pluginId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Values
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase));

        public Task SeedSchemaDefaultsAsync(
            IMediaPagerPlugin plugin,
            IPluginSettingsSchema schema,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
