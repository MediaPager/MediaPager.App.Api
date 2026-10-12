using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Starts only the standalone tools installer with OS authorization. The API host
/// remains at the caller's normal privilege level.</summary>
internal static class ElevatedManagedToolsBootstrap
{
    public static Process Start(string apiAssembly, string toolsDirectory, string statusFile)
    {
        var workerArguments = GetBootstrapArguments(apiAssembly, toolsDirectory, statusFile, GetCallerIdentity());
        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = workerArguments.Executable,
                WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
                UseShellExecute = true,
                Verb = "runas",
            };
            foreach (var argument in workerArguments.Arguments) startInfo.ArgumentList.Add(argument);
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the elevated tools installer.");
        }

        if (OperatingSystem.IsMacOS())
        {
            var commandLine = string.Join(' ', new[] { workerArguments.Executable }
                .Concat(workerArguments.Arguments).Select(QuoteShellArgument));
            var script = $"do shell script {QuoteAppleScript(commandLine)} with administrator privileges";
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("macOS did not start the administrator-authorized tools installer.");
        }

        if (OperatingSystem.IsLinux())
        {
            const string policyKit = "/usr/bin/pkexec";
            if (!File.Exists(policyKit))
                throw new PlatformNotSupportedException(
                    "The tools directory is not writable and pkexec is unavailable. Configure a writable Tools:Directory.");
            var startInfo = new ProcessStartInfo
            {
                FileName = policyKit,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
            };
            startInfo.ArgumentList.Add(workerArguments.Executable);
            foreach (var argument in workerArguments.Arguments) startInfo.ArgumentList.Add(argument);
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Linux did not start the administrator-authorized tools installer.");
        }

        throw new PlatformNotSupportedException("No supported privilege prompt is available for this operating system.");
    }

    private static (string Executable, IReadOnlyList<string> Arguments) GetBootstrapArguments(
        string apiAssembly,
        string toolsDirectory,
        string statusFile,
        string owner)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The API process path is unavailable.");
        var runningApiAppHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath), "MediaPager.App.Api", StringComparison.OrdinalIgnoreCase);
        var executable = runningApiAppHost
            ? processPath
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(executable))
            throw new FileNotFoundException("Could not locate the host executable for the tools installer.", executable);

        var arguments = new List<string>();
        if (!runningApiAppHost) arguments.Add(apiAssembly);
        arguments.Add("--bootstrap-managed-tools");
        arguments.Add(toolsDirectory);
        arguments.Add(statusFile);
        arguments.Add(owner);
        return (executable, arguments);
    }

    private static string GetCallerIdentity()
    {
        if (OperatingSystem.IsWindows())
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        return $"{GetUid()}:{GetGid()}";
    }

    private static string QuoteShellArgument(string argument) =>
        "'" + argument.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string QuoteAppleScript(string commandLine) =>
        "\"" + commandLine.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "getgid")]
    private static extern uint GetGid();
}
