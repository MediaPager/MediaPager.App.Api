using MediaPager.App.Api.Services;
using MediaPager.App.Api.Services.Workers;
using MediaPager.App.Core.Plugins;
using MediaPager.App.PluginContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class PluginJobsServiceDisableTests
{
    [Fact]
    public void UninstallingAlreadyDisabledCommunityPluginRemovesItsInstalledFolder()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mediapager-plugin-uninstall-disabled-{Guid.NewGuid():N}");
        var pluginsRoot = Path.Combine(temporaryRoot, "plugins");
        var communityRoot = Path.Combine(pluginsRoot, "community");
        const string assembly = "MediaPager.Plugins.Stream.Sample";
        var pluginDirectory = Path.Combine(communityRoot, assembly);
        Directory.CreateDirectory(pluginDirectory);

        try
        {
            File.WriteAllText(Path.Combine(pluginDirectory, $"{assembly}.dll"), "test assembly");
            File.WriteAllText(Path.Combine(pluginDirectory, $"{assembly}.manifest.json"), """
                {
                  "id": "mediapager.stream.sample",
                  "name": "Sample",
                  "sdk": "MediaPager.App.PluginContracts",
                  "sdkVersion": "0.1.0",
                  "types": "stream"
                }
                """);
            File.WriteAllText(Path.Combine(pluginDirectory, ".disabled"), string.Empty);

            var registry = new PluginRegistry();
            registry.Reset([]);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Plugins:Directory"] = pluginsRoot,
                })
                .Build();
            var jobs = new PluginJobsService(
                services: null!,
                registry,
                deployer: null!,
                configuration,
                storageLayout: null!,
                toolBootstrapState: null!);

            var name = jobs.UninstallDisabledCommunityPlugin("mediapager.stream.sample");

            Assert.Equal("Sample", name);
            Assert.False(Directory.Exists(pluginDirectory));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void TurningOffCommunityPluginKeepsFilesAndMarksItDisabled()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mediapager-plugin-disable-{Guid.NewGuid():N}");
        var communityRoot = Path.Combine(temporaryRoot, "plugins", "community");
        var pluginDirectory = Path.Combine(communityRoot, "SamplePlugin");
        var uiDirectory = Path.Combine(pluginDirectory, "ui");
        Directory.CreateDirectory(uiDirectory);

        try
        {
            var plugin = new TestCommunityPlugin(
                new PluginDescriptor("sample.community", "Sample", "1.0.0"), uiDirectory);
            var registry = new PluginRegistry();
            registry.Reset([plugin], [plugin.Descriptor.Id]);
            registry.SetRequirements(plugin.Descriptor.Id, []);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Plugins:Directory"] = Path.Combine(temporaryRoot, "plugins"),
                })
                .Build();
            var jobs = new PluginJobsService(
                services: null!,
                registry,
                deployer: null!,
                configuration,
                storageLayout: null!,
                toolBootstrapState: null!);

            var result = jobs.Disable(plugin.Descriptor.Id);

            Assert.Contains("files are kept", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(pluginDirectory, ".disabled")));
            Assert.True(Directory.Exists(pluginDirectory));
            Assert.DoesNotContain(registry.Plugins, loaded => loaded.Descriptor.Id == plugin.Descriptor.Id);

            using var services = new ServiceCollection().BuildServiceProvider();
            Assert.Empty(PluginLoader.LoadFolder(services, pluginDirectory, official: false));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private sealed class TestCommunityPlugin(PluginDescriptor descriptor, string pluginUiDirectory)
        : IMediaPagerPlugin, IPluginUiAssetProvider
    {
        public PluginDescriptor Descriptor { get; } = descriptor;
        public string PluginUiDirectory { get; } = pluginUiDirectory;
    }
}
