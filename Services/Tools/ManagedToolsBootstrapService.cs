using MediaPager.App.Core.Plugins;
using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Warms the version-pinned managed tool cache after the web host has started accepting
/// requests. Docker can delegate provisioning to its root entrypoint while this API process
/// remains unprivileged and mirrors the bootstrap status into Activity.</summary>
public sealed class ManagedToolsBootstrapService(
    ManagedToolDirectory directories,
    ManagedToolsProvisioner provisioner,
    IPluginBuildSdkProvider buildSdkProvider,
    PluginActivityStore activity,
    ManagedToolsBootstrapState bootstrapState,
    ILogger<ManagedToolsBootstrapService> logger) : BackgroundService
{
    private const string ActivityPluginId = "mediapager.tools";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync must return before tool setup begins so Kestrel and the
        // bundled SPA can serve the warm-up activity while tools are provisioned.
        await Task.Yield();
        try
        {
            var externalStatusPath = Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_BOOTSTRAP_STATUS");
            if (string.Equals(Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_BOOTSTRAP_EXTERNAL"), "true", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(externalStatusPath))
            {
                await FollowExternalBootstrapAsync(externalStatusPath, stoppingToken);
            }
            else
            {
                await BootstrapInCurrentUserAsync(stoppingToken);
            }
        }
        finally
        {
            bootstrapState.Complete();
        }

        // Keep the API/runtime startup independent of SDK setup. Once media tools are
        // ready (or their bootstrap has reported a failure), prepare the pinned plugin-build
        // SDK in the same managed-tools cache.
        await BootstrapPluginBuildSdkAsync(stoppingToken);
    }

    private async Task BootstrapPluginBuildSdkAsync(CancellationToken cancellationToken)
    {
        PluginJob? job = null;
        var progress = new InlineProgress<PluginBuildSdkProgress>(update =>
        {
            job ??= activity.CreateJob(ActivityPluginId, "Preparing the pinned plugin-build SDK");
            job.Report(update.Percent / 100d, update.Message);
        });
        try
        {
            var sdk = await buildSdkProvider.EnsureInstalledAsync(progress, cancellationToken);
            logger.LogInformation("Pinned plugin-build SDK {SdkVersion} is ready in {SdkDirectory}.",
                PluginBuildSdkVersion.Version, sdk.InstallDirectory);
            if (job is not null)
            {
                job.Complete($"Pinned .NET SDK {PluginBuildSdkVersion.Version} is ready for plugin builds.");
                activity.AddNotification(ActivityPluginId, "Plugin build tools are ready",
                    $"Pinned .NET SDK {PluginBuildSdkVersion.Version} is installed in the managed tools directory.",
                    PluginNotificationLevel.Success);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job?.Cancel();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not prepare the pinned plugin-build SDK; the API will remain available.");
            job ??= activity.CreateJob(ActivityPluginId, "Preparing the pinned plugin-build SDK");
            var error = new PluginError("tools.dotnet-sdk.failed", exception.Message,
                "Check tools-directory permissions and network access. Plugin installs will retry SDK setup.");
            job.FailWithError(error);
            activity.AddNotification(ActivityPluginId, "Plugin build tools need attention",
                "The API is available, but the pinned .NET SDK could not be prepared. Plugin installs will retry setup.",
                PluginNotificationLevel.Error, error);
        }
    }

    private async Task BootstrapInCurrentUserAsync(CancellationToken cancellationToken)
    {
        PluginJob? job = null;
        try
        {
            var progress = new InlineProgress<ManagedToolsBootstrapProgress>(update =>
            {
                if (!update.NeedsInstallation) return;
                job ??= activity.CreateJob(ActivityPluginId, "Preparing managed media tools");
                job.Report(update.Progress, update.Message);
            });
            var result = await provisioner.EnsureInstalledAsync(progress, cancellationToken);
            if (!result.NeedsInstallation) return;
            job ??= activity.CreateJob(ActivityPluginId, "Preparing managed media tools");
            if (result.RequiresElevation)
            {
                await RunElevatedBootstrapAsync(result.ToolDirectory, job, cancellationToken);
                return;
            }
            CompleteBootstrap(job, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job?.Cancel();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Managed tool bootstrap failed; the API will remain available.");
            var error = new PluginError("tools.bootstrap.failed", exception.Message,
                "Check filesystem permissions and API logs. Tools will be checked again at the next startup.");
            job?.FailWithError(error);
            activity.AddNotification(ActivityPluginId, "Managed tool setup needs attention",
                "The API and SPA are available, but the managed tools could not be verified. " +
                "Check the API logs for details.", PluginNotificationLevel.Error, error);
        }
    }

    private async Task RunElevatedBootstrapAsync(
        string toolsDirectory,
        PluginJob job,
        CancellationToken cancellationToken)
    {
        var statusPath = Path.Combine(Path.GetTempPath(), $"mediapager-tools-bootstrap-{Guid.NewGuid():N}.json");
        try
        {
            await ManagedToolsBootstrapStatusFile.WriteAsync(statusPath,
                new ManagedToolsBootstrapStatus("checking", true, 0, "Waiting for permission to install media tools…",
                    ToolDirectory: toolsDirectory), cancellationToken);
            job.Report(0.01, "Requesting administrator permission to install media tools…");
            using var installer = ElevatedManagedToolsBootstrap.Start(
                typeof(PluginWorkerCommand).Assembly.Location, toolsDirectory, statusPath);
            await FollowExternalBootstrapAsync(statusPath, cancellationToken, job, installer);
        }
        finally
        {
            try { if (File.Exists(statusPath)) File.Delete(statusPath); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task FollowExternalBootstrapAsync(
        string statusPath,
        CancellationToken cancellationToken,
        PluginJob? existingJob = null,
        System.Diagnostics.Process? externalProcess = null)
    {
        var job = existingJob;
        var startedAt = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await ManagedToolsBootstrapStatusFile.ReadAsync(statusPath, cancellationToken);
            if (status is not null)
            {
                if (status.NeedsInstallation == true && status.State is "running" or "ready" or "failed")
                {
                    job ??= activity.CreateJob(ActivityPluginId, "Preparing managed media tools");
                    if (status.State == "running")
                        job.Report(status.Progress, status.Message);
                    else if (status.State == "ready")
                    {
                        SelectRuntimeToolsDirectory(status.ToolDirectory);
                        CompleteBootstrap(job, new ManagedToolsBootstrapResult(status.ToolDirectory ?? "", true, []));
                    }
                    else
                        FailExternalBootstrap(job, status.Error ?? status.Message);
                    if (status.State is "ready" or "failed") return;
                }
                else if (status.State == "ready" && status.NeedsInstallation == false)
                {
                    SelectRuntimeToolsDirectory(status.ToolDirectory);
                    logger.LogInformation("The Docker tools volume already contains a valid tool cache at {ToolsDirectory}.",
                        status.ToolDirectory);
                    if (job is not null)
                        CompleteBootstrap(job,
                            new ManagedToolsBootstrapResult(status.ToolDirectory ?? "", false, []));
                    return;
                }
                else if (status.State == "failed")
                {
                    job ??= activity.CreateJob(ActivityPluginId, "Preparing managed media tools");
                    FailExternalBootstrap(job, status.Error ?? status.Message);
                    return;
                }
            }

            if (externalProcess is { HasExited: true } && status?.State is not ("ready" or "failed"))
            {
                job ??= activity.CreateJob(ActivityPluginId, "Preparing managed media tools");
                var reason = externalProcess.ExitCode == 0
                    ? "The elevated tools installer exited without reporting a final status."
                    : $"The elevated tools installer exited with code {externalProcess.ExitCode}. Permission may have been declined.";
                FailExternalBootstrap(job, reason);
                return;
            }

            if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromMinutes(10))
            {
                job ??= activity.CreateJob(ActivityPluginId, "Preparing managed media tools");
                FailExternalBootstrap(job, "The Docker tools bootstrapper did not report completion within ten minutes.");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        job?.Cancel();
    }

    private void SelectRuntimeToolsDirectory(string? toolsDirectory)
    {
        if (string.IsNullOrWhiteSpace(toolsDirectory)) return;

        var selectedRoot = Path.GetFullPath(toolsDirectory);
        directories.SelectRoot(selectedRoot);
        var browserDirectory = PlaywrightBrowserInstaller.ConfigureBrowserPath(selectedRoot);
        logger.LogInformation("Using managed tools directory {ToolsDirectory} and Playwright browser directory {BrowserDirectory}.",
            selectedRoot, browserDirectory);
    }

    private void CompleteBootstrap(PluginJob job, ManagedToolsBootstrapResult result)
    {
        if (result.Failures.Count > 0)
        {
            var message = string.Join(" ", result.Failures);
            var error = new PluginError("tools.install.failed", message,
                "Check network access and API logs. The affected feature will retry setup when used.");
            job.FailWithError(error);
            activity.AddNotification(ActivityPluginId, "Managed tool setup needs attention",
                "The API and SPA are available, but one or more media tools could not be installed. " +
                "The affected feature will retry setup when used.", PluginNotificationLevel.Error, error);
            return;
        }

        job.Complete("FFmpeg/FFprobe and Chromium are ready.");
        activity.AddNotification(ActivityPluginId, "Managed tools are ready",
            "FFmpeg, FFprobe, and Chromium are installed in the managed tools directory.",
            PluginNotificationLevel.Success);
        logger.LogInformation("Managed tools are ready in {ToolsDirectory}.", result.ToolDirectory);
    }

    private void FailExternalBootstrap(PluginJob job, string message)
    {
        var error = new PluginError("tools.bootstrap.failed", message,
            "Check the Docker bootstrap logs and tools-volume permissions. Features may retry setup when used.");
        job.FailWithError(error);
        activity.AddNotification(ActivityPluginId, "Managed tool setup needs attention",
            "The API and SPA are available, but the Docker tools bootstrapper did not finish successfully.",
            PluginNotificationLevel.Error, error);
        logger.LogError("External managed-tools bootstrap failed: {Message}", message);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
