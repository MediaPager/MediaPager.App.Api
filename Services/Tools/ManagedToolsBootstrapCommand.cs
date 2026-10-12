using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Privileged Docker entrypoint mode. It installs the same managed tools as the API
/// bootstrap service but publishes progress to a status file shared with the unprivileged API.</summary>
internal static class ManagedToolsBootstrapCommand
{
    public static bool IsBootstrapMode(string[] args) => args.Length > 0 && args[0] == "--bootstrap-managed-tools";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            Console.Error.WriteLine("Tools bootstrap expects: --bootstrap-managed-tools <tools-directory> <status-file> [owner].");
            return 2;
        }

        var toolsDirectory = Path.GetFullPath(args[1]);
        var statusFile = Path.GetFullPath(args[2]);
        var owner = args.Length == 4 ? args[3] : null;
        await ManagedToolsBootstrapStatusFile.WriteAsync(statusFile,
            new ManagedToolsBootstrapStatus("checking", null, 0, "Checking the persistent tools directory…", ToolDirectory: toolsDirectory));

        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tools:Directory"] = toolsDirectory })
            .Build();
        var directories = new ManagedToolDirectory(configuration);
        var ffmpeg = new FfmpegToolInstaller(httpClient, directories, loggerFactory.CreateLogger<FfmpegToolInstaller>());
        var chromium = new PlaywrightBrowserInstaller(directories, loggerFactory.CreateLogger<PlaywrightBrowserInstaller>());
        var provisioner = new ManagedToolsProvisioner(
            directories,
            ffmpeg,
            chromium,
            loggerFactory.CreateLogger<ManagedToolsProvisioner>());

        try
        {
            var progress = new InlineProgress<ManagedToolsBootstrapProgress>(update =>
                ManagedToolsBootstrapStatusFile.WriteAsync(statusFile,
                        new ManagedToolsBootstrapStatus(
                            update.NeedsInstallation ? "running" : "checking",
                            update.NeedsInstallation,
                            update.Progress,
                            update.Message,
                            ToolDirectory: directories.Root))
                    .GetAwaiter().GetResult());
            var result = await provisioner.EnsureInstalledAsync(progress);
            PrepareRuntimeOwnership(toolsDirectory, result.NeedsInstallation, owner);
            if (result.Failures.Count > 0)
            {
                var error = string.Join(" ", result.Failures);
                await ManagedToolsBootstrapStatusFile.WriteAsync(statusFile,
                    new ManagedToolsBootstrapStatus("failed", true, 1, "Managed tool installation failed.", error, result.ToolDirectory));
                PrepareStatusOwnership(statusFile, owner);
                return 1;
            }

            await ManagedToolsBootstrapStatusFile.WriteAsync(statusFile,
                new ManagedToolsBootstrapStatus("ready", result.NeedsInstallation, 1,
                    "Managed FFmpeg/FFprobe and Chromium are ready.", ToolDirectory: result.ToolDirectory));
            PrepareStatusOwnership(statusFile, owner);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[tools] managed tool bootstrap failed: {exception.Message}");
            await ManagedToolsBootstrapStatusFile.WriteAsync(statusFile,
                new ManagedToolsBootstrapStatus("failed", true, 1, "Managed tool installation failed.", exception.Message, directories.Root));
            PrepareStatusOwnership(statusFile, owner);
            return 1;
        }
    }

    private static void PrepareRuntimeOwnership(string toolsDirectory, bool toolsWereInstalled, string? owner)
    {
        var dockerBootstrap = OperatingSystem.IsLinux() &&
            string.Equals(Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_BOOTSTRAP_EXTERNAL"), "true", StringComparison.OrdinalIgnoreCase);
        if (!dockerBootstrap && owner is null)
            return;

        Directory.CreateDirectory(Path.Combine(toolsDirectory, "scratch"));
        if (OperatingSystem.IsWindows())
        {
            var runtimeUser = dockerBootstrap ? "mediapager" : owner!;
            RunAclTool(toolsDirectory, runtimeUser);
            return;
        }

        var runtimeOwner = dockerBootstrap
            ? $"{Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_RUNTIME_UID") ?? "10001"}:" +
              (Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_RUNTIME_GID") ?? "10001")
            : owner!;
        var startInfo = new ProcessStartInfo
        {
            FileName = "chown",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (toolsWereInstalled) startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add(runtimeOwner);
        startInfo.ArgumentList.Add(Path.GetFullPath(toolsDirectory));
        if (!toolsWereInstalled) startInfo.ArgumentList.Add(Path.Combine(Path.GetFullPath(toolsDirectory), "scratch"));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not set tool-cache ownership for the unprivileged API process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Could not set tool-cache ownership for the unprivileged API process: " +
                outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult());
    }

    private static void RunAclTool(string directory, string account)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(systemRoot, "System32", "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add($"{account}:(OI)(CI)M");
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("/Q");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not grant the desktop user access to the installed tools.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Could not grant the desktop user access to the installed tools: " +
                outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult());
    }

    private static void PrepareStatusOwnership(string statusFile, string? owner)
    {
        if (owner is null || OperatingSystem.IsWindows()) return;
        RunChown(owner, Path.GetFullPath(statusFile));
    }

    private static void RunChown(string owner, string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "chown",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(owner);
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not set the caller's ownership on the tools status file.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Could not set the caller's ownership on the tools status file: " +
                outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult());
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
