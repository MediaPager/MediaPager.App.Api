using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaPager.App.Api.Services;
using MediaPager.App.Core.Services;

namespace MediaPager.App.Api.Controllers;

/// <summary>
/// Plugin discovery surface for the SPA. Drives the left-nav "Stream" node, its sub-tabs,
/// subtitle providers, and the data-driven settings UI. Serves browse/details/resolve for
/// stream-provider sources and the fused unified-search type-ahead.
/// </summary>
[ApiController]
[Route("sources")]
public sealed class SourcesController(
    PluginRegistry registry,
    IPluginSettingsStore pluginSettings,
    StreamSessionState sessions,
    IConfiguration configuration,
    PluginInstaller installer,
    PluginJobsService pluginJobs,
    GitHubPluginDiscovery pluginDiscovery) : ControllerBase
{
    // The plugin-settings namespace key is the descriptor id's last segment
    // ("mediapager.search.tmdb" → "tmdb") — the same convention PluginSettingsService uses.
    private static string PluginSettingsKey(IMediaPagerPlugin plugin) =>
        plugin.Descriptor.Id.Split('.')[^1];

    // Category for SPA grouping: the plugins.official.json list entry when the id is
    // cataloged (the single source of truth), else derived from the plugin's capabilities.
    private Dictionary<string, string> CatalogCategories()
    {
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in PluginCatalog.ReadOfficial(configuration))
            categories.TryAdd(entry.Id, entry.Category);
        return categories;
    }

    private static string CategoryFor(IMediaPagerPlugin plugin, Dictionary<string, string> catalogCategories)
    {
        if (catalogCategories.TryGetValue(plugin.Descriptor.Id, out var category))
            return category;
        if (plugin is IEmailProviderPlugin) return "email";
        if (plugin is ISubtitleProviderPlugin) return "subtitles";
        if (plugin is ISearchProviderPlugin) return "search";
        if (plugin is IMetadataProviderPlugin) return "metadata";
        if (plugin is IStreamProviderPlugin) return "stream";
        if (plugin is IPluginActions) return "actions";
        if (plugin is IPluginInterface) return "interface";
        return "other";
    }

    // GET /sources — catalog of every loaded plugin, its capabilities, the nav sources it
    // contributes, and subtitle providers. The SPA renders nav/settings generically from this.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        // Only ONLINE stream providers contribute Stream-screen sources: local ones obey
        // the catalogs instead and must not activate the Stream nav entry.
        var streamSources = registry.ResolveAll<IStreamProviderPlugin>()
            .Where(source => source.Mode == StreamProviderMode.Online)
            .SelectMany(source => source.Sources.Select(descriptor => new
            {
                key = descriptor.Key,
                name = descriptor.Name,
                kind = descriptor.Kind.ToSlug(),
                icon = descriptor.Icon,
                customUi = descriptor.CustomUi,
                uiUrl = descriptor.UiUrl,
                pluginId = source.Descriptor.Id,
            }))
            .OrderBy(source => source.kind)
            .ThenBy(source => source.name)
            .ToList();

        var subtitleProviders = registry.ResolveAll<ISubtitleProviderPlugin>()
            .Select(provider => new
            {
                key = provider.Descriptor.Id,
                name = provider.Descriptor.Name,
            })
            .OrderBy(provider => provider.name)
            .ToList();

        var catalogCategories = CatalogCategories();
        var plugins = await Task.WhenAll(registry.Plugins.Select(async plugin => new
        {
            id = plugin.Descriptor.Id,
            name = plugin.Descriptor.Name,
            version = plugin.Descriptor.Version,
            author = plugin.Descriptor.Author,
            official = !registry.IsCommunity(plugin),
            category = CategoryFor(plugin, catalogCategories),
            capabilities = new[]
            {
                plugin is IStreamProviderPlugin ? "stream" : null,
                plugin is IMetadataProviderPlugin ? "metadata" : null,
                plugin is ISearchProviderPlugin ? "search" : null,
                plugin is ISubtitleProviderPlugin ? "subtitles" : null,
                plugin is IEmailProviderPlugin ? "email" : null,
                plugin is IPluginActions ? "actions" : null,
                plugin is IPluginInterface ? "interface" : null,
            }.Where(capability => capability is not null).ToArray(),
            streamMode = plugin is IStreamProviderPlugin stream
                ? stream.Mode.ToString().ToLowerInvariant()
                : null,
            supportedCatalogTypes = plugin is IStreamProviderPlugin streamKinds
                ? streamKinds.SupportedKinds.Select(kind => kind.ToCatalogTypeSlug()).ToArray()
                : Array.Empty<string>(),
            settings = plugin is IPluginSettingsSchema schema
                ? schema.Settings.Select(setting => (object)new
                {
                    key = setting.Key,
                    label = setting.Label,
                    type = setting.Type.ToString().ToLowerInvariant(),
                    required = setting.Required,
                    secret = setting.Secret,
                    defaultValue = setting.Default,
                    catalogTypeSlug = setting.CatalogTypeSlug,
                }).ToArray()
                : Array.Empty<object>(),
            actions = await ActionPayloadAsync(plugin, cancellationToken),
            settingsNav = plugin is IPluginNavigation navigation && navigation.SettingsNav is { } settingsNav
                ? new { label = settingsNav.Label, icon = settingsNav.Icon, uiPath = settingsNav.UiPath }
                : null,
            mainNav = plugin is IPluginNavigation menuNavigation
                ? menuNavigation.MainNav.Select(entry => new { label = entry.Label, icon = entry.Icon, path = entry.Path }).ToArray()
                : Array.Empty<object>(),
        }));

        return Ok(new { plugins, sources = streamSources, subtitles = subtitleProviders });
    }

    // GET /sources/{key}/browse — one page of a stream source's catalog (poster-card grid).
    [HttpGet("{key}/browse")]
    public async Task<IActionResult> Browse(string key, [FromQuery] string? query, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        var (provider, _) = FindStreamSource(key);
        if (provider is null)
            return NotFound(Problem($"No stream source '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        try
        {
            var result = await provider.BrowseAsync(key, query, page, cancellationToken);
            return Ok(result);
        }
        catch (Exception exception)
        {
            return Problem($"Source '{key}' browse failed: {exception.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // GET /sources/{key}/details/{externalId} — full detail-sheet payload for one title.
    [HttpGet("{key}/details/{externalId}")]
    public async Task<IActionResult> Details(string key, string externalId, CancellationToken cancellationToken)
    {
        var (provider, _) = FindStreamSource(key);
        if (provider is null)
            return NotFound(Problem($"No stream source '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        try
        {
            var details = await provider.GetDetailsAsync(key, externalId, cancellationToken);
            if (details is null)
                return NotFound(Problem($"Source '{key}' has no title '{externalId}'.", statusCode: StatusCodes.Status404NotFound));
            return Ok(details);
        }
        catch (Exception exception)
        {
            return Problem($"Source '{key}' details failed: {exception.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // GET /sources/{key}/resolve/{externalId} — resolve a title to a playback session.
    // The plugin returns the raw upstream URI; the core SSRF-guards it, mints a short-lived
    // session, and the browser plays through /stream/{streamId}/root (playlists/segments
    // proxied anonymously, token never exposed to the media element).
    [HttpGet("{key}/resolve/{externalId}")]
    public async Task<IActionResult> Resolve(
        string key,
        string externalId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        CancellationToken cancellationToken)
    {
        var (provider, source) = FindStreamSource(key);
        if (provider is null || source is null)
            return NotFound(Problem($"No stream source '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        StreamResult? result;
        try
        {
            var request = new StreamResolveRequest(key, externalId, source.Kind, season, episode);
            result = await provider.ResolveAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            return Problem($"Source '{key}' resolve failed: {exception.Message}", statusCode: StatusCodes.Status502BadGateway);
        }

        if (result is null)
            return NotFound(Problem($"Source '{key}' could not resolve '{externalId}'.", statusCode: StatusCodes.Status404NotFound));

        if (!await StreamingService.IsSafeStreamUri(result.UpstreamUri, cancellationToken))
            return Problem("The resolved stream URL is not a safe HTTPS URL.", statusCode: StatusCodes.Status502BadGateway);

        var session = sessions.Create(result.UpstreamUri);
        return Ok(new { streamId = session.Id });
    }

    // GET /sources/search?q=&limit= — fused unified type-ahead across every registered
    // ISearchProviderPlugin. Hits carry the provider's SourceKey so the UI routes a pick
    // back to its source. (Core-local library search stays core-side; this is the
    // provider-contributed half.)
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 8, CancellationToken cancellationToken = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0)
            return Ok(new { hits = Array.Empty<SourceSearchHit>() });
        if (limit is < 1 or > 50) limit = 8;

        var searchRequest = new SearchRequest(query, limit, OnlineStreamKinds());
        var providers = registry.ResolveAll<ISearchProviderPlugin>();
        var tasks = providers.Select(async provider =>
        {
            try
            {
                var hits = await provider.SearchAsync(searchRequest, cancellationToken);
                return hits.Select(hit => new SourceSearchHit(
                    hit.Kind.ToSlug(),
                    hit.ExternalId,
                    hit.Title,
                    hit.SourceKey,
                    hit.Year,
                    hit.VoteAverage,
                    hit.Overview,
                    hit.ArtworkUrl)).ToList();
            }
            catch
            {
                // One provider failing must not sink the whole type-ahead.
                return new List<SourceSearchHit>();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        var fused = tasks
            .SelectMany(task => task.Result)
            .OrderByDescending(hit => hit.VoteAverage ?? 0)
            .Take(limit)
            .ToList();
        return Ok(new { hits = fused });
    }

    private sealed record SourceSearchHit(
        string Kind,
        string ExternalId,
        string Title,
        string SourceKey,
        int? Year,
        double? VoteAverage,
        string? Overview,
        string? ArtworkUrl);

    // GET /plugins/{key}/settings — stored values for the data-driven settings UI.
    // Secret fields are never read back: the response carries a boolean per secret field so
    // the UI can show a "set" placeholder and only write when the admin types a new value.
    [HttpGet("/plugins/{key}/settings")]
    [Authorize(Policy = "CanEditSettings")]
    public async Task<IActionResult> GetPluginSettings(string key, CancellationToken cancellationToken)
    {
        var (plugin, schema) = FindPluginWithSchema(key);
        if (plugin is null)
            return NotFound(Problem($"No plugin '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        var stored = await pluginSettings.GetAllAsync(PluginSettingsKey(plugin), cancellationToken);
        var values = new Dictionary<string, string?>();
        foreach (var field in schema)
        {
            if (field.Secret)
                values[field.Key] = stored.ContainsKey(field.Key) ? "" : null;
            else
                values[field.Key] = stored.TryGetValue(field.Key, out var value) ? value : field.Default;
        }

        return Ok(new { plugin = plugin.Descriptor.Id, values });
    }

    // PUT /plugins/{key}/settings — writes only keys declared by the plugin's schema.
    // A null/empty value for a secret field means "keep the stored value"; for non-secret
    // fields null deletes (resetting to the schema default).
    [HttpPut("/plugins/{key}/settings")]
    [Authorize(Policy = "CanEditSettings")]
    public async Task<IActionResult> PutPluginSettings(string key, [FromBody] Dictionary<string, string?> values, CancellationToken cancellationToken)
    {
        var (plugin, schema) = FindPluginWithSchema(key);
        if (plugin is null)
            return NotFound(Problem($"No plugin '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));

        var settingsKey = PluginSettingsKey(plugin);
        var stored = await pluginSettings.GetAllAsync(settingsKey, cancellationToken);
        var errors = new List<string>();
        foreach (var field in schema)
        {
            if (!field.Required)
                continue;
            if (field.Secret)
            {
                // Blank means "keep the stored value" — fine only when one exists.
                if (values.TryGetValue(field.Key, out var secretValue) && !string.IsNullOrWhiteSpace(secretValue))
                    continue;
                if (!stored.ContainsKey(field.Key))
                    errors.Add($"'{field.Label}' is required.");
            }
            else if (values.TryGetValue(field.Key, out var value) && string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"'{field.Label}' is required.");
            }
        }
        if (errors.Count > 0)
            return BadRequest(Problem(string.Join(' ', errors), statusCode: StatusCodes.Status400BadRequest));

        foreach (var field in schema)
        {
            if (!values.TryGetValue(field.Key, out var value))
                continue;
            if (field.Secret && string.IsNullOrWhiteSpace(value))
                continue;
            await pluginSettings.SetAsync(settingsKey, field.Key, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), cancellationToken);
        }

        return Ok(new { message = $"Saved {plugin.Descriptor.Name} settings." });
    }

    // GET /plugins — management list for the Settings > Plugins screen (admin:super only).
    // `plugins` are the loaded ones (each with whether it's official / active-required);
    // `available` lists official + recognized-community catalog entries that are NOT loaded
    // yet, with deployed/enabled flags so the SPA can offer "Turn on" / "Install".
    [HttpGet("/plugins")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ListPlugins(CancellationToken cancellationToken)
    {
        var catalogCategories = CatalogCategories();
        var required = PluginCatalog.ReadRequired(configuration).Official;
        var officialEntries = PluginCatalog.ReadOfficial(configuration);
        var communityEntries = PluginCatalog.ReadCommunity(configuration);
        var reposById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in officialEntries)
            reposById.TryAdd(entry.Id, entry.Repo);
        var communityById = new Dictionary<string, CommunityPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in communityEntries)
        {
            reposById.TryAdd(entry.Id, entry.Repo);
            communityById.TryAdd(entry.Id, entry);
        }
        bool IsDiscoverable(IMediaPagerPlugin plugin) =>
            !registry.IsCommunity(plugin) ||
            !communityById.TryGetValue(plugin.Descriptor.Id, out var entry) || entry.Discoverable;
        bool IsSystemRequired(IMediaPagerPlugin plugin) =>
            PluginCatalog.IsSystemRequired(configuration, plugin.Descriptor.Id);

        var plugins = await Task.WhenAll(registry.Plugins.Select(async plugin => new
        {
            id = plugin.Descriptor.Id,
            name = plugin.Descriptor.Name,
            version = plugin.Descriptor.Version,
            author = plugin.Descriptor.Author,
            description = plugin.Descriptor.Description,
            official = !registry.IsCommunity(plugin),
            enabled = registry.IsCommunity(plugin) || required.Contains(plugin.Descriptor.Id),
            locked = IsSystemRequired(plugin) || registry.GetDependents(plugin.Descriptor.Id).Count > 0,
            lockReason = IsSystemRequired(plugin)
                ? "Required by the system."
                : registry.GetDependents(plugin.Descriptor.Id) is { Count: > 0 } requiredBy
                    ? $"Required by: {string.Join(", ", requiredBy.Select(dependent => dependent.Descriptor.Name))}."
                    : null,
            repo = reposById.GetValueOrDefault(plugin.Descriptor.Id),
            discoverable = IsDiscoverable(plugin),
            requires = registry.GetRequirements(plugin.Descriptor.Id),
            category = CategoryFor(plugin, catalogCategories),
            capabilities = new[]
            {
                plugin is IStreamProviderPlugin ? "stream" : null,
                plugin is IMetadataProviderPlugin ? "metadata" : null,
                plugin is ISearchProviderPlugin ? "search" : null,
                plugin is ISubtitleProviderPlugin ? "subtitles" : null,
                plugin is IEmailProviderPlugin ? "email" : null,
                plugin is IPluginActions ? "actions" : null,
                plugin is IPluginInterface ? "interface" : null,
            }.Where(capability => capability is not null).ToArray(),
            streamMode = plugin is IStreamProviderPlugin stream
                ? stream.Mode.ToString().ToLowerInvariant()
                : null,
            supportedCatalogTypes = plugin is IStreamProviderPlugin streamKinds
                ? streamKinds.SupportedKinds.Select(kind => kind.ToCatalogTypeSlug()).ToArray()
                : Array.Empty<string>(),
            settings = plugin is IPluginSettingsSchema schema
                ? schema.Settings.Select(setting => (object)new
                {
                    key = setting.Key,
                    label = setting.Label,
                    type = setting.Type.ToString().ToLowerInvariant(),
                    required = setting.Required,
                    secret = setting.Secret,
                    defaultValue = setting.Default,
                    catalogTypeSlug = setting.CatalogTypeSlug,
                }).ToArray()
                : Array.Empty<object>(),
            actions = await ActionPayloadAsync(plugin, cancellationToken),
            settingsNav = plugin is IPluginNavigation navigation && navigation.SettingsNav is { } settingsNav
                ? new { label = settingsNav.Label, icon = settingsNav.Icon, uiPath = settingsNav.UiPath }
                : null,
            mainNav = plugin is IPluginNavigation menuNavigation
                ? menuNavigation.MainNav.Select(entry => new { label = entry.Label, icon = entry.Icon, path = entry.Path }).ToArray()
                : Array.Empty<object>(),
        }));

        var loadedIds = registry.Plugins
            .Select(plugin => plugin.Descriptor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var officialDirectory = PluginDirectories.Official(configuration);
        var communityDirectory = PluginDirectories.Community(configuration);
        var available = new List<object>();
        foreach (var entry in officialEntries)
        {
            if (loadedIds.Contains(entry.Id)) continue;
            var assembly = entry.Assembly ?? PluginInstaller.DeriveAssemblyName(entry.Repo);
            available.Add(new
            {
                id = entry.Id,
                name = entry.Name,
                category = entry.Category,
                repo = entry.Repo,
                author = (string?)null,
                official = true,
                discoverable = true,
                deployed = System.IO.File.Exists(Path.Combine(officialDirectory, assembly, $"{assembly}.dll")),
                enabled = required.Contains(entry.Id),
                locked = entry.SystemRequired,
                lockReason = entry.SystemRequired ? "Required by the system." : null,
            });
        }
        foreach (var entry in communityEntries)
        {
            if (loadedIds.Contains(entry.Id)) continue;
            var assembly = entry.Assembly ?? PluginInstaller.DeriveAssemblyName(entry.Repo);
            available.Add(new
            {
                id = entry.Id,
                name = entry.Name,
                category = entry.Category ?? "",
                repo = entry.Repo,
                author = entry.Author,
                official = false,
                discoverable = entry.Discoverable,
                deployed = System.IO.File.Exists(Path.Combine(communityDirectory, assembly, $"{assembly}.dll")),
                enabled = false,
                locked = entry.SystemRequired,
                lockReason = entry.SystemRequired ? "Required by the system." : null,
            });
        }

        return Ok(new { plugins, available });
    }

    // GET /plugins/official — the official-plugin list (plugins.official.json). Gives the SPA
    // links it can offer at any time (e.g. the other mail providers in Settings → Email)
    // plus whether each entry is loaded right now. Settings-editors only: it is install
    // guidance, not a public surface.
    [HttpGet("/plugins/official")]
    [Authorize(Policy = "CanEditSettings")]
    public IActionResult OfficialPlugins()
    {
        var entries = PluginCatalog.ReadOfficial(configuration);
        var loadedIds = registry.Plugins
            .Select(plugin => plugin.Descriptor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            plugins = entries.Select(entry => new
            {
                id = entry.Id,
                name = entry.Name,
                category = entry.Category,
                repo = entry.Repo,
                installed = loadedIds.Contains(entry.Id),
            }).ToList(),
        });
    }

    // POST /plugins/{key}/install — deploy a listed plugin (official list → official
    // directory, recognized community list → community directory) from its source repo
    // through the shared pipeline and register it live. The directories are the load
    // origin: what lands in official is official, community stays removable.
    [HttpPost("/plugins/{key}/install")]
    [Authorize(Policy = "CanEditSettings")]
    public async Task<IActionResult> InstallPlugin(string key, CancellationToken cancellationToken)
    {
        PluginInstallResult result;
        try
        {
            result = await installer.InstallAsync(key, cancellationToken);
        }
        catch (Exception exception)
        {
            return Problem($"Install of '{key}' failed: {exception.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }

        if (!result.Found)
            return NotFound(Problem($"'{key}' is not in the official or recognized-community list.",
                statusCode: StatusCodes.Status404NotFound));

        return Ok(new
        {
            id = result.Id,
            assembly = result.Assembly,
            path = result.Path,
            loaded = result.Loaded,
        });
    }

    // POST /plugins/jobs — start a background deploy job: { key } turns on (official,
    // deployed on demand) or installs (community) a listed plugin; { repo } adds an
    // arbitrary https repo as a community plugin, optionally on { branch } (null = the
    // repo's default). Returns a jobId the SPA polls.
    [HttpPost("/plugins/jobs")]
    [Authorize(Policy = "SuperAdmin")]
    public IActionResult StartPluginJob([FromBody] PluginJobRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Key) == string.IsNullOrWhiteSpace(request.Repo))
            return BadRequest(Problem("Send exactly one of 'key' (a listed plugin id) or 'repo' (a repository URL).",
                statusCode: StatusCodes.Status400BadRequest));
        if (!string.IsNullOrWhiteSpace(request.Branch) && string.IsNullOrWhiteSpace(request.Repo))
            return BadRequest(Problem("'branch' only applies when adding a repo.",
                statusCode: StatusCodes.Status400BadRequest));
        try
        {
            var jobId = string.IsNullOrWhiteSpace(request.Key)
                ? pluginJobs.StartByRepo(request.Repo!, request.Branch)
                : pluginJobs.StartByKey(request.Key);
            return Accepted(new { jobId });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(exception.Message, statusCode: StatusCodes.Status404NotFound));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // GET /plugins/branches?repo= — the Add-plugin branch picker: every branch plus the
    // repo's default (remote HEAD, else main/master).
    [HttpGet("/plugins/branches")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ListPluginBranches([FromQuery] string? repo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return BadRequest(Problem("A 'repo' query parameter is required.",
                statusCode: StatusCodes.Status400BadRequest));
        try
        {
            var (branches, defaultBranch) = await pluginJobs.ListBranchesAsync(repo, cancellationToken);
            return Ok(new { branches, @default = defaultBranch });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest));
        }
    }

    // POST /plugins/{key}/enable — turn a listed plugin on without redeploying when its
    // files are still deployed (community clears the .disabled marker, official is added
    // to Required:Official — instant). When nothing is deployed yet it returns a jobId
    // the SPA polls, same as POST /plugins/jobs {key}.
    [HttpPost("/plugins/{key}/enable")]
    [Authorize(Policy = "SuperAdmin")]
    public IActionResult EnablePlugin(string key)
    {
        try
        {
            var outcome = pluginJobs.Enable(key);
            return outcome.JobId is not null
                ? Accepted(new { jobId = outcome.JobId })
                : Ok(new { message = outcome.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(exception.Message, statusCode: StatusCodes.Status404NotFound));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // GET /plugins/discover?q= — community plugins on GitHub matching the query
    // (Settings > Plugins > Community search box). Official plugins are never searched:
    // they always come from plugins.official.json for the branch you are on.
    [HttpGet("/plugins/discover")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> DiscoverCommunityPlugins([FromQuery] string? q, CancellationToken cancellationToken)
    {
        try
        {
            var results = await pluginDiscovery.GetCommunityListAsync(q, cancellationToken);
            return Ok(new { results });
        }
        catch (HttpRequestException exception)
        {
            return BadRequest(Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest));
        }
    }

    // GET /plugins/jobs/{id} — poll one job's stage/percent/log while it runs.
    [HttpGet("/plugins/jobs/{id}")]
    [Authorize(Policy = "SuperAdmin")]
    public IActionResult GetPluginJob(string id) =>
        pluginJobs.Get(id) is { } job
            ? Ok(job)
            : NotFound(Problem($"No plugin job '{id}'.", statusCode: StatusCodes.Status404NotFound));

    // POST /plugins/{key}/disable — turn an official plugin off: remove it from
    // Plugins:Required:Official (appsettings.json) and unregister it live. Community
    // plugins are uninstalled (DELETE) instead of disabled.
    [HttpPost("/plugins/{key}/disable")]
    [Authorize(Policy = "SuperAdmin")]
    public IActionResult DisablePlugin(string key)
    {
        try
        {
            return Ok(new { message = pluginJobs.Disable(key) });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(exception.Message, statusCode: StatusCodes.Status404NotFound));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem(exception.Message, statusCode: StatusCodes.Status409Conflict));
        }
    }

    // DELETE /plugins/{key} — uninstall a community plugin: unload it from the registry and
    // drop a `.disabled` marker into its install folder so it stays off on the next boot.
    // Official plugins are locked and cannot be uninstalled.
    [HttpDelete("/plugins/{key}")]
    [Authorize(Policy = "SuperAdmin")]
    public IActionResult UninstallPlugin(string key, [FromQuery] bool removeDependencies = false)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(PluginSettingsKey(candidate), key, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return NotFound(Problem($"No plugin '{key}' is loaded.", statusCode: StatusCodes.Status404NotFound));
        if (!registry.IsCommunity(plugin))
            return BadRequest(Problem($"'{plugin.Descriptor.Name}' is an official plugin and cannot be uninstalled.", statusCode: StatusCodes.Status400BadRequest));
        if (PluginCatalog.IsSystemRequired(configuration, plugin.Descriptor.Id))
            return Conflict(Problem($"'{plugin.Descriptor.Name}' is required by the system and cannot be uninstalled.",
                statusCode: StatusCodes.Status409Conflict));

        var dependents = registry.GetDependents(plugin.Descriptor.Id);
        if (dependents.Count > 0)
            return Conflict(Problem(
                $"Cannot uninstall '{plugin.Descriptor.Name}' while required by: "
                + string.Join(", ", dependents.Select(dependent => dependent.Descriptor.Name))
                + ". Uninstall its dependents first.",
                statusCode: StatusCodes.Status409Conflict));

        var dependenciesToRemove = removeDependencies
            ? registry.GetUnusedDependencies(plugin.Descriptor.Id)
            : [];
        if (!registry.Remove(plugin.Descriptor.Id))
            return Conflict(Problem($"'{plugin.Descriptor.Name}' is still required by another loaded plugin.",
                statusCode: StatusCodes.Status409Conflict));
        MarkDisabled(plugin);

        var removedDependencies = new List<string>();
        foreach (var dependency in dependenciesToRemove)
        {
            if (registry.GetDependents(dependency.Descriptor.Id).Count > 0 ||
                !registry.Remove(dependency.Descriptor.Id))
                continue;
            MarkDisabled(dependency);
            removedDependencies.Add(dependency.Descriptor.Name);
        }

        var dependencyNote = removedDependencies.Count > 0
            ? $" Removed unused dependencies: {string.Join(", ", removedDependencies)}."
            : "";
        return Ok(new
        {
            message = $"Uninstalled {plugin.Descriptor.Name}. It will stay off after a restart.{dependencyNote}",
            removedDependencies,
        });
    }

    private static void MarkDisabled(IMediaPagerPlugin plugin)
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(plugin.GetType().Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDir) && Directory.Exists(assemblyDir))
                System.IO.File.WriteAllText(Path.Combine(assemblyDir, ".disabled"), $"disabled {DateTimeOffset.UtcNow:O}\n");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[plugins] could not write .disabled marker for {plugin.Descriptor.Id}: {exception.Message}");
        }
    }

    // GET /plugins/{key}/ui/{**path} — a plugin's custom UI assets. Plugins that declare a
    // source with CustomUi = true ship a "ui" folder next to their assembly; the SPA embeds
    // it in a sandboxed iframe (the token never crosses the bridge). Static content only.
    [HttpGet("/plugins/{key}/ui/{**path}")]
    [AllowAnonymous]
    public IActionResult PluginUi(string key, string? path)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(PluginSettingsKey(candidate), key, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return NotFound();

        var uiRoot = Path.Combine(Path.GetDirectoryName(plugin.GetType().Assembly.Location) ?? "", "ui");
        if (!Directory.Exists(uiRoot))
            return NotFound();

        var relative = string.IsNullOrWhiteSpace(path) ? "index.html" : path;
        var fullPath = Path.GetFullPath(Path.Combine(uiRoot, relative));
        // Containment: a crafted path must never escape the plugin's ui folder.
        if (!fullPath.StartsWith(Path.GetFullPath(uiRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(fullPath, Path.GetFullPath(uiRoot), StringComparison.Ordinal))
            return NotFound();
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
        return PhysicalFile(fullPath, contentType);
    }

    private (IStreamProviderPlugin? Provider, SourceDescriptor? Source) FindStreamSource(string key)
    {
        foreach (var provider in registry.ResolveAll<IStreamProviderPlugin>())
        {
            var source = provider.Sources.FirstOrDefault(descriptor =>
                string.Equals(descriptor.Key, key, StringComparison.OrdinalIgnoreCase));
            if (source is not null)
                return (provider, source);
        }
        return (null, null);
    }

    private MediaKind[] OnlineStreamKinds() => registry.ResolveAll<IStreamProviderPlugin>()
        .Where(provider => provider.Mode == StreamProviderMode.Online)
        .SelectMany(provider => provider.SupportedKinds)
        .Distinct()
        .ToArray();

    private static async Task<object[]> ActionPayloadAsync(IMediaPagerPlugin plugin, CancellationToken cancellationToken) =>
        plugin is IPluginActions provider
        ? (await provider.GetActionsAsync(cancellationToken)).Select(action => (object)new
        {
            actionId = action.ActionId,
            label = action.Label,
            icon = action.Icon,
            surface = action.Surface.ToString(),
            position = action.Position.ToString(),
            order = action.Order,
            kinds = action.Kinds?.Select(kind => kind.ToString().ToLowerInvariant()).ToArray(),
            click = action.Click.ToString(),
            uiPath = action.UiPath,
            scope = action.Scope.ToString(),
            hostActionId = action.HostActionId,
            enabled = action.Enabled,
            availabilityMessage = action.AvailabilityMessage,
            availabilityPluginId = action.AvailabilityPluginId,
        }).ToArray()
        : [];

    private (IMediaPagerPlugin? Plugin, IReadOnlyList<PluginSettingDefinition> Schema) FindPluginWithSchema(string key)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(PluginSettingsKey(candidate), key, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return (null, []);
        var schema = plugin is IPluginSettingsSchema settingsSchema ? settingsSchema.Settings : [];
        return (plugin, schema);
    }
}
