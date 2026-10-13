namespace MediaPager.App.Api.Services;

// Local artwork bytes are kept on disk, never in the database. Poster and backdrop folders
// can be configured separately; the legacy Artwork:Directory remains a shared-directory
// fallback for existing installations.
public sealed class ArtworkStorage
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".avif",
    };

    public static string DefaultDirectory()
    {
        return Path.Combine(StorageLayout.DefaultDataRoot, "artwork");
    }

    public static string LegacyDefaultDirectory()
    {
        var oldRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaPager")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".MediaPager");
        return Path.Combine(oldRoot, "artwork");
    }

    public static string ResolveDirectory(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? DefaultDirectory()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

    public static string ResolveCategoryDirectory(
        string? categoryDirectory,
        string? legacyDirectory,
        string dataRoot,
        bool poster)
    {
        var fallback = Path.Combine(dataRoot, "artwork", poster ? "posters" : "backgrounds");
        var configured = !string.IsNullOrWhiteSpace(categoryDirectory) ? categoryDirectory : legacyDirectory;
        if (string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(fallback);
        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(dataRoot, expanded));
    }

    public static void MigrateLegacyDefaultArtwork(string dataRoot)
    {
        var legacy = LegacyDefaultDirectory();
        var destination = Path.Combine(dataRoot, "artwork", "legacy");
        if (!Directory.Exists(legacy) || PathsEqual(legacy, destination)) return;
        Directory.CreateDirectory(destination);
        foreach (var source in Directory.EnumerateFiles(legacy, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(source))) continue;
            var target = Path.Combine(destination, Path.GetFileName(source));
            if (!File.Exists(target)) File.Copy(source, target);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public string Save(string directory, Stream content, string extension, long maxBytes = 6 * 1024 * 1024)
    {
        Directory.CreateDirectory(directory);
        var name = $"{Guid.NewGuid():N}{extension}";
        var path = Path.Combine(directory, name);
        using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = content.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
            {
                output.Dispose();
                File.Delete(path);
                throw new IOException($"Image exceeds the {maxBytes} byte limit.");
            }
            output.Write(buffer, 0, read);
        }
        return name;
    }
}
