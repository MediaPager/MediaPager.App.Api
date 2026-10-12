using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Standalone developer bootstrap used by dev.sh to prepare the same pinned SDK
/// that PluginDeployer uses, before local Community sources are published.</summary>
internal static class ManagedPluginBuildSdkCommand
{
    public static bool IsBootstrapMode(string[] args) =>
        args.Length > 0 && args[0] == "--bootstrap-plugin-build-sdk";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Plugin SDK bootstrap expects: --bootstrap-plugin-build-sdk <tools-directory> <sdk-path-file>.");
            return 2;
        }

        var toolsDirectory = Path.GetFullPath(args[1]);
        var sdkPathFile = Path.GetFullPath(args[2]);
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tools:Directory"] = toolsDirectory })
            .Build();
        var provider = new ManagedPluginBuildSdkProvider(
            new ManagedToolDirectory(configuration),
            httpClient,
            loggerFactory.CreateLogger<ManagedPluginBuildSdkProvider>());

        try
        {
            var sdk = await provider.EnsureInstalledAsync(new InlineProgress<PluginBuildSdkProgress>(update =>
                Console.Error.WriteLine($"[plugin-sdk] {update.Message}")));
            Directory.CreateDirectory(Path.GetDirectoryName(sdkPathFile)!);
            var temporaryPath = sdkPathFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, sdk.Executable + Environment.NewLine);
                File.Move(temporaryPath, sdkPathFile, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[plugin-sdk] Could not prepare the pinned plugin-build SDK: {exception.Message}");
            return 1;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
