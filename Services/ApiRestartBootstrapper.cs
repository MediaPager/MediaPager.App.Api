using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace MediaPager.App.Api.Services;

internal static class ApiRestartBootstrapper
{
    private const string BootstrapArgument = "--bootstrap-api-restart";
    private const string PendingRemovalMarker = ".uninstalling";

    public static bool IsBootstrapMode(string[] args) =>
        args.Length > 0 && string.Equals(args[0], BootstrapArgument, StringComparison.Ordinal);

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out var parentProcessId))
            throw new ArgumentException("Invalid API restart bootstrap arguments.");

        var communityDirectory = Path.GetFullPath(args[2]);
        var startupArguments = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(args[3])) ?? [];
        await WaitForExitAsync(parentProcessId);
        CleanupPendingUninstalls(communityDirectory, Console.Error.WriteLine);

        var apiAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(apiAssembly) || !File.Exists(apiAssembly))
            throw new InvalidOperationException("The API assembly could not be located for restart.");

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The API executable could not be located for restart.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
        };
        if (IsDotnetHost(executable)) startInfo.ArgumentList.Add(apiAssembly);
        foreach (var argument in startupArguments) startInfo.ArgumentList.Add(argument);

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("The API process could not be restarted.");
    }

    public static void CleanupPendingUninstalls(string communityDirectory, Action<string>? log = null)
    {
        if (!Directory.Exists(communityDirectory)) return;
        foreach (var pluginDirectory in Directory.EnumerateDirectories(communityDirectory))
        {
            if (!File.Exists(Path.Combine(pluginDirectory, PendingRemovalMarker))) continue;
            try
            {
                Directory.Delete(pluginDirectory, recursive: true);
                log?.Invoke($"[plugins] removed pending uninstall {Path.GetFileName(pluginDirectory)}");
            }
            catch (Exception exception)
            {
                log?.Invoke($"[plugins] could not finish pending uninstall for {Path.GetFileName(pluginDirectory)}: {exception.Message}");
            }
        }
    }

    public static void RemoveOrSchedule(string pluginDirectory, Action<Exception>? onFailure = null)
    {
        if (!Directory.Exists(pluginDirectory)) return;
        try
        {
            File.WriteAllText(Path.Combine(pluginDirectory, PendingRemovalMarker), $"uninstalling {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            Directory.Delete(pluginDirectory, recursive: true);
        }
        catch (Exception exception)
        {
            onFailure?.Invoke(exception);
            try
            {
                if (Directory.Exists(pluginDirectory))
                    File.WriteAllText(Path.Combine(pluginDirectory, PendingRemovalMarker), $"uninstalling {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            }
            catch (Exception markerException)
            {
                onFailure?.Invoke(markerException);
            }
        }
    }

    private static async Task WaitForExitAsync(int processId)
    {
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static bool IsDotnetHost(string executable) =>
        string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
}
