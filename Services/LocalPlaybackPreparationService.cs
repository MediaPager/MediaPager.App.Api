using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MediaPager.App.Api.Services;

/// <summary>
/// Creates a short-lived, cached fast-start copy of an existing local catalog object when
/// its MP4 metadata is at the end of the file. This lets browser media elements begin
/// playback without downloading the entire object first; the catalog file is never changed.
/// </summary>
public sealed class LocalPlaybackPreparationService(
    FfmpegToolInstaller tools,
    ManagedToolDirectory toolDirectory,
    ILogger<LocalPlaybackPreparationService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PreparationLocks = new(StringComparer.Ordinal);

    public async Task<string> PrepareForPlaybackAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var extension = Path.GetExtension(fullSourcePath).ToLowerInvariant();
        if (extension is not (".mp4" or ".m4v" or ".mov" or ".m4a" or ".m4b"))
            return fullSourcePath;

        var source = new FileInfo(fullSourcePath);
        if (!source.Exists || !await Mp4FastStartDetector.RequiresFastStartAsync(fullSourcePath, cancellationToken))
            return fullSourcePath;

        var identity = Encoding.UTF8.GetBytes($"{fullSourcePath}\0{source.Length}\0{source.LastWriteTimeUtc.Ticks}");
        var cacheKey = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        var cacheDirectory = Path.Combine(toolDirectory.Root, "local-playback-faststart");
        Directory.CreateDirectory(cacheDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(cacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RemoveExpiredCacheFiles(cacheDirectory, DateTime.UtcNow - CacheLifetime);

        var optimizedPath = Path.Combine(cacheDirectory, $"{cacheKey}{extension}");
        if (await IsValidFastStartCopyAsync(optimizedPath, cancellationToken))
        {
            File.SetLastWriteTimeUtc(optimizedPath, DateTime.UtcNow);
            return optimizedPath;
        }

        var preparationLock = PreparationLocks.GetOrAdd(optimizedPath, _ => new SemaphoreSlim(1, 1));
        await preparationLock.WaitAsync(cancellationToken);
        try
        {
            if (await IsValidFastStartCopyAsync(optimizedPath, cancellationToken))
            {
                File.SetLastWriteTimeUtc(optimizedPath, DateTime.UtcNow);
                return optimizedPath;
            }

            var toolsPaths = await tools.EnsureInstalledAsync(cancellationToken);
            var temporaryPath = Path.Combine(cacheDirectory, $"{cacheKey}.partial-{Guid.NewGuid():N}{extension}");
            try
            {
                logger.LogInformation(
                    "Preparing a browser-friendly playback copy of a local MP4 ({SizeBytes} bytes).",
                    source.Length);
                await RemuxWithFastStartAsync(toolsPaths.Ffmpeg, fullSourcePath, temporaryPath, cancellationToken);
                File.Move(temporaryPath, optimizedPath, overwrite: true);
                File.SetLastWriteTimeUtc(optimizedPath, DateTime.UtcNow);
                return optimizedPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not prepare a local MP4 for browser playback.");
            throw new LocalPlaybackPreparationException(
                "The local MP4 could not be prepared for browser playback.", exception);
        }
        finally
        {
            preparationLock.Release();
        }
    }

    private static async Task<bool> IsValidFastStartCopyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0 &&
                   !await Mp4FastStartDetector.RequiresFastStartAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static void RemoveExpiredCacheFiles(string directory, DateTime cutoffUtc)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RemuxWithFastStartAsync(
        string ffmpegPath,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", sourcePath,
                     "-map", "0", "-c", "copy", "-movflags", "+faststart",
                     "-f", Path.GetExtension(destinationPath).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? "mov" : "mp4",
                     destinationPath,
                 })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The managed FFmpeg tool could not be started.");
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"FFmpeg exited with code {process.ExitCode}."
                : error.Trim());
        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
            throw new InvalidOperationException("FFmpeg completed without creating a playback copy.");
    }
}

public sealed class LocalPlaybackPreparationException(string message, Exception innerException)
    : Exception(message, innerException);
