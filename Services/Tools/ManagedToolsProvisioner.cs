namespace MediaPager.App.Api.Services.Tools;

public sealed record ManagedToolsBootstrapProgress(bool NeedsInstallation, double Progress, string Message);

public sealed record ManagedToolsBootstrapResult(
    string ToolDirectory,
    bool NeedsInstallation,
    IReadOnlyList<string> Failures,
    bool RequiresElevation = false);

/// <summary>Finds an existing complete tools directory before choosing an installation target,
/// then installs only missing toolsets into that selected root.</summary>
public sealed class ManagedToolsProvisioner(
    ManagedToolDirectory directories,
    FfmpegToolInstaller ffmpeg,
    PlaywrightBrowserInstaller chromium,
    ILogger<ManagedToolsProvisioner> logger)
{
    public async Task<ManagedToolsBootstrapResult> EnsureInstalledAsync(
        IProgress<ManagedToolsBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var readiness = new List<ToolReadiness>(directories.Candidates.Count);
        foreach (var candidate in directories.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ffmpegReady = await ffmpeg.IsInstalledAtAsync(candidate, cancellationToken);
            var chromiumReady = false;
            try { chromiumReady = chromium.IsChromiumInstalled(candidate); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            readiness.Add(new ToolReadiness(candidate, ffmpegReady, chromiumReady));
        }

        // Prefer a root containing both validated toolsets. Otherwise reuse the root with
        // the most ready tools (candidate order gives the configured volume priority), and
        // install any missing tools there so one volume becomes the complete cache.
        var selected = readiness.FirstOrDefault(item => item.FfmpegReady && item.ChromiumReady)
            ?? readiness.OrderByDescending(item => (item.FfmpegReady ? 1 : 0) + (item.ChromiumReady ? 1 : 0)).FirstOrDefault()
            ?? new ToolReadiness(directories.PreferredInstallRoot, false, false);
        directories.SelectRoot(selected.Root);

        var missingCount = (selected.FfmpegReady ? 0 : 1) + (selected.ChromiumReady ? 0 : 1);
        if (missingCount == 0)
        {
            logger.LogInformation("Reusing the verified managed tools directory {ToolDirectory}.", selected.Root);
            progress?.Report(new ManagedToolsBootstrapProgress(false, 1, "Managed tools are ready."));
            return new ManagedToolsBootstrapResult(selected.Root, NeedsInstallation: false, []);
        }

        if (!CanWriteDirectory(selected.Root))
        {
            progress?.Report(new ManagedToolsBootstrapProgress(
                true, 0, "Waiting for permission to install tools beside the application…"));
            return new ManagedToolsBootstrapResult(selected.Root, NeedsInstallation: true, [], RequiresElevation: true);
        }

        progress?.Report(new ManagedToolsBootstrapProgress(true, 0, "Preparing the managed tools directory…"));
        var failures = new List<string>();
        var stageIndex = 0;

        if (!selected.FfmpegReady)
        {
            var stageStart = stageIndex / (double)missingCount;
            var stageLength = 1d / missingCount;
            var toolProgress = new InlineProgress<MediaToolProgress>(update =>
                progress?.Report(new ManagedToolsBootstrapProgress(
                    true,
                    stageStart + stageLength * Math.Clamp(update.Progress / 0.1, 0, 1),
                    update.Message)));
            try
            {
                await ffmpeg.EnsureInstalledAsync(cancellationToken, toolProgress, selected.Root);
                logger.LogInformation("Managed FFmpeg/FFprobe are ready in {ToolDirectory}.", selected.Root);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not install managed FFmpeg/FFprobe.");
                failures.Add($"FFmpeg/FFprobe: {exception.Message}");
            }
            stageIndex++;
        }

        if (!selected.ChromiumReady)
        {
            var stageStart = stageIndex / (double)missingCount;
            var stageLength = 1d / missingCount;
            var toolProgress = new InlineProgress<MediaToolProgress>(update =>
                progress?.Report(new ManagedToolsBootstrapProgress(
                    true,
                    stageStart + stageLength * Math.Clamp(update.Progress, 0, 1),
                    update.Message)));
            try
            {
                await chromium.EnsureChromiumInstalledAsync(cancellationToken, toolProgress, selected.Root);
                logger.LogInformation("Managed Chromium is ready in {ToolDirectory}.", selected.Root);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not install managed Chromium.");
                failures.Add($"Chromium: {exception.Message}");
            }
            stageIndex++;
        }

        var success = failures.Count == 0;
        progress?.Report(new ManagedToolsBootstrapProgress(
            true,
            success ? 1 : stageIndex / (double)missingCount,
            success ? "Managed tools are ready." : "One or more managed tools need attention."));
        return new ManagedToolsBootstrapResult(selected.Root, NeedsInstallation: true, failures);
    }

    private sealed record ToolReadiness(string Root, bool FfmpegReady, bool ChromiumReady);

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testPath = Path.Combine(directory, $".mediapager-write-test-{Guid.NewGuid():N}");
            using (new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(testPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 1 or 5 or 13 or 30)
        {
            return false;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
