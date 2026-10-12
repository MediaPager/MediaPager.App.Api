using System.Text.Json;

namespace MediaPager.App.Api.Services.Tools;

public sealed record ManagedToolsBootstrapStatus(
    string State,
    bool? NeedsInstallation,
    double Progress,
    string Message,
    string? Error = null,
    string? ToolDirectory = null);

internal static class ManagedToolsBootstrapStatusFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        string path,
        ManagedToolsBootstrapStatus status,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(status, JsonOptions) + "\n", cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    public static async Task<ManagedToolsBootstrapStatus?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<ManagedToolsBootstrapStatus>(stream, JsonOptions, cancellationToken);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
