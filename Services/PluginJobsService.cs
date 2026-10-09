using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace MediaPager.App.Api.Services;

/// <summary>Snapshot of one install/enable/add job, polled by the SPA while the pipeline
/// runs (progress bar + live status log in Settings → Plugins).</summary>
public sealed record PluginJobSnapshot(
    string Id,
    string? Key,
    string? Repo,
    string? Branch,
    string Kind,
    string Title,
    string Stage,
    int Percent,
    bool Done,
    bool Failed,
    string? Error,
    string? PluginId,
    IReadOnlyList<string> Log);

/// <summary>
/// Runs plugin deployment as tracked background jobs — the one pipeline behind
/// "Turn on" (official, incl. deploy-when-missing), "Install" (community), and
/// "Add plugin" (a bare GitHub repo URL): clone → publish → verify (load + official-id
/// reservation) → write configuration (appsettings Required:Official / plugins.community.json)
/// → register live. Every stage is reported into an in-memory job the SPA polls.
/// Singleton: builds plugins against the root service provider, like PluginInstaller.
/// </summary>
public sealed class PluginJobsService(
    IServiceProvider services,
    PluginRegistry registry,
    PluginDeployer deployer,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    private readonly ConcurrentDictionary<string, PluginJobState> _jobs = new();
    // Serialized writes to the JSON config files (appsettings.json / plugins.community.json).
    private static readonly SemaphoreSlim FileWriteLock = new(1, 1);

    public PluginJobSnapshot? Get(string id) =>
        _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    /// <summary>Start a job for a listed plugin (official or community) by id: deploy if
    /// it isn't deployed yet, load it, and — for officials — add it to
    /// Plugins:Required:Official so it stays on across restarts.</summary>
    public string StartByKey(string key)
    {
        var official = PluginCatalog.ReadOfficial(configuration).FirstOrDefault(entry =>
            string.Equals(entry.Id, key, StringComparison.OrdinalIgnoreCase));
        var community = official is null
            ? PluginCatalog.ReadCommunity(configuration).FirstOrDefault(entry =>
                string.Equals(entry.Id, key, StringComparison.OrdinalIgnoreCase))
            : null;
        if (official is null && community is null)
            throw new KeyNotFoundException($"'{key}' is not in the official or community list.");
        if (registry.Plugins.Any(plugin =>
                string.Equals(plugin.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"'{official?.Name ?? community!.Name}' is already active.");

        var plan = official is not null
            ? new JobPlan(
                Kind: "enable",
                Title: $"Turn on {official.Name}",
                Id: official.Id,
                Name: official.Name,
                Repo: official.Repo,
                Assembly: official.Assembly ?? PluginInstaller.DeriveAssemblyName(official.Repo),
                Directory: PluginDirectories.Official(configuration),
                Official: true,
                Custom: false)
            : new JobPlan(
                Kind: "install",
                Title: $"Install {community!.Name}",
                Id: community.Id,
                Name: community.Name,
                Repo: community.Repo,
                Assembly: community.Assembly ?? PluginInstaller.DeriveAssemblyName(community.Repo),
                Directory: PluginDirectories.Community(configuration),
                Official: false,
                Custom: false);
        return Start(plan, key, plan.Repo);
    }

    /// <summary>Start a job for an arbitrary plugin repo URL: deploy into the community
    /// directory (from <paramref name="branch"/>, or the repo's default when null),
    /// verify it loads (an official id is rejected), then append it to
    /// plugins.community.json so it is recognized from then on.</summary>
    public string StartByRepo(string repo, string? branch = null)
    {
        repo = ValidateRepoUrl(repo);
        branch = string.IsNullOrWhiteSpace(branch) ? null : ValidateBranch(branch);

        var official = PluginCatalog.ReadOfficial(configuration).FirstOrDefault(entry =>
            string.Equals(NormalizeRepo(entry.Repo), NormalizeRepo(repo), StringComparison.OrdinalIgnoreCase));
        if (official is not null)
            throw new InvalidOperationException(
                $"That repo is the official plugin '{official.Name}' — turn it on from its row instead.");
        var community = PluginCatalog.ReadCommunity(configuration).FirstOrDefault(entry =>
            string.Equals(NormalizeRepo(entry.Repo), NormalizeRepo(repo), StringComparison.OrdinalIgnoreCase));
        if (community is not null)
            throw new InvalidOperationException(
                $"That repo is already listed as '{community.Name}' — install it from its row instead.");

        var plan = new JobPlan(
            Kind: "add",
            Title: "Add plugin",
            Id: null,
            Name: "plugin",
            Repo: repo,
            Assembly: null,
            Directory: PluginDirectories.Community(configuration),
            Official: false,
            Custom: true,
            Branch: branch);
        return Start(plan, null, repo);
    }

    /// <summary>List a repo's branches and its default branch for the Add-plugin branch
    /// picker (same URL rules as StartByRepo).</summary>
    public async Task<(IReadOnlyList<string> Branches, string? Default)> ListBranchesAsync(
        string repo, CancellationToken cancellationToken = default)
        => await deployer.ListBranchesAsync(ValidateRepoUrl(repo), cancellationToken);

    // Transport rules shared by the job pipeline and the branch listing: no shell
    // metacharacters or whitespace in any form, https without embedded credentials, or
    // ssh (git@host:path / ssh://…) — the deployer rewrites ssh to https for public
    // repos, or honors MEDIAPAGER_GIT_SSH_PRIVATE_KEY_PATH for private ones.
    private static string ValidateRepoUrl(string repo)
    {
        repo = repo.Trim();
        if (repo.Length is 0 or > 400 || repo.StartsWith('-') ||
            repo.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '`' or '$'))
            throw new ArgumentException("The repository URL contains characters that are not allowed.");

        if (GitRepoUrl.IsSshLike(repo))
        {
            if (repo.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                repo.StartsWith("git+ssh://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(repo, UriKind.Absolute, out var sshUri) || string.IsNullOrWhiteSpace(sshUri.Host))
                    throw new ArgumentException(
                        "Send a valid repository URL, e.g. https://github.com/owner/repo or git@github.com:owner/repo.");
                if (sshUri.UserInfo.Contains(':'))
                    throw new ArgumentException("The repository URL must not contain a password.");
            }
            return repo;
        }

        if (!Uri.TryCreate(repo, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException(
                "Send a valid repository URL, e.g. https://github.com/owner/repo (ssh forms like git@github.com:owner/repo work too).");
        if (uri.UserInfo.Length > 0)
            throw new ArgumentException("The repository URL must not contain credentials.");
        return repo;
    }

    // Branch names: git's own charset (no whitespace/shell chars), no option-looking
    // leading dash, no '..' traversal, no lock-suffix refs.
    private static string ValidateBranch(string branch)
    {
        branch = branch.Trim();
        if (branch.Length is 0 or > 120)
            throw new ArgumentException("The branch name is empty or too long.");
        if (branch.StartsWith('-') || branch.StartsWith('/') || branch.EndsWith('/') ||
            branch.Contains("..") || branch.Contains("//") || branch.Contains("@{") ||
            branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            branch.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or '@' or '+')))
            throw new ArgumentException($"'{branch}' is not a valid branch name.");
        return branch;
    }

    /// <summary>Turn an official plugin off: drop it from Plugins:Required:Official in
    /// appsettings.json and unregister it. Files stay put; the next boot skips it too.</summary>
    public string Disable(string key)
    {
        var plugin = registry.Plugins.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            throw new KeyNotFoundException($"No plugin '{key}' is active.");
        if (registry.IsCommunity(plugin))
            throw new InvalidOperationException(
                $"'{plugin.Descriptor.Name}' is a community plugin — uninstall it instead.");
        if (PluginCatalog.IsSystemRequired(configuration, plugin.Descriptor.Id))
            throw new InvalidOperationException(
                $"'{plugin.Descriptor.Name}' is required by the system and cannot be turned off.");

        var dependents = registry.GetDependents(plugin.Descriptor.Id);
        if (dependents.Count > 0)
            throw new InvalidOperationException(
                $"Cannot turn off '{plugin.Descriptor.Name}' while required by: "
                + string.Join(", ", dependents.Select(dependent => dependent.Descriptor.Name))
                + ". Turn off its dependents first.");

        SetOfficialRequired(plugin.Descriptor.Id, false);
        registry.Unload(plugin.Descriptor.Id);
        return $"{plugin.Descriptor.Name} is off. It stays off after a restart until you turn it on again.";
    }

    /// <summary>Turn a listed-but-inactive plugin on. When its files are already deployed
    /// it comes up instantly with a short-circuiting outcome: community clears the
    /// <c>.disabled</c> marker, official is added to Required:Official, both reload in
    /// place — no network, no rebuild. When nothing is deployed yet it returns a jobId the
    /// SPA polls (the same deploy path StartByKey uses).</summary>
    public PluginEnableOutcome Enable(string key)
    {
        var official = PluginCatalog.ReadOfficial(configuration).FirstOrDefault(entry =>
            string.Equals(entry.Id, key, StringComparison.OrdinalIgnoreCase));
        var community = official is null
            ? PluginCatalog.ReadCommunity(configuration).FirstOrDefault(entry =>
                string.Equals(entry.Id, key, StringComparison.OrdinalIgnoreCase))
            : null;
        if (official is null && community is null)
            throw new KeyNotFoundException($"'{key}' is not in the official or community list.");
        if (registry.Plugins.Any(plugin =>
                string.Equals(plugin.Descriptor.Id, key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"'{official?.Name ?? community!.Name}' is already active.");

        var (repo, catalogAssembly) = official is { } officialEntry
            ? (officialEntry.Repo, officialEntry.Assembly)
            : (community!.Repo, community!.Assembly);
        var assembly = catalogAssembly ?? PluginInstaller.DeriveAssemblyName(repo);
        var directory = official is not null
            ? PluginDirectories.Official(configuration)
            : PluginDirectories.Community(configuration);
        if (!File.Exists(Path.Combine(directory, assembly, $"{assembly}.dll")))
            return new PluginEnableOutcome(StartByKey(key), null);

        var folder = Path.Combine(directory, assembly);
        var manifest = ReadInstalledManifest(folder, assembly);
        if ((manifest?.Requires ?? []).Any(id => registry.GetPlugin(id) is null))
            return new PluginEnableOutcome(StartByKey(key), null);

        var marker = Path.Combine(folder, ".disabled");
        if (File.Exists(marker))
            File.Delete(marker);

        var reservedIds = new HashSet<string>(
            PluginCatalog.ReadOfficial(configuration).Select(entry => entry.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var loaded in registry.Plugins)
            reservedIds.Add(loaded.Descriptor.Id);
        var loadedPlugins = PluginLoader.LoadFolder(services, folder, official is not null, reservedIds);
        if (loadedPlugins.Count == 0)
            throw new InvalidOperationException(
                "The plugin could not be loaded — its id may collide with an official plugin, "
                + "or the assembly is broken (details are in the server log).");

        if (official is not null)
            SetOfficialRequired(official.Id, true);
        foreach (var plugin in loadedPlugins)
        {
            if (!registry.Plugins.Any(existing =>
                    string.Equals(existing.Descriptor.Id, plugin.Descriptor.Id, StringComparison.OrdinalIgnoreCase)))
                registry.Add(plugin, community: official is null);
        }
        return new PluginEnableOutcome(null, $"{loadedPlugins[0].Descriptor.Name} is active.");
    }

    /// <summary>Add or remove an official id in Plugins:Required:Official (appsettings.json),
    /// then reload configuration so the checklist and catalog read see it immediately.</summary>
    public void SetOfficialRequired(string id, bool required)
        => EditJsonFile("appsettings.json", AppSettingsPath(), "Plugins", "Required", "Official", array =>
        {
            var index = IndexOf(array, id);
            if (required && index < 0) array.Add(id);
            if (!required && index >= 0) array.RemoveAt(index);
        });

    private string AppSettingsPath()
    {
        var path = Path.Combine(environment.ContentRootPath, "appsettings.json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Could not find appsettings.json at {path}.");
        return path;
    }

    private string Start(JobPlan plan, string? key, string? repo)
    {
        PruneJobs();
        var job = new PluginJobState
        {
            Key = key,
            Repo = repo,
            Branch = plan.Branch,
            Kind = plan.Kind,
            Title = plan.Title,
        };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, plan));
        return job.Id;
    }

    private async Task RunAsync(PluginJobState job, JobPlan plan)
    {
        string? deployedFolder = null;
        string? installedPluginId = plan.Id;
        try
        {
            job.Update(2, "queued", $"{job.Title} started.");
            string assembly;
            PluginManifest? manifest;
            if (plan.AlreadyDeployed())
            {
                assembly = plan.Assembly!;
                manifest = ReadInstalledManifest(Path.Combine(plan.Directory, assembly), assembly);
                job.Update(88, "deploying", "Already deployed — skipping the build.");
            }
            else
            {
                var progress = new Progress<PluginDeployProgress>(step =>
                    job.Update(step.Percent, step.Stage, step.Message));
                var outcome = await deployer.InstallAsync(
                    plan.Repo!, plan.Assembly, plan.Directory, plan.Branch, progress,
                    CancellationToken.None);
                assembly = outcome.Assembly;
                manifest = outcome.Manifest;
            }

            var folder = Path.Combine(plan.Directory, assembly);
            deployedFolder = folder;
            var marker = Path.Combine(folder, ".disabled");
            if (File.Exists(marker))
            {
                File.Delete(marker);
                job.Update(90, "deploying", "Cleared the uninstall marker.");
            }

            manifest ??= ReadInstalledManifest(folder, assembly);
            if (manifest is not null)
                await EnsureRequiredPluginsAsync(manifest, job, new HashSet<string>([manifest.Id], StringComparer.OrdinalIgnoreCase));

            job.Update(92, "loading", "Verifying the plugin (load + id checks)…");
            var reservedIds = new HashSet<string>(
                PluginCatalog.ReadOfficial(configuration).Select(entry => entry.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var loaded in registry.Plugins)
                reservedIds.Add(loaded.Descriptor.Id);

            var missingRequirements = (manifest?.Requires ?? [])
                .Where(id => registry.GetPlugin(id) is null)
                .ToList();
            if (missingRequirements.Count > 0)
            {
                if (plan.Custom) TryDeleteDirectory(folder);
                throw new InvalidOperationException(
                    $"Missing required plugin(s): {string.Join(", ", missingRequirements)}. Install those plugins first.");
            }

            var loadedPlugins = PluginLoader.LoadFolder(services, folder, plan.Official, reservedIds);
            if (loadedPlugins.Count == 0)
            {
                if (plan.Custom)
                    TryDeleteDirectory(folder);
                throw new InvalidOperationException(
                    "Verification failed: the plugin could not be loaded — its id may collide with an official plugin, "
                    + "or the assembly is broken (details are in the server log).");
            }
            installedPluginId = loadedPlugins[0].Descriptor.Id;

            if (manifest is not null)
            {
                try
                {
                    VerifyManifestAgainstCode(manifest, loadedPlugins[0]);
                }
                catch
                {
                    // Roll the freshly deployed folder back so a retry re-validates from
                    // scratch instead of slipping through the already-deployed fast path.
                    TryDeleteDirectory(folder);
                    throw;
                }
            }

            if (plan.Official)
            {
                job.Update(97, "configuring", $"Adding {plan.Id} to Plugins:Required:Official in appsettings.json…");
                SetOfficialRequired(plan.Id!, true);
            }
            else if (plan.Custom)
            {
                var descriptor = loadedPlugins[0].Descriptor;
                job.Update(97, "configuring", $"Adding {descriptor.Id} to plugins.community.json…");
                AppendCommunityEntry(descriptor, DeriveCategory(loadedPlugins[0]), plan.Repo!, assembly, manifest?.Discoverable ?? true);
            }

            foreach (var plugin in loadedPlugins)
            {
                if (!registry.Plugins.Any(existing =>
                        string.Equals(existing.Descriptor.Id, plugin.Descriptor.Id, StringComparison.OrdinalIgnoreCase)))
                    registry.Add(plugin, community: !plan.Official);
            }

            var registered = loadedPlugins[0].Descriptor;
            job.Update(100, "done", $"{registered.Name} is active.");
            job.Complete(registered.Id);
        }
        catch (Exception exception)
        {
            if (plan.Custom && deployedFolder is not null &&
                (installedPluginId is null || registry.GetPlugin(installedPluginId) is null))
                TryDeleteDirectory(deployedFolder);
            job.Fail(exception.Message);
        }
    }

    private async Task EnsureRequiredPluginsAsync(
        PluginManifest manifest,
        PluginJobState job,
        HashSet<string> resolving)
    {
        foreach (var requiredId in manifest.Requires ?? [])
        {
            if (registry.GetPlugin(requiredId) is not null) continue;
            if (!resolving.Add(requiredId))
                throw new InvalidOperationException($"Plugin dependency cycle detected at '{requiredId}'.");

            try
            {
                await EnsurePluginLoadedAsync(
                    requiredId,
                    manifest.RequirementRepos?.GetValueOrDefault(requiredId),
                    job,
                    resolving);
            }
            finally
            {
                resolving.Remove(requiredId);
            }
        }
    }

    private async Task EnsurePluginLoadedAsync(
        string pluginId,
        string? requiredRepo,
        PluginJobState job,
        HashSet<string> resolving)
    {
        if (registry.GetPlugin(pluginId) is not null) return;

        var official = PluginCatalog.ReadOfficial(configuration).FirstOrDefault(entry =>
            string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var communityEntry = PluginCatalog.ReadCommunity(configuration).FirstOrDefault(entry =>
            string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var repo = official?.Repo ?? communityEntry?.Repo ?? requiredRepo;
        if (string.IsNullOrWhiteSpace(repo))
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' is required but no repository was declared. Add its repo URL to the manifest requirement.");

        var assembly = official?.Assembly ?? communityEntry?.Assembly ?? PluginInstaller.DeriveAssemblyName(repo);
        var directory = official is not null
            ? PluginDirectories.Official(configuration)
            : PluginDirectories.Community(configuration);
        var folder = Path.Combine(directory, assembly);
        var assemblyFile = Path.Combine(folder, $"{assembly}.dll");
        PluginManifest? manifest = File.Exists(assemblyFile)
            ? ReadInstalledManifest(folder, assembly)
            : null;

        if (manifest is null)
        {
            job.Update(91, "dependencies", $"Deploying required plugin {pluginId}…");
            var outcome = await deployer.InstallAsync(repo, assembly, directory, cancellationToken: CancellationToken.None);
            assembly = outcome.Assembly;
            folder = Path.Combine(directory, assembly);
            manifest = outcome.Manifest;
        }

        if (manifest is null)
            throw new InvalidOperationException($"Required plugin '{pluginId}' has no valid manifest.");
        if (!string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Dependency repo '{repo}' declares plugin id '{manifest.Id}', not required id '{pluginId}'.");

        await EnsureRequiredPluginsAsync(manifest, job, resolving);

        var marker = Path.Combine(folder, ".disabled");
        if (File.Exists(marker)) File.Delete(marker);

        job.Update(91, "dependencies", $"Loading required plugin {pluginId}…");
        var reservedIds = new HashSet<string>(
            PluginCatalog.ReadOfficial(configuration).Select(entry => entry.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var loaded in registry.Plugins)
            reservedIds.Add(loaded.Descriptor.Id);
        var loadedPlugins = PluginLoader.LoadFolder(services, folder, official is not null, reservedIds);
        if (loadedPlugins.Count == 0)
            throw new InvalidOperationException($"Required plugin '{pluginId}' could not be loaded.");

        VerifyManifestAgainstCode(manifest, loadedPlugins[0]);
        if (official is not null)
        {
            SetOfficialRequired(pluginId, true);
        }
        else if (communityEntry is null)
        {
            var descriptor = loadedPlugins[0].Descriptor;
            AppendCommunityEntry(descriptor, DeriveCategory(loadedPlugins[0]), repo, assembly, manifest.Discoverable);
        }

        foreach (var plugin in loadedPlugins)
            registry.Add(plugin, community: official is null);
    }

    private static PluginManifest? ReadInstalledManifest(string folder, string assembly)
    {
        if (!assembly.StartsWith(PluginManifestFile.Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var suffix = assembly[PluginManifestFile.Prefix.Length..];
        var path = Path.Combine(folder, PluginManifestFile.FileName(suffix));
        return File.Exists(path) ? PluginManifestFile.Parse(File.ReadAllText(path)) : null;
    }

    // plugins.community.json append — persists a custom-added plugin so it is recognized
    // by id from then on (reinstall, listing, author display). Conflicts on id or repo fail
    // the job before the plugin is registered.
    private void AppendCommunityEntry(
        PluginDescriptor descriptor,
        string category,
        string repo,
        string assembly,
        bool discoverable = true)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugins.community.json");
        EditJsonFile("plugins.community.json", path, "Plugins", "List", "Community", array =>
        {
            foreach (var node in array)
            {
                if (node is not JsonObject entry) continue;
                if (TryString(entry["id"], out var listedId) &&
                    string.Equals(listedId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"A community plugin with id '{descriptor.Id}' is already listed.");
                if (TryString(entry["repo"], out var listedRepo) &&
                    string.Equals(NormalizeRepo(listedRepo), NormalizeRepo(repo), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("That repo is already listed in plugins.community.json.");
            }
            var created = new JsonObject
            {
                ["id"] = descriptor.Id,
                ["name"] = descriptor.Name,
                ["category"] = category,
                ["repo"] = repo,
                ["assembly"] = assembly,
                ["discoverable"] = discoverable,
            };
            if (!string.IsNullOrWhiteSpace(descriptor.Author))
                created["author"] = descriptor.Author;
            array.Add(created);
        });
    }

    private static string DeriveCategory(IMediaPagerPlugin plugin) => plugin switch
    {
        IEmailProviderPlugin => "email",
        ISubtitleProviderPlugin => "subtitles",
        ISearchProviderPlugin => "search",
        IMetadataProviderPlugin => "metadata",
        IStreamProviderPlugin => "stream",
        IPluginActions => "actions",
        IPluginInterface => "interface",
        _ => "other",
    };

    // The capability vocabulary is shared with the discovery manifest ("types") and the
    // management screen; a manifest claim is only trusted when the loaded plugin actually
    // implements that interface.
    private static string[] CapabilitiesOf(IMediaPagerPlugin plugin) => new[]
    {
        plugin is IStreamProviderPlugin ? "stream" : null,
        plugin is IMetadataProviderPlugin ? "metadata" : null,
        plugin is ISearchProviderPlugin ? "search" : null,
        plugin is ISubtitleProviderPlugin ? "subtitles" : null,
        plugin is IEmailProviderPlugin ? "email" : null,
        plugin is IPluginActions ? "actions" : null,
        plugin is IPluginInterface ? "interface" : null,
    }.Where(capability => capability is not null).ToArray()!;

    private static readonly Version SdkVersion = ResolveSdkVersion();

    private static Version ResolveSdkVersion()
    {
        var assembly = typeof(IMediaPagerPlugin).Assembly;
        var version = assembly.GetName().Version;
        return version is { Major: > 0 } ? version : new Version(0, 1, 0);
    }

    /// <summary>The manifest that vetted an install must match the code that was built:
    /// the plugin's own id/name (read from its descriptor) and the capability interfaces it
    /// actually implements. A mismatch fails the job — the otherwise-silent distinction
    /// between "claims to be a stream plugin" and "is one".</summary>
    private static void VerifyManifestAgainstCode(PluginManifest manifest, IMediaPagerPlugin plugin)
    {
        var descriptor = plugin.Descriptor;
        if (!string.Equals(manifest.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Manifest/code mismatch: the manifest id '{manifest.Id}' does not match the plugin's own id '{descriptor.Id}'.");
        if (!string.Equals(manifest.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Manifest/code mismatch: the manifest name '{manifest.Name}' does not match the plugin's own name '{descriptor.Name}'.");

        var actual = CapabilitiesOf(plugin);
        var unclaimed = manifest.Types
            .Where(type => !actual.Contains(type, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unclaimed.Count > 0)
            throw new InvalidOperationException(
                $"Manifest/code mismatch: the manifest claims capability '{string.Join(", ", unclaimed)}' "
                + "but the plugin does not implement it.");

        if (!PluginManifestFile.IsSdkCompatible(manifest, SdkVersion))
            throw new InvalidOperationException(
                $"Manifest/code mismatch: the manifest declares SDK '{manifest.Sdk} {manifest.SdkVersion}' "
                + $"but this server runs '{PluginManifestFile.SdkName} {SdkVersion}' (major version must match).");
    }

    // Read → mutate the JSON array at path (root → section keys → array) → write back
    // atomically → reload every configuration provider so ReadRequired/ReadCommunity and
    // the checklist see the new file immediately (the list files are load-once).
    private void EditJsonFile(string fileName, string path, string rootKey, string sectionKey, string arrayKey, Action<JsonArray> mutate)
    {
        FileWriteLock.Wait();
        try
        {
            var text = File.ReadAllText(path);
            var root = JsonNode.Parse(
                text,
                nodeOptions: null,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject ?? new JsonObject();
            var section = EnsureObject(root, rootKey);
            var nested = EnsureObject(section, sectionKey);
            var array = nested[arrayKey] as JsonArray ?? [];
            nested[arrayKey] = array;
            mutate(array);

            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            FileWriteLock.Release();
        }

        if (configuration is IConfigurationRoot rootConfig)
        {
            foreach (var provider in rootConfig.Providers)
            {
                try
                {
                    provider.Load();
                }
                catch
                {
                    // One provider failing to reload must not fail the operation — the
                    // files are written; the next restart reads them regardless.
                }
            }
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string key) =>
        parent[key] as JsonObject ?? new JsonObject().Also(node => parent[key] = node);

    private static int IndexOf(JsonArray array, string id)
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) &&
                string.Equals(text, id, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private static bool TryString(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text2) && !string.IsNullOrWhiteSpace(text2))
        {
            text = text2;
            return true;
        }
        text = "";
        return false;
    }

    // Identity across transports: git@github.com:a/b and https://github.com/a/b must
    // collide for the official/community "already listed" checks (GitRepoUrl.Normalize
    // reduces every accepted form to host/path).
    private static string NormalizeRepo(string repo) => GitRepoUrl.Normalize(repo);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort rollback of a failed custom add.
        }
    }

    // Keep the job store small: finished jobs older than 30 minutes drop out; everything
    // still running is kept (the SPA polls until done/failed).
    private void PruneJobs()
    {
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAgo(TimeSpan.FromMinutes(30)))
                _jobs.TryRemove(id, out _);
        }
    }

    private sealed record JobPlan(
        string Kind,
        string Title,
        string? Id,
        string Name,
        string Repo,
        string? Assembly,
        string Directory,
        bool Official,
        bool Custom,
        string? Branch = null)
    {
        public bool AlreadyDeployed() =>
            Assembly is not null && File.Exists(Path.Combine(Directory, Assembly, $"{Assembly}.dll"));
    }

    private sealed class PluginJobState
    {
        private readonly object _sync = new();
        private readonly List<string> _log = [];
        private DateTimeOffset? _finishedAt;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string? Key { get; init; }
        public string? Repo { get; init; }
        public string? Branch { get; init; }
        public string Kind { get; init; } = "install";
        public string Title { get; init; } = "Plugin job";
        public string Stage { get; private set; } = "queued";
        public int Percent { get; private set; }
        public bool Done { get; private set; }
        public bool Failed { get; private set; }
        public string? Error { get; private set; }
        public string? PluginId { get; private set; }

        public void Update(int percent, string stage, string message)
        {
            lock (_sync)
            {
                if (Done || Failed) return;
                Percent = percent;
                Stage = stage;
                _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            }
        }

        public void Complete(string pluginId)
        {
            lock (_sync)
            {
                Done = true;
                PluginId = pluginId;
                _finishedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Fail(string error)
        {
            lock (_sync)
            {
                Failed = true;
                Stage = "failed";
                Error = error;
                _log.Add($"{DateTime.Now:HH:mm:ss}  Failed: {error}");
                _finishedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool FinishedAgo(TimeSpan age) =>
            _finishedAt is { } finished && DateTimeOffset.UtcNow - finished > age;

        public PluginJobSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new PluginJobSnapshot(
                    Id, Key, Repo, Branch, Kind, Title, Stage, Percent, Done, Failed, Error, PluginId,
                    _log.ToList());
            }
        }
    }
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

/// <summary>Body of POST /plugins/jobs: exactly one of a listed plugin id or a repo URL
/// (branch applies to repo adds only — null = the repo's default branch).</summary>
public sealed record PluginJobRequest(string? Key, string? Repo, string? Branch = null);

/// <summary>Result of a turn-on that could be satisfied from already-deployed files:
/// either an instant message (done) or a jobId to poll (deploy needed first).</summary>
public sealed record PluginEnableOutcome(string? JobId, string? Message);
