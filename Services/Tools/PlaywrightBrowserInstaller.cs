using Microsoft.Playwright;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Installs the Playwright-matched Chromium revision inside the app's persistent
/// tool cache rather than into the user's standard Playwright/browser directories.</summary>
public sealed class PlaywrightBrowserInstaller(ManagedToolDirectory toolDirectory, ILogger<PlaywrightBrowserInstaller> logger)
{
    private const string Version = "1.63.0";
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public bool IsChromiumInstalled(string? toolsRoot = null)
    {
        var browserDirectory = BrowserDirectory(toolsRoot);
        return IsInstalled(browserDirectory, Path.Combine(browserDirectory, "mediapager-browser-manifest.json"));
    }

    public async Task EnsureChromiumInstalledAsync(
        CancellationToken cancellationToken = default,
        IProgress<MediaToolProgress>? progress = null,
        string? toolsRoot = null)
    {
        var browserDirectory = BrowserDirectory(toolsRoot);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browserDirectory);
        var markerPath = Path.Combine(browserDirectory, "mediapager-browser-manifest.json");
        if (IsInstalled(browserDirectory, markerPath))
        {
            progress?.Report(new MediaToolProgress(1, "Managed Chromium is ready."));
            return;
        }

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            var versionDirectory = Path.GetDirectoryName(browserDirectory)!;
            Directory.CreateDirectory(versionDirectory);
            await using var processLock = await AcquireProcessLockAsync(
                Path.Combine(versionDirectory, ".playwright-1.63.0.install.lock"), cancellationToken);
            if (IsInstalled(browserDirectory, markerPath))
            {
                progress?.Report(new MediaToolProgress(1, "Managed Chromium is ready."));
                return;
            }
            Directory.CreateDirectory(browserDirectory);
            progress?.Report(new MediaToolProgress(0.05, "Installing the pinned Chromium runtime…"));
            logger.LogInformation("Installing the Playwright {Version} Chromium revision into {BrowserDirectory}.",
                Version, browserDirectory);

            var exitCode = await Task.Run(
                () => Microsoft.Playwright.Program.Main(["install", "chromium"]),
                cancellationToken);
            if (exitCode != 0 || !ContainsChromium(browserDirectory))
                throw new InvalidOperationException(
                    $"Playwright could not provision its Chromium {Version} runtime in the managed tools directory (exit code {exitCode}).");

            await File.WriteAllTextAsync(markerPath,
                $"{{\"playwrightVersion\":\"{Version}\",\"installedAt\":\"{DateTimeOffset.UtcNow:O}\"}}\n",
                cancellationToken);
            progress?.Report(new MediaToolProgress(1, "Managed Chromium is ready."));
            logger.LogInformation("Playwright Chromium {Version} is ready in the managed tool cache.", Version);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static bool IsInstalled(string directory, string markerPath) =>
        File.Exists(markerPath) && ContainsChromium(directory);

    private string BrowserDirectory(string? toolsRoot) =>
        Path.Combine(Path.GetFullPath(toolsRoot ?? toolDirectory.Root), "playwright", Version);

    private static bool ContainsChromium(string directory) => Directory.Exists(directory) &&
        (Directory.EnumerateDirectories(directory, "chromium-*", SearchOption.TopDirectoryOnly).Any() ||
         Directory.EnumerateDirectories(directory, "chromium_headless_shell-*", SearchOption.TopDirectoryOnly).Any());

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
            catch (UnauthorizedAccessException) when (
                OperatingSystem.IsLinux() &&
                string.Equals(Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_BOOTSTRAP_EXTERNAL"), "true", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }
}
