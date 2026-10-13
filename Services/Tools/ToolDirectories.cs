namespace MediaPager.App.Api.Services.Tools;

/// <summary>Resolves the writable managed-tool cache under Settings:Storage. Existing
/// adjacent/legacy caches are copied into the selected persistent directory once so upgrades
/// retain downloaded tools without continuing to write beside the application image.</summary>
public sealed class ManagedToolDirectory
{
    private readonly string configuredRoot;
    private readonly string adjacentRoot;
    private readonly string[] candidates;
    private string activeRoot;

    public ManagedToolDirectory(IConfiguration configuration)
    {
        var configured = SettingsConfigurationKeys.ReadConfiguredValue(configuration, SettingsConfigurationKeys.ToolsDirectory);
        var root = new StorageLayout(configuration).DataRoot;
        configuredRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(root, "tools")
            : Path.GetFullPath(Path.IsPathRooted(configured)
                ? Environment.ExpandEnvironmentVariables(configured)
                : Path.Combine(root, Environment.ExpandEnvironmentVariables(configured)));
        adjacentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools"));
        var legacyRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".MediaPager", "tools"));
        var legacyConfigured = Environment.GetEnvironmentVariable("MEDIAPAGER_Tools__Directory")
            ?? configuration["Tools:Directory"];
        var legacyConfiguredRoot = string.IsNullOrWhiteSpace(legacyConfigured) ? "" : Path.GetFullPath(legacyConfigured);
        candidates = new[] { configuredRoot, legacyConfiguredRoot, adjacentRoot, legacyRoot }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        activeRoot = configuredRoot;
        MigrateExistingCache(configuredRoot, candidates.Where(path =>
            !string.Equals(path, configuredRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    }

    public string Root => Volatile.Read(ref activeRoot);

    public string PreferredInstallRoot => configuredRoot;

    public IReadOnlyList<string> Candidates => candidates;

    public void SelectRoot(string root) => Interlocked.Exchange(ref activeRoot, Path.GetFullPath(root));

    private static void MigrateExistingCache(string destination, IEnumerable<string> candidates)
    {
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, ".legacy-cache-migrated-v1");
        if (File.Exists(marker)) return;
        foreach (var source in candidates.Where(HasManagedTools))
            CopyDirectory(source, destination);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
    }

    private static bool HasManagedTools(string candidate)
    {
        if (!Directory.Exists(candidate)) return false;
        try
        {
            return new[] { "ffmpeg/9.0.2", "playwright/1.63.0", "dotnet-sdk/10.0.401" }
                .Any(tool => Directory.Exists(Path.Combine(candidate, tool)) &&
                             Directory.EnumerateFileSystemEntries(Path.Combine(candidate, tool)).Any());
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                continue; // Let the tool verifier/bootstrapper re-create skipped links as needed.
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target);
            }
            else if (!File.Exists(target))
            {
                File.Copy(entry, target);
            }
        }
    }
}

public static class ToolDirectories
{
    public static string FfmpegVersion(string root, string runtimeIdentifier) =>
        Path.Combine(root, "ffmpeg", "9.0.2", runtimeIdentifier);
}
