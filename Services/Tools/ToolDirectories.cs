namespace MediaPager.App.Api.Services.Tools;

/// <summary>Selects a managed tool cache. An explicitly configured persistent volume is
/// preferred when populated; otherwise existing tools beside the API binary (or the legacy
/// per-user cache) are reused before a new installation target is chosen.</summary>
public sealed class ManagedToolDirectory
{
    private readonly string configuredRoot;
    private readonly string adjacentRoot;
    private readonly string[] candidates;
    private string activeRoot;

    public ManagedToolDirectory(IConfiguration configuration)
    {
        var configured = configuration["Tools:Directory"];
        configuredRoot = string.IsNullOrWhiteSpace(configured) ? "" : Path.GetFullPath(configured);
        adjacentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools"));
        var legacyRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".MediaPager", "tools"));
        candidates = new[] { configuredRoot, adjacentRoot, legacyRoot }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        activeRoot = PreferredExistingRoot();
    }

    public string Root => Volatile.Read(ref activeRoot);

    public string PreferredInstallRoot => string.IsNullOrEmpty(configuredRoot) ? adjacentRoot : configuredRoot;

    public IReadOnlyList<string> Candidates => candidates;

    public void SelectRoot(string root) => Interlocked.Exchange(ref activeRoot, Path.GetFullPath(root));

    private string PreferredExistingRoot()
    {
        var bestRoot = PreferredInstallRoot;
        var bestScore = 0;
        foreach (var candidate in candidates)
        {
            var ffmpegPath = Path.Combine(candidate, "ffmpeg", "9.0.2");
            var hasFfmpeg = false;
            try
            {
                hasFfmpeg = Directory.Exists(ffmpegPath) &&
                    Directory.EnumerateDirectories(ffmpegPath).Any(path => File.Exists(Path.Combine(path, "tool-manifest.json")));
            }
            catch (IOException) { }

            var hasChromium = File.Exists(Path.Combine(candidate, "playwright", "1.63.0", "mediapager-browser-manifest.json"));
            var score = (hasFfmpeg ? 1 : 0) + (hasChromium ? 1 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                bestRoot = candidate;
            }
        }
        return bestRoot;
    }
}

public static class ToolDirectories
{
    public static string FfmpegVersion(string root, string runtimeIdentifier) =>
        Path.Combine(root, "ffmpeg", "9.0.2", runtimeIdentifier);
}
