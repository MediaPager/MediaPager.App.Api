namespace MediaPager.App.Api.Services;

// Uploaded poster/backdrop files land in a folder on disk (never the database). The folder
// is a runtime setting (Artwork:Directory) that defaults to ~/.MediaPager/artwork and can be
// changed in Settings; files are served publicly via /artwork because an <img> tag can't
// attach a bearer token.
public sealed class ArtworkStorage
{
    public static string DefaultDirectory()
    {
        var baseDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaPager")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".MediaPager");
        return Path.Combine(baseDirectory, "artwork");
    }

    public static string ResolveDirectory(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? DefaultDirectory()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

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