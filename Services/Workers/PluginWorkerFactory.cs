using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Services.Workers;

/// <summary>Starts manifest-selected plugins out of process and returns host-side proxies
/// for their declared capabilities. Workers use platform restrictions where available, but
/// still run under the host's OS identity.</summary>
public sealed class PluginWorkerFactory(ILoggerFactory loggerFactory) : IPluginWorkerFactory, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginWorkerProcess> _workers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public bool ShouldRunInWorker(PluginManifest? manifest, bool official) => manifest?.Worker == true;

    public IReadOnlyList<IMediaPagerPlugin> LoadWorker(
        IServiceProvider hostServices,
        string pluginDirectory,
        string pluginId,
        IReadOnlySet<string> hostCapabilities)
    {
        var settings = hostServices.GetRequiredService<IPluginSettingsRepository>();
        var activityStore = hostServices.GetRequiredService<PluginActivityStore>();
        var activity = new PluginActivity(activityStore) { PluginId = pluginId };
        var browser = hostServices.GetRequiredService<IBrowserAutomationBroker>();
        var mediaTools = hostServices.GetRequiredService<IMediaToolBroker>();
        var workerDescription = StartWorker(
            pluginDirectory, pluginId, settings, activity, browser, mediaTools, activityStore, hostCapabilities);
        var lease = new WorkerLease(
            () => GetOrRestartWorker(pluginDirectory, pluginId, settings, activity, browser, mediaTools, activityStore, hostCapabilities),
            () => StopWorker(pluginId));

        if (workerDescription.IsStreamProvider)
            return [new PluginWorkerStreamProviderProxy(pluginDirectory, workerDescription, lease.GetWorker, lease.Dispose)];
        if (workerDescription.HasActions)
            return [new PluginWorkerActionProxy(pluginDirectory, workerDescription, lease.GetWorker, lease.Dispose)];
        lease.Dispose();
        throw new InvalidOperationException($"Worker '{pluginId}' exposes no supported host capability.");
    }

    private PluginWorkerDescription StartWorker(
        string pluginDirectory,
        string pluginId,
        IPluginSettingsRepository settings,
        IPluginActivity activity,
        IBrowserAutomationBroker browser,
        IMediaToolBroker mediaTools,
        PluginActivityStore activityStore,
        IReadOnlySet<string> hostCapabilities)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workers.TryGetValue(pluginId, out var existing))
            {
                if (existing.IsRunning)
                    throw new InvalidOperationException($"A worker for '{pluginId}' is already active.");
                _workers.Remove(pluginId);
                existing.Dispose();
            }

            var worker = CreateWorker(pluginDirectory, pluginId, settings, activity, browser, mediaTools, activityStore, hostCapabilities);
            try
            {
                var description = worker.StartAndDescribe();
                if (!string.Equals(description.Descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Worker identity mismatch: manifest '{pluginId}', worker '{description.Descriptor.Id}'.");
                _workers.Add(pluginId, worker);
                return description;
            }
            catch
            {
                worker.Dispose();
                throw;
            }
        }
    }

    private PluginWorkerProcess GetOrRestartWorker(
        string pluginDirectory,
        string pluginId,
        IPluginSettingsRepository settings,
        IPluginActivity activity,
        IBrowserAutomationBroker browser,
        IMediaToolBroker mediaTools,
        PluginActivityStore activityStore,
        IReadOnlySet<string> hostCapabilities)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workers.TryGetValue(pluginId, out var worker) && worker.IsRunning)
                return worker;
            if (worker is not null)
            {
                _workers.Remove(pluginId);
                worker.Dispose();
            }

            var replacement = CreateWorker(pluginDirectory, pluginId, settings, activity, browser, mediaTools, activityStore, hostCapabilities);
            try
            {
                var description = replacement.StartAndDescribe();
                if (!string.Equals(description.Descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The restarted worker reported a different plugin id.");
                _workers.Add(pluginId, replacement);
                return replacement;
            }
            catch
            {
                replacement.Dispose();
                throw;
            }
        }
    }

    private PluginWorkerProcess CreateWorker(
        string pluginDirectory,
        string pluginId,
        IPluginSettingsRepository settings,
        IPluginActivity activity,
        IBrowserAutomationBroker browser,
        IMediaToolBroker mediaTools,
        PluginActivityStore activityStore,
        IReadOnlySet<string> hostCapabilities) =>
        new(pluginDirectory, pluginId, settings, activity, browser, mediaTools,
            hostCapabilities,
            jobId => { activityStore.CancelJob(jobId); },
            loggerFactory.CreateLogger($"PluginWorker.{pluginId}"));

    private void StopWorker(string pluginId)
    {
        PluginWorkerProcess? worker = null;
        lock (_gate)
        {
            if (_workers.Remove(pluginId, out var removed)) worker = removed;
        }
        worker?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        PluginWorkerProcess[] workers;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            workers = _workers.Values.ToArray();
            _workers.Clear();
        }
        foreach (var worker in workers) worker.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class WorkerLease(Func<PluginWorkerProcess> getWorker, Action stopWorker) : IDisposable
    {
        private readonly object _sync = new();
        private bool _disposed;

        public PluginWorkerProcess GetWorker()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return getWorker();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            stopWorker();
        }
    }
}

