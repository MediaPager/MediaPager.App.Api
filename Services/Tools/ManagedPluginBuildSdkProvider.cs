using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MediaPager.App.Core.Services;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Installs the pinned SDK used only for on-demand plugin builds. It lives in the
/// persistent managed-tools directory; the API itself continues to run on its normal .NET
/// runtime and local `dotnet run` continues to use the developer's installed SDK.</summary>
public sealed class ManagedPluginBuildSdkProvider(
    ManagedToolDirectory toolDirectory,
    HttpClient httpClient,
    ILogger<ManagedPluginBuildSdkProvider> logger) : IPluginBuildSdkProvider
{
    private const string ScriptBaseUrl = "https://dot.net/v1/dotnet-install.";
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(400);

    public async Task<PluginBuildSdk> EnsureInstalledAsync(
        IProgress<PluginBuildSdkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var toolsRoot = Path.GetFullPath(toolDirectory.Root);
        var sdkParent = Path.Combine(toolsRoot, "dotnet-sdk");
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var installDirectory = Path.Combine(sdkParent, PluginBuildSdkVersion.Version, runtimeIdentifier);
        var executable = Path.Combine(installDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var packagesDirectory = Path.Combine(toolsRoot, "nuget", "packages");
        var manifestPath = Path.Combine(installDirectory, "mediapager-sdk-manifest.json");

        if (IsInstalled(executable, manifestPath))
            return new PluginBuildSdk(executable, installDirectory, packagesDirectory);

        Directory.CreateDirectory(sdkParent);
        await using var processLock = await AcquireProcessLockAsync(
            Path.Combine(sdkParent, $".{PluginBuildSdkVersion.Version}-{runtimeIdentifier}.install.lock"),
            cancellationToken);
        if (IsInstalled(executable, manifestPath))
            return new PluginBuildSdk(executable, installDirectory, packagesDirectory);

        var stagingDirectory = Path.Combine(sdkParent,
            $".{PluginBuildSdkVersion.Version}-{runtimeIdentifier}.installing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            progress?.Report(new PluginBuildSdkProgress(5,
                $"Preparing pinned .NET SDK {PluginBuildSdkVersion.Version} for plugin builds…"));
            var installerScript = await DownloadInstallerAsync(stagingDirectory, cancellationToken);
            var stagedInstallDirectory = Path.Combine(stagingDirectory, "sdk");
            Directory.CreateDirectory(stagedInstallDirectory);

            progress?.Report(new PluginBuildSdkProgress(12,
                $"Installing pinned .NET SDK {PluginBuildSdkVersion.Version} into the tools cache…"));
            await RunInstallerAsync(installerScript, stagedInstallDirectory, progress, cancellationToken);

            var stagedExecutable = Path.Combine(stagedInstallDirectory,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            await VerifySdkAsync(stagedExecutable, stagedInstallDirectory, cancellationToken);
            progress?.Report(new PluginBuildSdkProgress(96, "Finalizing the managed .NET SDK cache…"));

            var finalParent = Path.GetDirectoryName(installDirectory)!;
            Directory.CreateDirectory(finalParent);
            if (Directory.Exists(installDirectory))
                Directory.Delete(installDirectory, recursive: true);
            Directory.Move(stagedInstallDirectory, installDirectory);
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(new ManagedSdkManifest(
                    PluginBuildSdkVersion.Version, runtimeIdentifier, DateTimeOffset.UtcNow)) + "\n",
                cancellationToken);

            logger.LogInformation("Installed pinned .NET SDK {SdkVersion} ({RuntimeIdentifier}) in {InstallDirectory}.",
                PluginBuildSdkVersion.Version, runtimeIdentifier, installDirectory);
            progress?.Report(new PluginBuildSdkProgress(100,
                $"Pinned .NET SDK {PluginBuildSdkVersion.Version} is ready for plugin builds."));
            return new PluginBuildSdk(executable, installDirectory, packagesDirectory);
        }
        catch
        {
            try { if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, recursive: true); }
            catch (IOException) { }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<string> DownloadInstallerAsync(string stagingDirectory, CancellationToken cancellationToken)
    {
        var scriptName = OperatingSystem.IsWindows() ? "dotnet-install.ps1" : "dotnet-install.sh";
        var scriptPath = Path.Combine(stagingDirectory, scriptName);
        using var response = await httpClient.GetAsync(
            ScriptBaseUrl + (OperatingSystem.IsWindows() ? "ps1" : "sh"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        return scriptPath;
    }

    private async Task RunInstallerAsync(
        string scriptPath,
        string installDirectory,
        IProgress<PluginBuildSdkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Version");
            startInfo.ArgumentList.Add(PluginBuildSdkVersion.Version);
            startInfo.ArgumentList.Add("-InstallDir");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("-NoPath");
        }
        else
        {
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(PluginBuildSdkVersion.Version);
            startInfo.ArgumentList.Add("--install-dir");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--no-path");
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Could not start the .NET SDK installer.");

        var stdoutTask = CaptureInstallerOutputAsync(process.StandardOutput, progress, cancellationToken);
        var stderrTask = CaptureInstallerOutputAsync(process.StandardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var output = await stdoutTask;
        var error = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"The pinned .NET SDK installer exited {process.ExitCode}: {detail.Trim()}");
        }
    }

    private async Task<string> CaptureInstallerOutputAsync(
        StreamReader reader,
        IProgress<PluginBuildSdkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (lines.Count == 60) lines.RemoveAt(0);
            lines.Add(line);
            logger.LogDebug(".NET SDK installer: {Line}", line);
            if (line.Contains("download", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new PluginBuildSdkProgress(20, "Downloading the pinned .NET SDK…"));
            else if (line.Contains("extract", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new PluginBuildSdkProgress(80, "Extracting the pinned .NET SDK…"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task VerifySdkAsync(
        string executable,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable) ||
            !File.Exists(Path.Combine(installDirectory, "sdk", PluginBuildSdkVersion.Version, "dotnet.dll")))
            throw new InvalidOperationException("The pinned .NET SDK installer completed without a usable SDK.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        SetSdkEnvironment(startInfo, installDirectory);
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not verify the installed .NET SDK.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        if (process.ExitCode != 0 || !string.Equals(output, PluginBuildSdkVersion.Version, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected pinned .NET SDK {PluginBuildSdkVersion.Version}, got '{output}'. " +
                (await errorTask).Trim());
        _ = await errorTask;
    }

    private static void SetSdkEnvironment(ProcessStartInfo startInfo, string installDirectory)
    {
        startInfo.Environment["DOTNET_ROOT"] = installDirectory;
        startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
    }

    private static bool IsInstalled(string executable, string manifestPath) =>
        File.Exists(executable) && File.Exists(manifestPath);

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
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
    }

    private sealed record ManagedSdkManifest(string Version, string RuntimeIdentifier, DateTimeOffset InstalledAt);
}
