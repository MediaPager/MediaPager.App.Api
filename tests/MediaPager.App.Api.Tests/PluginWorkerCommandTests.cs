using MediaPager.App.Api.Services.Workers;
using MediaPager.App.PluginContracts;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class PluginWorkerCommandTests
{
    [Fact]
    public async Task ActionListIsEmptyForPluginsWithoutTheOptionalActionCapability()
    {
        var result = await PluginWorkerCommand.DispatchAsync(
            new MinimalPlugin(),
            new PluginWorkerMessage("request", "actions-1", Operation: "actions.list"),
            CancellationToken.None);

        Assert.Equal(System.Text.Json.JsonValueKind.Array, result.ValueKind);
        Assert.Empty(result.EnumerateArray());
    }

    [Fact]
    public async Task WorkerProxyDoesNotStartAnRpcForAnAbsentActionCapability()
    {
        var description = new PluginWorkerDescription(
            new PluginDescriptor("test.stream", "Minimal stream", "1.0.0"),
            IsStreamProvider: true,
            HasSettingsSchema: false,
            HasActions: false,
            HasNavigation: false,
            Sources: [],
            Mode: StreamProviderMode.Online,
            SupportedKinds: [],
            Settings: [],
            Actions: [],
            SettingsNav: null,
            MainNav: [],
            Scopes: [],
            Agents: [],
            UserSettings: []);
        using var proxy = new PluginWorkerStreamProviderProxy(
            ".",
            description,
            () => throw new InvalidOperationException("No worker RPC should be needed."),
            () => { });

        Assert.Empty(await proxy.GetActionsAsync(CancellationToken.None));
    }

    private sealed class MinimalPlugin : IMediaPagerPlugin
    {
        public PluginDescriptor Descriptor { get; } = new("test.minimal", "Minimal", "1.0.0");
    }
}
