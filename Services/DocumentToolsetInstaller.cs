using System.IO.Compression;
using System.Text.Json;

namespace MediaPager.App.Api.Services.Tools;

public sealed record DocumentToolInfo(
    string Id,
    string Name,
    string PinnedVersion,
    string? InstalledVersion,
    string? LatestVersion,
    bool Ready,
    string EditorUrl);

public sealed record DocumentToolsetStatus(
    bool Ready,
    string Phase,
    string Message,
    bool UpdateAvailable,
    IReadOnlyList<DocumentToolInfo> Tools);

/// <summary>Installs the pinned, customized document-editor web bundles beside the other
/// managed tools. The build machine does the Rust/WASM work; the API only verifies and stages
/// the versioned release artifacts.</summary>
public sealed class DocumentToolsetInstaller(
    IHttpClientFactory clients,
    ManagedToolDirectory toolDirectory,
    ILogger<DocumentToolsetInstaller> logger)
{
    private sealed record ToolDefinition(string Id, string Name, string SourceRepository, string ArtifactRepository);
    private sealed record BuildManifest(string Tool, string SourceVersion, string DiscordUiPatch, string ArtifactFormat);

    private static readonly ToolDefinition[] Definitions =
    [
        new("wordcraft", "WordCraft", "storytold/wordcraft", "MediaPager/MediaPager.Forks.WordCraft"),
        new("gridcraft", "GridCraft", "storytold/gridcraft", "MediaPager/MediaPager.Forks.GridCraft"),
        new("pdfcraft", "PdfCraft", "storytold/pdfcraft", "MediaPager/MediaPager.Forks.PdfCraft"),
        new("deckcraft", "DeckCraft", "storytold/deckcraft", "MediaPager/MediaPager.Forks.DeckCraft"),
    ];

    private const string BuildPatch = "mediapager-web-integration-v1";
    private const string ArtifactFormat = "quickwasmtest-web-v1";
    private const string ArtifactSuffix = ".mediapager";
    private static readonly TimeSpan LatestCacheLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly object _stateGate = new();
    private Dictionary<string, string> _latestVersions = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _latestCheckedAt;
    private string _phase = "warming";
    private string _message = "Preparing pinned document tools…";

    public string ToolsRoot => Path.Combine(toolDirectory.Root, "document-tools");
    private string PinsPath => Path.Combine(toolDirectory.Root, "document-tool-versions.json");

    public DocumentToolsetStatus GetStatus()
    {
        lock (_stateGate)
        {
            var pins = ReadPins();
            var tools = Definitions.Select(definition =>
            {
                pins.TryGetValue(definition.Id, out var pinned);
                var installed = ReadInstalledVersion(definition);
                _latestVersions.TryGetValue(definition.Id, out var latest);
                var ready = !string.IsNullOrWhiteSpace(pinned) && IsInstalledAtVersion(definition, pinned);
                return new DocumentToolInfo(
                    definition.Id,
                    definition.Name,
                    pinned ?? "missing pin",
                    installed,
                    latest,
                    ready,
                    $"/tools/docs/{definition.Id}/index.html?v={Uri.EscapeDataString(pinned ?? "")}");
            }).ToArray();
            var readyAll = tools.All(tool => tool.Ready);
            var updateAvailable = tools.Any(tool => tool.LatestVersion is not null &&
                                                     !string.Equals(tool.LatestVersion, tool.PinnedVersion, StringComparison.OrdinalIgnoreCase));
            return new DocumentToolsetStatus(readyAll, _phase, _message, updateAvailable, tools);
        }
    }

    public async Task EnsurePinnedToolsAsync(CancellationToken cancellationToken)
    {
        var pins = ReadPins();
        if (Definitions.All(tool => pins.TryGetValue(tool.Id, out var version) && IsInstalledAtVersion(tool, version)))
        {
            SetStatus("ready", "Pinned document tools are ready.");
            return;
        }

        SetStatus("warming", "Installing pinned Word, spreadsheet, PDF, and presentation tools…");
        try
        {
            await InstallToolsetAsync(pins, updatePins: false, cancellationToken);
            SetStatus("ready", "Pinned document tools are ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("warming", "Document-tool installation paused during shutdown.");
            throw;
        }
        catch (Exception exception)
        {
            SetStatus("failed", $"Pinned document-tool installation failed. {exception.Message}");
            throw;
        }
    }

    public async Task<DocumentToolsetStatus> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _latestCheckedAt < LatestCacheLifetime && _latestVersions.Count == Definitions.Length)
            return GetStatus();

        SetStatus("checking", "Checking GitHub for newer document-tool releases…");
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var client = clients.CreateClient(nameof(DocumentToolsetInstaller));
        try
        {
            foreach (var definition in Definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var response = await client.GetAsync(
                    $"https://api.github.com/repos/{definition.SourceRepository}/releases/latest", cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                latest[definition.Id] = release.RootElement.GetProperty("tag_name").GetString()
                    ?? throw new InvalidDataException($"{definition.Name}'s latest release has no tag_name.");
            }

            lock (_stateGate)
            {
                _latestVersions = latest;
                _latestCheckedAt = DateTimeOffset.UtcNow;
            }
            SetStatus("ready", "Document-tool releases checked.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not check for document-tool updates.");
            SetStatus(IsToolsetReady() ? "ready" : "failed", $"Could not check for updates: {exception.Message}");
        }

        return GetStatus();
    }

    public async Task<DocumentToolsetStatus> UpdateToLatestAsync(CancellationToken cancellationToken)
    {
        var status = await CheckForUpdatesAsync(cancellationToken);
        var latest = _latestVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (latest.Count != Definitions.Length)
            throw new InvalidOperationException(status.Message);

        _ = Task.Run(async () =>
        {
            try
            {
                SetStatus("updating", "Installing the latest customized document-tool releases…");
                await InstallToolsetAsync(latest, updatePins: true, CancellationToken.None);
                SetStatus("ready", "Document tools are up to date.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Document-tool update failed.");
                SetStatus(IsToolsetReady() ? "ready" : "failed",
                    $"Document-tool update failed; the previous installed set remains in place. {exception.Message}");
            }
        }, CancellationToken.None);

        SetStatus("updating", "Installing the latest customized document-tool releases…");
        return GetStatus();
    }

    public string? ResolveAssetPath(string toolId, string? relativePath)
    {
        if (!Definitions.Any(definition => string.Equals(definition.Id, toolId, StringComparison.OrdinalIgnoreCase)))
            return null;
        var toolRoot = Path.GetFullPath(Path.Combine(ToolsRoot, toolId));
        var relative = string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath;
        var fullPath = Path.GetFullPath(Path.Combine(toolRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = toolRoot.EndsWith(Path.DirectorySeparatorChar) ? toolRoot : toolRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(fullPath)) return null;
        return fullPath;
    }

    private async Task InstallToolsetAsync(
        IReadOnlyDictionary<string, string> versions,
        bool updatePins,
        CancellationToken cancellationToken)
    {
        await _installGate.WaitAsync(cancellationToken);
        var root = ToolsRoot;
        var parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $"document-tools-staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $"document-tools-backup-{Guid.NewGuid():N}");
        var hadOld = Directory.Exists(root);
        var swapped = false;
        try
        {
            if (Definitions.Any(tool => !versions.TryGetValue(tool.Id, out var version) || string.IsNullOrWhiteSpace(version)))
                throw new InvalidDataException("Every document tool must have a pinned version.");

            Directory.CreateDirectory(staging);
            for (var index = 0; index < Definitions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = Definitions[index];
                var version = versions[definition.Id];
                SetStatus("installing", $"Installing {definition.Name} {version} ({index + 1}/{Definitions.Length})…");
                var destination = Path.Combine(staging, definition.Id);
                if (IsInstalledAtVersion(definition, version))
                    CopyDirectory(Path.Combine(root, definition.Id), destination);
                else
                    await DownloadToolAsync(definition, version, destination, cancellationToken);
            }

            if (hadOld) Directory.Move(root, backup);
            try
            {
                Directory.Move(staging, root);
                swapped = true;
            }
            catch
            {
                if (hadOld && Directory.Exists(backup)) Directory.Move(backup, root);
                throw;
            }

            if (updatePins) await WritePinsAsync(versions, cancellationToken);
            if (Directory.Exists(backup))
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Could not remove old document-tool backup at {BackupPath}.", backup);
                }
            }
        }
        catch
        {
            if (swapped && hadOld && Directory.Exists(backup))
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                Directory.Move(backup, root);
            }
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                if (Directory.Exists(backup) && !Directory.Exists(root)) Directory.Move(backup, root);
                if (Directory.Exists(backup))
                {
                    try { Directory.Delete(backup, recursive: true); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(exception, "Could not remove document-tool backup at {BackupPath}.", backup);
                    }
                }
            }
            finally { _installGate.Release(); }
        }
    }

    private async Task DownloadToolAsync(ToolDefinition tool, string version, string outputPath, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(nameof(DocumentToolsetInstaller));
        var releaseTag = version + ArtifactSuffix;
        var releaseUrl = $"https://api.github.com/repos/{tool.ArtifactRepository}/releases/tags/{Uri.EscapeDataString(releaseTag)}";
        using var response = await client.GetAsync(releaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{tool.Name} {version} customized release was not found (HTTP {(int)response.StatusCode}).");

        await using var releaseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
        var expectedAsset = $"{tool.Id}-web-{releaseTag}.zip";
        var assetUrl = release.RootElement.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == expectedAsset)
            .Select(asset => asset.GetProperty("browser_download_url").GetString())
            .FirstOrDefault(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com");
        if (assetUrl is null) throw new InvalidDataException($"{tool.Name} {version} release has no {expectedAsset} asset.");

        var archivePath = Path.Combine(Path.GetDirectoryName(outputPath)!, $"{tool.Id}-{Guid.NewGuid():N}.zip");
        try
        {
            using var archiveResponse = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            archiveResponse.EnsureSuccessStatusCode();
            await using (var input = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken);

            using var archive = ZipFile.OpenRead(archivePath);
            var manifestEntry = archive.GetEntry(".quickwasmtest-build.json")
                ?? throw new InvalidDataException($"{tool.Name} artifact has no build manifest.");
            using var manifestStream = manifestEntry.Open();
            using var manifestDocument = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            var manifest = manifestDocument.RootElement;
            if (manifest.GetProperty("tool").GetString() != tool.Id ||
                manifest.GetProperty("sourceVersion").GetString() != version ||
                manifest.GetProperty("discordUiPatch").GetString() != BuildPatch ||
                manifest.GetProperty("artifactFormat").GetString() != ArtifactFormat)
                throw new InvalidDataException($"{tool.Name} artifact doesn't match pin {version} and patch {BuildPatch}.");

            ExtractArchive(archive, outputPath);
            if (!HasRequiredAssets(outputPath))
                throw new InvalidDataException($"{tool.Name} artifact is missing its bundled web assets.");
            await File.WriteAllTextAsync(Path.Combine(outputPath, ".build.json"), JsonSerializer.Serialize(new
            {
                version,
                artifactFormat = ArtifactFormat,
                discordUiPatch = BuildPatch,
                sourceRepository = tool.SourceRepository,
                artifactRepository = tool.ArtifactRepository,
                sourceCommit = manifest.TryGetProperty("sourceCommit", out var sourceCommit) ? sourceCommit.GetString() : null,
                craftFontsCommit = manifest.TryGetProperty("craftFontsCommit", out var fontsCommit) ? fontsCommit.GetString() : null,
                builtAtUtc = DateTimeOffset.UtcNow,
            }), cancellationToken);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    private bool IsInstalledAtVersion(ToolDefinition tool, string version)
    {
        var manifestPath = Path.Combine(ToolsRoot, tool.Id, ".build.json");
        if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(ToolsRoot, tool.Id, "index.html"))) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return manifest.RootElement.GetProperty("version").GetString() == version &&
                   manifest.RootElement.GetProperty("discordUiPatch").GetString() == BuildPatch &&
                   manifest.RootElement.GetProperty("artifactFormat").GetString() == ArtifactFormat &&
                   HasRequiredAssets(Path.Combine(ToolsRoot, tool.Id));
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private string? ReadInstalledVersion(ToolDefinition tool)
    {
        var manifestPath = Path.Combine(ToolsRoot, tool.Id, ".build.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return manifest.RootElement.GetProperty("version").GetString();
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private Dictionary<string, string> ReadPins()
    {
        var path = PinsPath;
        if (!File.Exists(path))
        {
            var seedPath = Path.Combine(AppContext.BaseDirectory, "document-tool-versions.json");
            if (!File.Exists(seedPath)) throw new FileNotFoundException("Pinned document tool versions are missing.", seedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(seedPath, path);
        }
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Document tool pins must be a JSON object.");
    }

    private async Task WritePinsAsync(IReadOnlyDictionary<string, string> pins, CancellationToken cancellationToken)
    {
        var temp = PinsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken);
            File.Move(temp, PinsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void SetStatus(string phase, string message)
    {
        lock (_stateGate)
        {
            _phase = phase;
            _message = message;
        }
    }

    private bool IsToolsetReady()
    {
        var pins = ReadPins();
        return Definitions.All(tool => pins.TryGetValue(tool.Id, out var version) && IsInstalledAtVersion(tool, version));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ExtractArchive(ZipArchive archive, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative)) throw new InvalidDataException("Document tool archive contains an absolute path.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException("Document tool archive contains a path outside its installation directory.");
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static bool HasRequiredAssets(string toolRoot)
    {
        try
        {
            var html = File.ReadAllText(Path.Combine(toolRoot, "index.html"));
            var references = System.Text.RegularExpressions.Regex.Matches(
                html,
                "[\\\"'](?<path>(?:\\./)?[^\\\"']+\\.(?:js|wasm|css))[\\\"']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (references.Count == 0) return false;
            var root = Path.GetFullPath(toolRoot) + Path.DirectorySeparatorChar;
            foreach (System.Text.RegularExpressions.Match reference in references)
            {
                var relative = Uri.UnescapeDataString(reference.Groups["path"].Value).TrimStart('.', '/')
                    .Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(toolRoot, relative));
                if (!fullPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(fullPath)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

}

public sealed class DocumentToolsBootstrapService(
    DocumentToolsetInstaller installer,
    ManagedToolsBootstrapState bootstrapState,
    ILogger<DocumentToolsBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await bootstrapState.WaitAsync(stoppingToken);
            await installer.EnsurePinnedToolsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not prepare pinned document tools.");
        }
    }
}