internal sealed class PluginWorkerStreamProviderProxy(
    string pluginDirectory,
    PluginWorkerDescription description,
    Func<PluginWorkerProcess> getWorker,
    Action stopWorker) : IStreamProviderPlugin, IPluginSettingsSchema, IPluginActions, IPluginUiAssetProvider, IDisposable
{
    public PluginDescriptor Descriptor => description.Descriptor;
    public string PluginUiDirectory => Path.Combine(pluginDirectory, "ui");
    public IReadOnlyList<SourceDescriptor> Sources => description.Sources;
    public StreamProviderMode Mode => description.Mode;
    public IReadOnlyList<MediaKind> SupportedKinds => description.SupportedKinds;
    public IReadOnlyList<PluginSettingDefinition> Settings => description.Settings;
    public IReadOnlyList<PluginActionDescriptor> Actions => description.Actions;

    public Task<IReadOnlyList<PluginActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken) =>
        getWorker().InvokeAsync<IReadOnlyList<PluginActionDescriptor>>("actions.list", new { }, cancellationToken);

    public async Task InvokeActionAsync(PluginActionContext context, CancellationToken cancellationToken) =>
        _ = await getWorker().InvokeAsync<bool>("actions.invoke", new PluginWorkerActionArguments(context), cancellationToken);

    public async Task<Paged<BrowseItem>> BrowseAsync(
        string sourceKey,
        string? query,
        int page,
        CancellationToken cancellationToken)
    {
        var result = await getWorker().InvokeAsync<Paged<BrowseItem>>(
            "stream.browse", new PluginWorkerBrowseArguments(sourceKey, query, page), cancellationToken);
        return result ?? new Paged<BrowseItem>([]);
    }

    public Task<TitleDetails?> GetDetailsAsync(
        string sourceKey,
        string externalId,
        CancellationToken cancellationToken) =>
        getWorker().InvokeAsync<TitleDetails?>(
            "stream.details", new PluginWorkerDetailsArguments(sourceKey, externalId), cancellationToken);

    public Task<StreamResult?> ResolveAsync(StreamResolveRequest request, CancellationToken cancellationToken) =>
        getWorker().InvokeAsync<StreamResult?>(
            "stream.resolve", new PluginWorkerResolveArguments(request), cancellationToken);

    public void Dispose() => stopWorker();
}

internal sealed class PluginWorkerActionProxy(
    string pluginDirectory,
    PluginWorkerDescription description,
    Func<PluginWorkerProcess> getWorker,
    Action stopWorker) : IMediaPagerPlugin, IPluginActions, IPluginSettingsSchema, IPluginNavigation, IPluginUiAssetProvider, IDisposable
{
    public PluginDescriptor Descriptor => description.Descriptor;
    public string PluginUiDirectory => Path.Combine(pluginDirectory, "ui");
    public IReadOnlyList<PluginActionDescriptor> Actions => description.Actions;
    public IReadOnlyList<PluginSettingDefinition> Settings => description.Settings;
    public PluginSettingsNav? SettingsNav => description.SettingsNav;
    public IReadOnlyList<PluginMenuEntry> MainNav => description.MainNav;

    public Task<IReadOnlyList<PluginActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken) =>
        getWorker().InvokeAsync<IReadOnlyList<PluginActionDescriptor>>("actions.list", new { }, cancellationToken);

    public async Task InvokeActionAsync(PluginActionContext context, CancellationToken cancellationToken) =>
        _ = await getWorker().InvokeAsync<bool>("actions.invoke", new PluginWorkerActionArguments(context), cancellationToken);

    public void Dispose() => stopWorker();
}
