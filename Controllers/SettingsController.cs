using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("settings")]
[Authorize(Policy = "CanEditSettings")]
public sealed class SettingsController(
    IRuntimeSettings settings,
    IPluginSettingsStore pluginSettings,
    AuthDbContext database,
    PluginRegistry registry,
    IConfiguration configuration) : ControllerBase
{
    // Runtime settings are shared by the SPA and the desktop launcher. Editing them
    // requires admin:settings-edit (or admin:super, which satisfies every policy).
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(await settings.GetAllAsync(cancellationToken));
        var subtitleProviders = registry.ResolveAll<ISubtitleProviderPlugin>();
        var selectedSubtitleProvider = values.GetValueOrDefault(RuntimeSettingKeys.SubtitlesProvider);
        var selectionIsLoaded = !string.IsNullOrWhiteSpace(selectedSubtitleProvider) && subtitleProviders.Any(provider =>
            string.Equals(provider.Descriptor.Id, selectedSubtitleProvider, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider.Descriptor.Id.Split('.')[^1], selectedSubtitleProvider, StringComparison.OrdinalIgnoreCase));
        if (!selectionIsLoaded)
        {
            var providerId = await DefaultSubtitleProviderIdAsync(cancellationToken);
            if (providerId is not null) values[RuntimeSettingKeys.SubtitlesProvider] = providerId;
            else values.Remove(RuntimeSettingKeys.SubtitlesProvider);
        }
        return Ok(values);
    }

    private async Task<string?> DefaultSubtitleProviderIdAsync(CancellationToken cancellationToken)
    {
        var providers = registry.ResolveAll<ISubtitleProviderPlugin>();
        foreach (var provider in providers)
        {
            if (provider is not IPluginSettingsSchema schema) return provider.Descriptor.Id;
            var settingsKey = provider.Descriptor.Id.Split('.')[^1];
            var configured = true;
            foreach (var setting in schema.Settings.Where(setting => setting.Required))
            {
                if (!string.IsNullOrWhiteSpace(await pluginSettings.GetAsync(settingsKey, setting.Key, cancellationToken)))
                    continue;
                configured = false;
                break;
            }
            if (configured) return provider.Descriptor.Id;
        }
        return providers.FirstOrDefault()?.Descriptor.Id;
    }

    [HttpPut]
    public async Task<IActionResult> SaveAll(Dictionary<string, string> request, CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { error = "A settings object is required." });
        var sanitized = request
            .Where(kv => RuntimeSettingKeys.All.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Trim() ?? "", StringComparer.Ordinal);
        await settings.SetAllAsync(sanitized, cancellationToken);
        return Ok(new { message = "Settings saved." });
    }

    // GET /settings/setup-checklist — a "did you set up everything you need?" screen for
    // Settings. Plugin items are derived from Plugins:Required (by id, appsettings), so
    // adding a required plugin to the lists is enough to enforce it here.
    [HttpGet("setup-checklist")]
    public async Task<IActionResult> SetupChecklist(CancellationToken cancellationToken)
    {
        var catalogCount = await database.Catalogs.CountAsync(cancellationToken);
        var items = new List<SetupChecklistItem>();
        items.AddRange(await RequiredPluginItemsAsync(cancellationToken));
        items.AddRange([
            new SetupChecklistItem(
                Key: "catalog",
                Label: "At least one catalog",
                Ok: catalogCount > 0,
                Hint: "Create a catalog to organize your library.",
                Action: "catalogs"),
        ]);

        return Ok(new { items, ready = items.All(item => item.Ok) });
    }

    // One checklist item per required plugin (Plugins:Required:Official + :Community, by
    // id): installed = loaded in the registry; configured = the plugin says so (email
    // providers answer IsConfiguredAsync) or every required setting of its schema is set.
    private async Task<IEnumerable<SetupChecklistItem>> RequiredPluginItemsAsync(CancellationToken cancellationToken)
    {
        var required = PluginCatalog.ReadRequired(configuration);
        var officialNames = PluginCatalog.ReadOfficial(configuration)
            .ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var communityNames = PluginCatalog.ReadCommunity(configuration)
            .ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        var items = new List<SetupChecklistItem>();
        foreach (var id in required.Official.Concat(required.Community))
        {
            var plugin = registry.Plugins.FirstOrDefault(candidate =>
                string.Equals(candidate.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
            var name = officialNames.GetValueOrDefault(id)
                ?? communityNames.GetValueOrDefault(id)
                ?? id;
            var loaded = plugin is not null;
            var configured = loaded && await IsConfiguredAsync(plugin!, cancellationToken);
            items.Add(new SetupChecklistItem(
                Key: $"plugin:{id}",
                Label: $"{name} plugin",
                Ok: loaded && configured,
                Hint: !loaded
                    ? $"Not installed — install it from Settings → Plugins (required by Plugins:Required)."
                    : configured
                        ? "Installed and configured."
                        : "Installed — finish its settings in Settings → Plugins.",
                Action: $"plugin:{id}"));
        }
        return items;
    }

    private async Task<bool> IsConfiguredAsync(IMediaPagerPlugin plugin, CancellationToken cancellationToken)
    {
        if (plugin is IEmailProviderPlugin email)
            return await email.IsConfiguredAsync(cancellationToken);
        if (plugin is IPluginSettingsSchema schema)
        {
            // Settings key = last segment of the descriptor id, like PluginSettingsService.
            var settingsKey = plugin.Descriptor.Id.Split('.')[^1];
            foreach (var setting in schema.Settings.Where(definition => definition.Required))
            {
                if (string.IsNullOrWhiteSpace(await pluginSettings.GetAsync(settingsKey, setting.Key, cancellationToken)))
                    return false;
            }
        }
        return true;
    }

}

public sealed record SetupChecklistItem(string Key, string Label, bool Ok, string Hint, string? Action = null);
