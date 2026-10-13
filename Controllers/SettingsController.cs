using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaPager.App.Api.Controllers;

[ApiController]
[Route("settings")]
[Authorize(Policy = "CanEditSettings")]
public sealed class SettingsController(
    IRuntimeSettings settings,
    IPluginSettingsRepository pluginSettings,
    AuthDbContext database,
    PluginRegistry registry,
    EmailSender emailSender,
    IConfiguration configuration,
    GitSshPrivateKeyStore gitSshKey,
    StorageLayout storageLayout,
    ApiRestartCoordinator restartCoordinator) : ControllerBase
{
    // Runtime settings are shared by the SPA and the desktop launcher. Editing them
    // requires admin:settings-edit (or admin:super, which satisfies every policy).
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(await settings.GetAllAsync(cancellationToken));
        values[StorageLayout.RootKey] = storageLayout.DataRoot;
        values[RuntimeSettingKeys.ConfigDirectory] = storageLayout.ConfigDirectory;
        values[RuntimeSettingKeys.DataProtectionKeysDirectory] = storageLayout.DataProtectionKeysDirectory;
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
        var emailProvider = await emailSender.ProviderAsync(cancellationToken);
        if (emailProvider is not null) values[RuntimeSettingKeys.EmailProvider] = emailProvider.Descriptor.Id;
        else values.Remove(RuntimeSettingKeys.EmailProvider);
        return Ok(values);
    }

    private async Task<string?> DefaultSubtitleProviderIdAsync(CancellationToken cancellationToken)
    {
        var providers = registry.ResolveAll<ISubtitleProviderPlugin>();
        foreach (var provider in providers)
        {
            if (provider is not IPluginSettingsSchema schema) return provider.Descriptor.Id;
            var settingsKey = provider.Descriptor.Id;
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
        if (sanitized.TryGetValue(RuntimeSettingKeys.FrontendBaseUrl, out var siteUrl) &&
            siteUrl.Length > 0 &&
            (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var siteUri) || !FrontendUrlPolicy.IsAllowed(siteUri)))
            return BadRequest(new { error = "Use an HTTPS site URL, or HTTP for localhost or a private LAN address." });

        await settings.SetAllAsync(sanitized, cancellationToken);
        var restartRequired = sanitized.Keys.Any(key => key is
            RuntimeSettingKeys.ToolsDirectory or RuntimeSettingKeys.CommunityPluginsDirectory or
            RuntimeSettingKeys.InstalledOfficialPluginsDirectory);
        if (restartRequired) restartCoordinator.RestartAfterResponse(Response);
        var message = restartRequired
            ? "Storage settings saved. The API is restarting to apply the new directories."
            : "Settings saved.";
        return Ok(new { message });
    }

    [HttpGet("git-ssh-key")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> GetGitSshKeyStatus(CancellationToken cancellationToken) =>
        Ok(new { configured = await gitSshKey.IsConfiguredAsync(cancellationToken) });

    [HttpPut("git-ssh-key")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> SetGitSshKey(SetGitSshPrivateKeyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PrivateKey))
            return BadRequest(new { error = "Paste an SSH private key to save it." });
        try
        {
            await gitSshKey.SetPrivateKeyAsync(request.PrivateKey, cancellationToken);
            return Ok(new { message = "Encrypted SSH key saved." });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("git-ssh-key")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ClearGitSshKey(CancellationToken cancellationToken)
    {
        await gitSshKey.ClearAsync(cancellationToken);
        return Ok(new { message = "Saved SSH key removed." });
    }

    // GET /settings/setup-checklist — required plugins and core setup. Email providers are
    // shown as alternatives: any one configured provider satisfies system mail.
    [HttpGet("setup-checklist")]
    public async Task<IActionResult> SetupChecklist(CancellationToken cancellationToken)
    {
        var catalogCount = await database.Catalogs.CountAsync(cancellationToken);
        var items = new List<SetupChecklistItem>();
        items.AddRange(await RequiredPluginItemsAsync(cancellationToken));
        items.AddRange(await EmailProviderItemsAsync(cancellationToken));
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

    // One checklist item per required non-email plugin. Email providers are alternatives:
    // the system needs one configured provider, not every enabled provider.
    private async Task<IEnumerable<SetupChecklistItem>> RequiredPluginItemsAsync(CancellationToken cancellationToken)
    {
        var required = PluginCatalog.ReadRequired(configuration);
        var officialCatalog = PluginCatalog.ReadOfficial(configuration);
        var communityCatalog = PluginCatalog.ReadCommunity(configuration);
        var emailProviderIds = new HashSet<string>(
            officialCatalog.Where(entry => string.Equals(entry.Category, "email", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Id),
            StringComparer.OrdinalIgnoreCase);
        emailProviderIds.UnionWith(communityCatalog
            .Where(entry => string.Equals(entry.Category, "email", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Id));
        emailProviderIds.UnionWith(registry.ResolveAll<IEmailProviderPlugin>()
            .Select(provider => provider.Descriptor.Id));
        var officialNames = PluginCatalog.ReadOfficial(configuration)
            .ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var communityNames = PluginCatalog.ReadCommunity(configuration)
            .ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        var items = new List<SetupChecklistItem>();
        foreach (var id in required.Official.Concat(required.Community))
        {
            if (emailProviderIds.Contains(id)) continue;
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

    private async Task<IEnumerable<SetupChecklistItem>> EmailProviderItemsAsync(CancellationToken cancellationToken)
    {
        var providers = registry.ResolveAll<IEmailProviderPlugin>();
        var activeProvider = await emailSender.ConfiguredProviderAsync(cancellationToken);
        if (activeProvider is not null)
        {
            return [new SetupChecklistItem(
                Key: "email-provider",
                Label: $"System email — {activeProvider.Descriptor.Name}",
                Ok: true,
                Hint: $"{activeProvider.Descriptor.Name} is configured and ready for invitations and password resets.")];
        }

        var choices = PluginCatalog.ReadOfficial(configuration)
            .Where(entry => string.Equals(entry.Category, "email", StringComparison.OrdinalIgnoreCase))
            .Select(entry => (entry.Id, entry.Name))
            .Concat(PluginCatalog.ReadCommunity(configuration)
                .Where(entry => string.Equals(entry.Category, "email", StringComparison.OrdinalIgnoreCase))
                .Select(entry => (entry.Id, entry.Name)))
            .Concat(providers.Select(provider => (provider.Descriptor.Id, provider.Descriptor.Name)))
            .GroupBy(choice => choice.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (choices.Length == 0)
        {
            return [new SetupChecklistItem(
                Key: "email-provider",
                Label: "System email provider",
                Ok: false,
                Hint: "Enable an email provider under Settings → Plugins, then configure it under Settings → General.",
                Action: "plugins")];
        }

        return choices.Select(choice =>
        {
            var loaded = providers.Any(provider =>
                string.Equals(provider.Descriptor.Id, choice.Id, StringComparison.OrdinalIgnoreCase));
            var hint = loaded
                ? $"Configure {choice.Name} under Settings → General. Any single email provider is enough."
                : $"Enable {choice.Name} under Settings → Plugins, then configure it under Settings → General. Any single email provider is enough.";
            return new SetupChecklistItem(
                Key: $"email-provider:{choice.Id}",
                Label: $"{choice.Name} email provider",
                Ok: false,
                Hint: hint,
                Action: loaded ? "general" : "plugins");
        }).ToArray();
    }

    private async Task<bool> IsConfiguredAsync(IMediaPagerPlugin plugin, CancellationToken cancellationToken)
    {
        if (plugin is IEmailProviderPlugin email)
            return await email.IsConfiguredAsync(cancellationToken);
        if (plugin is IPluginSettingsSchema schema)
        {
            var settingsKey = plugin.Descriptor.Id;
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
public sealed record SetGitSshPrivateKeyRequest(string PrivateKey);
