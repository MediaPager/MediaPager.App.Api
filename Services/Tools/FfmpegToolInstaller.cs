using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaPager.App.Api.Services.Tools;

public sealed record FfmpegToolPaths(string Ffmpeg, string Ffprobe, string VersionDirectory);

/// <summary>Installs checksum-pinned FFmpeg tools into the app-managed tools cache. It
/// never uses or modifies the operating system's PATH/tool installation.</summary>
public sealed class FfmpegToolInstaller(
    HttpClient httpClient,
    ManagedToolDirectory toolDirectory,
    ILogger<FfmpegToolInstaller> logger)
{
    private const string Version = "9.0.2";
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.Ordinal);

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
        => await IsInstalledAtAsync(toolDirectory.Root, cancellationToken);

    public async Task<bool> IsInstalledAtAsync(string root, CancellationToken cancellationToken = default)
    {
        var runtimeIdentifier = CurrentRuntimeIdentifier();
        var installDirectory = ToolDirectories.FfmpegVersion(Path.GetFullPath(root), runtimeIdentifier);
        return await TryValidateInstalledAsync(installDirectory, cancellationToken) is not null;
    }

    public async Task<FfmpegToolPaths> EnsureInstalledAsync(
        CancellationToken cancellationToken = default,
        IProgress<MediaToolProgress>? progress = null,
        string? toolsRoot = null)
    {
        var runtimeIdentifier = CurrentRuntimeIdentifier();
        var root = Path.GetFullPath(toolsRoot ?? toolDirectory.Root);
        var installDirectory = ToolDirectories.FfmpegVersion(root, runtimeIdentifier);
        var gate = InstallLocks.GetOrAdd(installDirectory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var parentDirectory = Path.GetDirectoryName(installDirectory)!;
            Directory.CreateDirectory(parentDirectory);
            await using var processLock = await AcquireProcessLockAsync(
                Path.Combine(parentDirectory, ".ffmpeg-9.0.2.install.lock"), cancellationToken);
            if (await TryValidateInstalledAsync(installDirectory, cancellationToken) is { } installed)
            {
                progress?.Report(new MediaToolProgress(0.05, "Managed FFmpeg tools are ready."));
                return installed;
            }

            var artifacts = ArtifactsFor(runtimeIdentifier);
            var stagingDirectory = Path.Combine(parentDirectory, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                var extractedDirectory = Path.Combine(stagingDirectory, "extract");
                Directory.CreateDirectory(extractedDirectory);
                var archivesDirectory = Path.Combine(stagingDirectory, "archives");
                Directory.CreateDirectory(archivesDirectory);

                for (var index = 0; index < artifacts.Archives.Count; index++)
                {
                    var artifact = artifacts.Archives[index];
                    var archivePath = Path.Combine(archivesDirectory, artifact.FileName);
                    progress?.Report(new MediaToolProgress(0.01 + (index / (double)artifacts.Archives.Count * 0.07),
                        $"Downloading pinned FFmpeg tools ({index + 1}/{artifacts.Archives.Count})…"));
                    await DownloadVerifiedAsync(artifact, archivePath, index, artifacts.Archives.Count, progress, cancellationToken);
                    await ExtractZipSafelyAsync(archivePath, extractedDirectory, cancellationToken);
                }

                CopyLicenseFiles(extractedDirectory, Path.Combine(stagingDirectory, "licenses"));
                var bundledNotice = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices", "FFmpeg.md");
                if (File.Exists(bundledNotice))
                {
                    var noticeDirectory = Path.Combine(stagingDirectory, "licenses");
                    Directory.CreateDirectory(noticeDirectory);
                    File.Copy(bundledNotice, Path.Combine(noticeDirectory, "MediaPager-FFmpeg.md"), overwrite: true);
                }
                var binaryDirectory = Path.Combine(stagingDirectory, "bin");
                Directory.CreateDirectory(binaryDirectory);
                var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
                var ffmpegSource = FindBinary(extractedDirectory, "ffmpeg" + executableSuffix);
                var ffprobeSource = FindBinary(extractedDirectory, "ffprobe" + executableSuffix);
                var ffmpegPath = Path.Combine(binaryDirectory, "ffmpeg" + executableSuffix);
                var ffprobePath = Path.Combine(binaryDirectory, "ffprobe" + executableSuffix);

                if (OperatingSystem.IsWindows())
                {
                    var sourceBin = Path.GetDirectoryName(ffmpegSource)!;
                    if (!string.Equals(sourceBin, Path.GetDirectoryName(ffprobeSource), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The FFmpeg archive placed ffmpeg and ffprobe in different directories.");
                    foreach (var dependency in Directory.EnumerateFiles(sourceBin))
                        File.Copy(dependency, Path.Combine(binaryDirectory, Path.GetFileName(dependency)), overwrite: true);
                }
                else
                {
                    File.Copy(ffmpegSource, ffmpegPath, overwrite: true);
                    File.Copy(ffprobeSource, ffprobePath, overwrite: true);
                    SetExecutable(ffmpegPath);
                    SetExecutable(ffprobePath);
                }

                Directory.Delete(extractedDirectory, recursive: true);
                Directory.Delete(archivesDirectory, recursive: true);

                var metadata = new InstalledFfmpegMetadata(
                    Version,
                    runtimeIdentifier,
                    artifacts.BuildDescription,
                    "GPL-enabled; FFmpeg and external-library terms are documented at https://ffmpeg.org/legal.html",
                    artifacts.Archives.Select(archive => new InstalledArchiveMetadata(
                        archive.FileName, archive.Url, archive.Sha256)).ToArray(),
                    await HashFileAsync(ffmpegPath, cancellationToken),
                    await HashFileAsync(ffprobePath, cancellationToken),
                    DateTimeOffset.UtcNow);
                await File.WriteAllTextAsync(
                    Path.Combine(stagingDirectory, "tool-manifest.json"),
                    JsonSerializer.Serialize(metadata, JsonOptions),
                    cancellationToken);

                var stagingPaths = new FfmpegToolPaths(ffmpegPath, ffprobePath, stagingDirectory);
                await ValidateExecutablesAsync(stagingPaths, cancellationToken);
                progress?.Report(new MediaToolProgress(0.09, "Verifying managed FFmpeg executables…"));

                if (Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);
                Directory.Move(stagingDirectory, installDirectory);
                logger.LogInformation("Installed FFmpeg {Version} ({RuntimeIdentifier}) in managed tool cache {ToolDirectory}.",
                    Version, runtimeIdentifier, installDirectory);
                progress?.Report(new MediaToolProgress(0.1, "Managed FFmpeg tools are ready."));

                return PathsFor(installDirectory);
            }
            catch
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FfmpegToolPaths?> TryValidateInstalledAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var paths = PathsFor(installDirectory);
        var manifestPath = Path.Combine(installDirectory, "tool-manifest.json");
        if (!File.Exists(paths.Ffmpeg) || !File.Exists(paths.Ffprobe) || !File.Exists(manifestPath))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize<InstalledFfmpegMetadata>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions);
            if (metadata is null || metadata.Version != Version || metadata.RuntimeIdentifier != CurrentRuntimeIdentifier())
                return null;
            var ffmpegHash = await HashFileAsync(paths.Ffmpeg, cancellationToken);
            var ffprobeHash = await HashFileAsync(paths.Ffprobe, cancellationToken);
            if (!string.Equals(ffmpegHash, metadata.FfmpegSha256, StringComparison.Ordinal) ||
                !string.Equals(ffprobeHash, metadata.FfprobeSha256, StringComparison.Ordinal))
                return null;
            await ValidateExecutablesAsync(paths, cancellationToken);
            return paths;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The cached FFmpeg toolset failed validation; it will be reinstalled.");
            return null;
        }
    }

    private async Task DownloadVerifiedAsync(
        ArchiveArtifact artifact,
        string destination,
        int archiveIndex,
        int archiveCount,
        IProgress<MediaToolProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The tool archive download did not remain on HTTPS.");
        if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
            throw new InvalidDataException($"The tool archive '{artifact.FileName}' exceeds the configured size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long totalBytes = 0;
        var lastReportedPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalBytes += read;
            if (totalBytes > MaximumArchiveBytes)
                throw new InvalidDataException($"The tool archive '{artifact.FileName}' exceeds the configured size limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (response.Content.Headers.ContentLength is long length && length > 0)
            {
                var percent = (int)Math.Clamp(totalBytes * 100 / length, 0, 100);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    var overall = 0.01 + ((archiveIndex + percent / 100d) / archiveCount * 0.07);
                    progress?.Report(new MediaToolProgress(overall, $"Downloading {artifact.FileName} ({percent}%)…"));
                }
            }
        }
        await output.FlushAsync(cancellationToken);

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 verification failed for tool archive '{artifact.FileName}'.");
    }

    private static async Task<FileStream> AcquireProcessLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (UnauthorizedAccessException) when (IsExternalDockerBootstrap())
            {
                // The detached root bootstrapper may own its in-flight lock file until it
                // finishes installing and hands the tools directory back to the API account.
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }

    private static bool IsExternalDockerBootstrap() =>
        OperatingSystem.IsLinux() &&
        string.Equals(Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_BOOTSTRAP_EXTERNAL"), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractZipSafelyAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        if (archive.Entries.Count > 100_000)
            throw new InvalidDataException("The tool archive contains too many entries.");
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded tool archive exceeds the configured size limit.");
            var normalizedName = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(normalizedName) || normalizedName.Contains(':') ||
                normalizedName.Split('/').Any(segment => segment is ".." or "."))
                throw new InvalidDataException($"Unsafe archive path '{entry.FullName}'.");

            var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!targetPath.StartsWith(destinationRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"Archive path '{entry.FullName}' escapes the staging directory.");
            if (normalizedName.EndsWith('/'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var entryStream = entry.Open();
            await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await entryStream.CopyToAsync(output, cancellationToken);
        }
    }

    private static string FindBinary(string directory, string name) =>
        Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault()
        ?? throw new InvalidDataException($"The pinned FFmpeg archive did not contain '{name}'.");

    private static void CopyLicenseFiles(string extractedDirectory, string licenseDirectory)
    {
        var licenseFiles = Directory.EnumerateFiles(extractedDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).StartsWith("license", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).StartsWith("copying", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (licenseFiles.Count == 0) return;
        Directory.CreateDirectory(licenseDirectory);
        foreach (var source in licenseFiles)
        {
            var relative = Path.GetRelativePath(extractedDirectory, source);
            var destination = Path.GetFullPath(Path.Combine(licenseDirectory, relative));
            var root = Path.GetFullPath(licenseDirectory) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static async Task ValidateExecutablesAsync(FfmpegToolPaths paths, CancellationToken cancellationToken)
    {
        var ffmpegVersion = await ReadVersionAsync(paths.Ffmpeg, cancellationToken);
        var ffprobeVersion = await ReadVersionAsync(paths.Ffprobe, cancellationToken);
        if (!ffmpegVersion.Contains("9.0", StringComparison.Ordinal) ||
            !ffprobeVersion.Contains("9.0", StringComparison.Ordinal))
            throw new InvalidDataException("The installed FFmpeg tools are not from the pinned 9.0 release series.");
    }

    private static async Task<string> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-version");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start an installed media tool.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout + await stderr;
        if (process.ExitCode != 0)
            throw new InvalidDataException($"The installed media tool failed its version check: {output.Trim()}");
        return output;
    }

    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static FfmpegToolPaths PathsFor(string installDirectory)
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var binaryDirectory = Path.Combine(installDirectory, "bin");
        return new FfmpegToolPaths(
            Path.Combine(binaryDirectory, "ffmpeg" + suffix),
            Path.Combine(binaryDirectory, "ffprobe" + suffix),
            installDirectory);
    }

    private static string CurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"FFmpeg tools do not support {RuntimeInformation.ProcessArchitecture} yet."),
        };
        var platform = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("FFmpeg tools are currently supported on Windows, macOS, and Linux.");
        return $"{platform}-{architecture}";
    }

    private static PlatformArtifacts ArtifactsFor(string runtimeIdentifier) => runtimeIdentifier switch
    {
        "win-x64" => new("BtbN FFmpeg 9.0 GPL static build, pinned by SHA-256",
        [new("ffmpeg-win64-gpl.zip",
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-9.0.zip",
            "24833dc7c61e61d4af34558cb51172b56c1ab427cd70df1b56733214af9a7f84")]),
        "osx-x64" => MartinRiedl("macos", "amd64", "1789931006_9.0.2",
            "7c6b4125b191cbf773832dc51f424cf2b6bb7da43007d1e066f95909e47cacd4",
            "2322438ed2f6319a691291b247d09c69dcaa3a982460d1f269a7e1af335cfdfd"),
        "osx-arm64" => MartinRiedl("macos", "arm64", "1789931890_9.0.2",
            "c8ed4c4e6978a03c485edbfe4e0a5dc2380f8a30bba5150531b31b094492d924",
            "fcbe839537485eaee7a7a8bc5cbc0f90d53617e80943e8a5b2e31cb851197ea6"),
        "linux-x64" => MartinRiedl("linux", "amd64", "1789931100_9.0.2",
            "fa8ecf4abbd290d98f7d188b8649cc6b391ae209a98452be955a15aab1909d7f",
            "3f428c49070be3d24ec338602b76d412e401ffcb8a5641ef0e729181a232fc32"),
        "linux-arm64" => MartinRiedl("linux", "arm64", "1789931697_9.0.2",
            "93a76ae90db5474eecdf951a729857c64f3de23567228d6a7d5e6e8e3cd1021b",
            "bcbe80fb741c180083327afaf5434812e006b33cacde2016b9aeaf6936128330"),
        _ => throw new PlatformNotSupportedException($"No pinned FFmpeg tool archives are available for '{runtimeIdentifier}'."),
    };

    private static PlatformArtifacts MartinRiedl(string platform, string architecture, string build, string ffmpegHash, string ffprobeHash)
    {
        var baseUrl = $"https://ffmpeg.martin-riedl.de/download/{platform}/{architecture}/{build}";
        return new($"Martin Riedl FFmpeg 9.0.2 GPL-enabled static build ({platform}/{architecture})",
        [
            new($"ffmpeg-{platform}-{architecture}.zip", $"{baseUrl}/ffmpeg.zip", ffmpegHash),
            new($"ffprobe-{platform}-{architecture}.zip", $"{baseUrl}/ffprobe.zip", ffprobeHash),
        ]);
    }

    private sealed record ArchiveArtifact(string FileName, string Url, string Sha256);
    private sealed record PlatformArtifacts(string BuildDescription, IReadOnlyList<ArchiveArtifact> Archives);
    private sealed record InstalledArchiveMetadata(string FileName, string Url, string Sha256);
    private sealed record InstalledFfmpegMetadata(
        string Version,
        string RuntimeIdentifier,
        string BuildDescription,
        string License,
        IReadOnlyList<InstalledArchiveMetadata> Archives,
        string FfmpegSha256,
        string FfprobeSha256,
        DateTimeOffset InstalledAt);
}
