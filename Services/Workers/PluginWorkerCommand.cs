using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MediaPager.App.Core.Plugins;
using MediaPager.App.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace MediaPager.App.Api.Services.Workers;

internal static class PluginWorkerCommand
{
    public static bool IsWorkerMode(string[] args) => args.Length > 0 && args[0] == "--plugin-worker";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 3 or > 5)
        {
            Console.Error.WriteLine("Plugin worker expects: --plugin-worker <assembly-path> <plugin-id> [worker-home] [host-capabilities].");
            return 2;
        }

        var assemblyPath = Path.GetFullPath(args[1]);
        var pluginId = args[2];
        var workerHome = args.Length >= 4
            ? Path.GetFullPath(args[3])
            : Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
              ?? Path.GetTempPath();
        var hostCapabilities = args.Length == 5
            ? args[4].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            ClearWorkerEnvironment(workerHome);
            ApplyWindowsWorkerRestrictions();
        }
        Console.Error.WriteLine($"[plugin-worker:{pluginId}] starting protocol v{PluginWorkerProtocol.CurrentVersion}.");
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);

        try
        {
            var pluginDirectory = Path.GetDirectoryName(assemblyPath)!;
            if (OperatingSystem.IsLinux())
                LinuxWorkerSyscallPolicy.Apply(
                    pluginDirectory,
                    workerHome);

            foreach (var dependencyPath in Directory.EnumerateFiles(pluginDirectory, "*.dll"))
            {
                if (string.Equals(Path.GetFullPath(dependencyPath), assemblyPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var dependencyName = Path.GetFileNameWithoutExtension(dependencyPath);
                if (AssemblyLoadContext.Default.Assemblies.Any(loaded =>
                        string.Equals(loaded.GetName().Name, dependencyName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try { AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath); }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[plugin-worker:{pluginId}] could not preload {dependencyName}: {exception.Message}");
                }
            }
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            Console.Error.WriteLine($"[plugin-worker:{pluginId}] loaded assembly.");
            var host = new PluginWorkerHostClient(Console.In, output);
            var services = new PluginWorkerServiceProvider(host, pluginId, hostCapabilities);
            var plugins = new List<IMediaPagerPlugin>();
            foreach (var type in assembly.GetExportedTypes())
            {
                if (!typeof(IMediaPagerPlugin).IsAssignableFrom(type) ||
                    type is not { IsAbstract: false, IsClass: true, IsNested: false })
                    continue;
                var instance = ActivatorUtilities.CreateInstance(services, type);
                if (instance is IMediaPagerPlugin plugin &&
                    string.Equals(plugin.Descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    plugins.Add(plugin);
                else if (instance is IDisposable disposable)
                    disposable.Dispose();
            }

            if (plugins.Count != 1)
                throw new InvalidOperationException(
                    $"Worker '{pluginId}' must expose exactly one matching plugin implementation.");

            var workerPlugin = plugins[0];
            Console.Error.WriteLine($"[plugin-worker:{pluginId}] constructed plugin instance.");
            var stream = plugins[0] as IStreamProviderPlugin;
            var schema = plugins[0] as IPluginSettingsSchema;
            var actionProvider = plugins[0] as IPluginActions;
            var agentProvider = plugins[0] as IAgentPlugin;
            var searchProvider = plugins[0] as ISearchProviderPlugin;
            var navigation = plugins[0] as IPluginNavigation;
            var scopeSchema = plugins[0] as IPluginScopeSchema;
            var userSettingsSchema = plugins[0] as IPluginUserSettingsSchema;
            if (stream is null && actionProvider is null && agentProvider is null && searchProvider is null)
                throw new InvalidOperationException($"Worker '{pluginId}' does not expose a supported capability.");

            // Start the duplex pipe reader before querying plugin actions: some plugins
            // ask host brokers while computing availability during GetActionsAsync.
            var workerLoop = Task.Run(() => host.RunAsync((request, cancellationToken) =>
                DispatchAsync(workerPlugin, request, cancellationToken)));
            var actions = actionProvider is null
                ? []
                : (await actionProvider.GetActionsAsync(CancellationToken.None)).ToArray();
            var agents = agentProvider is null
                ? []
                : (await agentProvider.GetAgentsAsync(CancellationToken.None)).ToArray();
            Console.Error.WriteLine($"[plugin-worker:{pluginId}] prepared worker description.");
            var description = new PluginWorkerDescription(
                workerPlugin.Descriptor,
                stream is not null,
                schema is not null,
                actionProvider is not null,
                navigation is not null,
                stream?.Sources.ToArray() ?? [],
                stream?.Mode ?? StreamProviderMode.Local,
                stream?.SupportedKinds.ToArray() ?? [],
                schema?.Settings.ToArray() ?? [],
                actions,
                navigation?.SettingsNav,
                navigation?.MainNav.ToArray() ?? [],
                scopeSchema?.Scopes.ToArray() ?? [],
                agentProvider is not null,
                agents,
                searchProvider is not null,
                userSettingsSchema?.UserSettings.ToArray() ?? []);
            await host.SendAsync(new PluginWorkerMessage(
                "ready", "ready", Result: PluginWorkerJson.ToElement(description)));
            Console.Error.WriteLine($"[plugin-worker:{pluginId}] sent worker ready message.");
            await workerLoop;

            foreach (var plugin in plugins)
            {
                if (plugin is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (plugin is IDisposable disposable)
                    disposable.Dispose();
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[plugin-worker:{pluginId}] {exception}");
            try
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new PluginWorkerMessage(
                    "ready", "ready", Success: false, ErrorCode: "worker.start.failed", ErrorMessage: exception.Message),
                    PluginWorkerJson.Options));
            }
            catch { }
            return 1;
        }
    }

    private static void ClearWorkerEnvironment(string workerHome)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR") ?? systemRoot;
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            if (!key.StartsWith('=') && !string.Equals(key, "SystemRoot", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "WINDIR", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(key, null);
        }

        Environment.SetEnvironmentVariable("PATH", string.Empty);
        Environment.SetEnvironmentVariable("SystemRoot", systemRoot);
        Environment.SetEnvironmentVariable("WINDIR", windowsDirectory);
        Environment.SetEnvironmentVariable("USERPROFILE", workerHome);
        Environment.SetEnvironmentVariable("TMP", workerHome);
        Environment.SetEnvironmentVariable("TEMP", workerHome);
        Environment.SetEnvironmentVariable("TMPDIR", workerHome);
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
    }

    private static void ApplyWindowsWorkerRestrictions()
    {
        const int ProcessChildProcessPolicy = 13;
        uint noChildProcessCreation = 1;
        if (!SetProcessMitigationPolicy(ProcessChildProcessPolicy, ref noChildProcessCreation, sizeof(uint)))
            throw new InvalidOperationException(
                $"Could not block child process creation in the Windows plugin worker (Win32 error {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int mitigationPolicy, ref uint policy, nuint length);

    internal static async Task<JsonElement> DispatchAsync(
        IMediaPagerPlugin mediaPlugin,
        PluginWorkerMessage request,
        CancellationToken cancellationToken)
    {
        object? result = request.Operation switch
        {
            "stream.browse" when mediaPlugin is IStreamProviderPlugin stream => await DispatchBrowseAsync(stream, request, cancellationToken),
            "stream.details" when mediaPlugin is IStreamProviderPlugin stream => await DispatchDetailsAsync(stream,
                PluginWorkerJson.Deserialize<PluginWorkerDetailsArguments>(request.Arguments), cancellationToken),
            "stream.resolve" when mediaPlugin is IStreamProviderPlugin stream => await DispatchResolveAsync(stream,
                PluginWorkerJson.Deserialize<PluginWorkerResolveArguments>(request.Arguments), cancellationToken),
            // Worker proxies expose IPluginActions uniformly so the host can query dynamic
            // availability. A plugin with no action capability has an empty action list;
            // treat that optional query as such rather than failing the entire sources/plugins
            // payload. Invocations still require the actual capability below.
            "actions.list" => mediaPlugin is IPluginActions actionProvider
                ? await actionProvider.GetActionsAsync(cancellationToken)
                : Array.Empty<PluginActionDescriptor>(),
            "actions.invoke" when mediaPlugin is IPluginActions actions => await DispatchActionAsync(actions, request, cancellationToken),
            "agents.list" when mediaPlugin is IAgentPlugin agentProvider =>
                await agentProvider.GetAgentsAsync(cancellationToken),
            "agents.run" when mediaPlugin is IAgentPlugin agent => await DispatchAgentRunAsync(agent, request, cancellationToken),
            "search.run" when mediaPlugin is ISearchProviderPlugin search => await DispatchSearchAsync(search, request, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported worker operation '{request.Operation}'."),
        };
        return PluginWorkerJson.ToElement(result);
    }

    private static Task<Paged<BrowseItem>> DispatchBrowseAsync(
        IStreamProviderPlugin plugin,
        PluginWorkerMessage request,
        CancellationToken cancellationToken)
    {
        var arguments = PluginWorkerJson.Deserialize<PluginWorkerBrowseArguments>(request.Arguments);
        return plugin.BrowseAsync(arguments.SourceKey, arguments.Query, arguments.Page, cancellationToken);
    }

    private static Task<TitleDetails?> DispatchDetailsAsync(
        IStreamProviderPlugin plugin,
        PluginWorkerDetailsArguments arguments,
        CancellationToken cancellationToken) =>
        plugin.GetDetailsAsync(arguments.SourceKey, arguments.ExternalId, cancellationToken);

    private static Task<StreamResult?> DispatchResolveAsync(
        IStreamProviderPlugin plugin,
        PluginWorkerResolveArguments arguments,
        CancellationToken cancellationToken) =>
        plugin.ResolveAsync(arguments.Request, cancellationToken);

    private static async Task<bool> DispatchActionAsync(
        IPluginActions actions,
        PluginWorkerMessage request,
        CancellationToken cancellationToken)
    {
        await actions.InvokeActionAsync(
            PluginWorkerJson.Deserialize<PluginWorkerActionArguments>(request.Arguments).Context,
            cancellationToken);
        return true;
    }

    private static Task<AgentRunResult> DispatchAgentRunAsync(
        IAgentPlugin agents,
        PluginWorkerMessage request,
        CancellationToken cancellationToken)
    {
        var arguments = PluginWorkerJson.Deserialize<PluginWorkerAgentRunArguments>(request.Arguments);
        return agents.RunAsync(arguments.Request, cancellationToken);
    }

    private static Task<IReadOnlyList<ProviderSearchHit>> DispatchSearchAsync(
        ISearchProviderPlugin plugin,
        PluginWorkerMessage request,
        CancellationToken cancellationToken)
    {
        var arguments = PluginWorkerJson.Deserialize<PluginWorkerSearchArguments>(request.Arguments);
        return plugin.SearchAsync(arguments.Request, cancellationToken);
    }
}

internal sealed class PluginWorkerHostClient
{
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<PluginWorkerMessage>> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkerPluginJob> _jobs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _cancelledJobs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IProgress<MediaToolProgress>> _progress = new();
    private long _nextId;

    public PluginWorkerHostClient(TextReader input, TextWriter output)
    {
        this.input = input;
        this.output = output;
        Activity = new WorkerPluginActivity(this);
        MediaTools = new WorkerMediaToolBroker(this);
    }

    public IPluginActivity Activity { get; }
    public IMediaToolBroker MediaTools { get; }

    public void RegisterJob(WorkerPluginJob job)
    {
        _jobs[job.Id] = job;
        if (_cancelledJobs.TryRemove(job.Id, out _)) job.Cancel();
    }

    public void RemoveJob(string id) => _jobs.TryRemove(id, out _);

    public Task SendAsync(PluginWorkerMessage message) => WriteAsync(message);

    public async Task RunAsync(Func<PluginWorkerMessage, CancellationToken, Task<JsonElement>> dispatch)
    {
        var requests = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();
        while (await input.ReadLineAsync() is { } line)
        {
            PluginWorkerMessage? message;
            try { message = JsonSerializer.Deserialize<PluginWorkerMessage>(line, PluginWorkerJson.Options); }
            catch (JsonException exception)
            {
                Console.Error.WriteLine($"[plugin-worker] invalid host message: {exception.Message}");
                continue;
            }
            if (message is null) continue;
            if (message.ProtocolVersion != PluginWorkerProtocol.CurrentVersion)
            {
                Console.Error.WriteLine($"[plugin-worker] unsupported protocol version {message.ProtocolVersion}.");
                return;
            }
            if (message.Type == "host.response")
            {
                if (_pending.TryRemove(message.Id, out var waiter)) waiter.TrySetResult(message);
                continue;
            }
            if (message.Type == "host.event")
            {
                if (message.Operation == "activity.cancel")
                {
                    if (_jobs.TryRemove(message.Id, out var job)) job.Cancel();
                    else _cancelledJobs[message.Id] = 0;
                }
                else if (message.Operation == "media.progress" && message.Result is { } progressJson &&
                         _progress.TryGetValue(message.Id, out var progress))
                {
                    progress.Report(PluginWorkerJson.Deserialize<MediaToolProgress>(progressJson));
                }
                continue;
            }
            if (message.Type == "request")
            {
                var cancellation = new CancellationTokenSource();
                if (requests.TryAdd(message.Id, cancellation))
                    _ = Task.Run(async () =>
                    {
                        try { await HandleRequestAsync(message, dispatch, cancellation.Token); }
                        finally
                        {
                            requests.TryRemove(message.Id, out _);
                            cancellation.Dispose();
                        }
                    });
                else
                    cancellation.Dispose();
                continue;
            }
            if (message.Type == "cancel" && requests.TryGetValue(message.Id, out var requestCancellation))
                requestCancellation.Cancel();
        }

        foreach (var pending in _pending.Values)
            pending.TrySetException(new EndOfStreamException("The host closed the plugin worker pipe."));
    }

    private async Task HandleRequestAsync(
        PluginWorkerMessage request,
        Func<PluginWorkerMessage, CancellationToken, Task<JsonElement>> dispatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await dispatch(request, cancellationToken);
            await WriteAsync(new PluginWorkerMessage("response", request.Id, Result: result));
        }
        catch (PluginOperationException exception)
        {
            await WriteAsync(new PluginWorkerMessage("response", request.Id, Success: false,
                ErrorCode: exception.Error.Code, ErrorMessage: exception.Error.Message));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[plugin-worker] request {request.Operation} failed: {exception}");
            await WriteAsync(new PluginWorkerMessage("response", request.Id, Success: false,
                ErrorCode: "worker.invocation.failed", ErrorMessage: exception.Message));
        }
    }

    public async Task<T> CallHostAsync<T>(
        string capability,
        string operation,
        object arguments,
        CancellationToken cancellationToken = default,
        IProgress<MediaToolProgress>? progress = null)
    {
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var waiter = new TaskCompletionSource<PluginWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, waiter)) throw new InvalidOperationException("Duplicate worker RPC id.");
        if (progress is not null) _progress[id] = progress;
        try
        {
            await WriteAsync(new PluginWorkerMessage(
                "host.call", id, operation, capability, PluginWorkerJson.ToElement(arguments)));
            var response = await waiter.Task.WaitAsync(cancellationToken);
            if (!response.Success)
                throw new PluginOperationException(new PluginError(
                    response.ErrorCode ?? "worker.host_call.failed",
                    response.ErrorMessage ?? "The host capability call failed."));
            return PluginWorkerJson.Deserialize<T>(response.Result);
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(new PluginWorkerMessage("host.cancel", id));
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _progress.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(PluginWorkerMessage message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(message, PluginWorkerJson.Options));
            await output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

internal sealed class WorkerPluginActivity(PluginWorkerHostClient host) : IPluginActivity
{
    public void Notify(string title, string? message = null, PluginNotificationLevel level = PluginNotificationLevel.Info) =>
        host.CallHostAsync<bool>("activity", "notify", new PluginWorkerNotifyArguments(title, message, level))
            .GetAwaiter().GetResult();

    public void NotifyError(string title, PluginError error, PluginNotificationLevel level = PluginNotificationLevel.Error) =>
        host.CallHostAsync<bool>("activity", "notify-error", new PluginWorkerNotifyErrorArguments(title, error, level))
            .GetAwaiter().GetResult();

    public IPluginJob BeginJob(string title)
    {
        var handle = host.CallHostAsync<PluginWorkerJobHandle>(
            "activity", "begin-job", new PluginWorkerActivityBeginJobArguments(title)).GetAwaiter().GetResult();
        var job = new WorkerPluginJob(host, handle.Id);
        host.RegisterJob(job);
        return job;
    }

    public bool ClearJob(IPluginJob job)
    {
        var removed = host.CallHostAsync<bool>("activity", "clear-job", new PluginWorkerActivityClearArguments(job.Id))
            .GetAwaiter().GetResult();
        host.RemoveJob(job.Id);
        return removed;
    }

    public IReadOnlyList<PluginJobInfo> Jobs =>
        host.CallHostAsync<IReadOnlyList<PluginJobInfo>>("activity", "jobs", new { }).GetAwaiter().GetResult();

    public IReadOnlyList<PluginNotification> Notifications =>
        host.CallHostAsync<IReadOnlyList<PluginNotification>>("activity", "notifications", new { }).GetAwaiter().GetResult();
}

internal sealed class WorkerPluginJob(PluginWorkerHostClient host, string id) : IPluginJob
{
    private readonly CancellationTokenSource _cancellation = new();
    public string Id { get; } = id;
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;
    public CancellationToken CancellationToken => _cancellation.Token;

    public void Report(double progress, string? status = null) =>
        host.CallHostAsync<bool>("activity", "report", new PluginWorkerJobArguments(Id, status, progress))
            .GetAwaiter().GetResult();

    public void Complete(string? status = null)
    {
        _ = host.CallHostAsync<bool>("activity", "complete", new PluginWorkerJobArguments(Id, status))
            .GetAwaiter().GetResult();
        host.RemoveJob(Id);
    }

    public void Fail(string status)
    {
        _ = host.CallHostAsync<bool>("activity", "fail", new PluginWorkerJobArguments(Id, status))
            .GetAwaiter().GetResult();
        host.RemoveJob(Id);
    }

    public void FailWithError(PluginError error)
    {
        _ = host.CallHostAsync<bool>("activity", "fail-error", new PluginWorkerJobArguments(Id, Error: error))
            .GetAwaiter().GetResult();
        host.RemoveJob(Id);
    }

    public void Cancel()
    {
        try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
}

internal sealed class WorkerPluginEmailSender(PluginWorkerHostClient host) : IPluginEmailSender
{
    public Task<bool> SendAsync(PluginEmailMessage message, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<bool>("email.sending", "send", new PluginWorkerEmailArguments(message), cancellationToken);
}

internal sealed class WorkerPluginPublicHttpBroker(PluginWorkerHostClient host) : IPluginPublicHttpBroker
{
    public Task<PluginPublicHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<PluginPublicHttpResponse>("public.http", "get",
            new PluginWorkerPublicHttpFetchArguments(uri), cancellationToken);
}

/// <summary>Worker-side proxy for the host's catalog-scanning service. The host performs every
/// containment check and database write; this class only forwards typed RPC calls.</summary>
internal sealed class WorkerCatalogScanStore(PluginWorkerHostClient host) : ICatalogScanStore
{
    public Task<IReadOnlyList<CatalogScanCatalogInfo>> ListCatalogsAsync(CancellationToken cancellationToken = default) =>
        host.CallHostAsync<IReadOnlyList<CatalogScanCatalogInfo>>("catalog.scanning", "list-catalogs", new { }, cancellationToken);

    public Task<CatalogScanCatalogInfo?> GetCatalogAsync(int catalogId, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<CatalogScanCatalogInfo?>("catalog.scanning", "get-catalog",
            new PluginWorkerCatalogScanCatalogArguments(catalogId), cancellationToken);

    public Task<IReadOnlyList<CatalogScanItemInfo>> ListItemsAsync(int catalogId, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<IReadOnlyList<CatalogScanItemInfo>>("catalog.scanning", "list-items",
            new PluginWorkerCatalogScanCatalogArguments(catalogId), cancellationToken);

    public Task<CatalogScanItemInfo?> FindByPathAsync(string path, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<CatalogScanItemInfo?>("catalog.scanning", "find-by-path",
            new PluginWorkerCatalogScanPathArguments(path), cancellationToken);

    public async Task<int> CreateAsync(CatalogScanCreateRequest request, CancellationToken cancellationToken = default) =>
        await host.CallHostAsync<int>("catalog.scanning", "create",
            new PluginWorkerCatalogScanCreateArguments(request), cancellationToken);

    public async Task UpdateAsync(int itemId, CatalogScanIdentityUpdate update, CancellationToken cancellationToken = default) =>
        _ = await host.CallHostAsync<bool>("catalog.scanning", "update",
            new PluginWorkerCatalogScanUpdateArguments(itemId, update), cancellationToken);

    public async Task DeleteAsync(int itemId, CancellationToken cancellationToken = default) =>
        _ = await host.CallHostAsync<bool>("catalog.scanning", "delete",
            new PluginWorkerCatalogScanItemArguments(itemId), cancellationToken);
}

internal sealed class WorkerPluginDataStore(PluginWorkerHostClient host, string pluginId) : IPluginDataStore
{
    private readonly string _pluginKey = pluginId.Split('.')[^1];

    public Task<PluginDataPage> ListAsync(
        string pluginKey,
        string collection,
        int offset = 0,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<PluginDataPage>("plugin.data", "list",
            new PluginWorkerDataListArguments(collection, offset, limit), cancellationToken);
    }

    public Task<PluginDataDocument?> GetAsync(
        string pluginKey,
        string collection,
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<PluginDataDocument?>("plugin.data", "get",
            new PluginWorkerDataGetArguments(collection, key), cancellationToken);
    }

    public Task ReplaceCollectionAsync(
        string pluginKey,
        string collection,
        IReadOnlyList<PluginDataWrite> documents,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<bool>("plugin.data", "replace",
            new PluginWorkerDataReplaceArguments(collection, documents), cancellationToken);
    }

    public Task UpsertAsync(
        string pluginKey,
        string collection,
        string key,
        string json,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<bool>("plugin.data", "upsert",
            new PluginWorkerDataUpsertArguments(collection, key, json), cancellationToken);
    }

    public Task DeleteAsync(
        string pluginKey,
        string collection,
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<bool>("plugin.data", "delete",
            new PluginWorkerDataDeleteArguments(collection, key), cancellationToken);
    }

    private void EnsureOwnNamespace(string pluginKey)
    {
        if (!string.Equals(pluginKey, _pluginKey, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Plugin '{pluginId}' may only access its own data namespace.");
    }
}

internal sealed class WorkerMediaToolBroker(PluginWorkerHostClient host) : IMediaToolBroker
{
    public Task<bool> IsDestinationAvailableAsync(int catalogId, string catalogTypeSlug, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<bool>("media.tools", "destination-available",
            new { catalogId, catalogTypeSlug }, cancellationToken);

    public Task<bool> HasOtherStreamItemActionAsync(
        string pluginId,
        MediaKind kind,
        CancellationToken cancellationToken = default) =>
        host.CallHostAsync<bool>("media.tools", "has-other-stream-action", new { pluginId, kind }, cancellationToken);

    public Task<bool> HasOnlineStreamProviderAsync(MediaKind kind, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<bool>("media.tools", "has-online-stream-provider", new { kind }, cancellationToken);

    public Task<ResolvedStreamSource?> ResolveStreamAsync(PluginActionContext context, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<ResolvedStreamSource?>("media.tools", "resolve-stream",
            new PluginWorkerMediaStreamArguments(context), cancellationToken);

    public async Task TransferAsync(
        MediaTransferRequest request,
        IProgress<MediaToolProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _ = await host.CallHostAsync<bool>("media.tools", "transfer",
            new PluginWorkerTransferArguments(request), cancellationToken, progress);
}

internal sealed class PluginWorkerServiceProvider(
    PluginWorkerHostClient host,
    string pluginId,
    IReadOnlySet<string> hostCapabilities)
    : IServiceProvider, IServiceProviderIsService
{
    private readonly IPluginSettingsStore _settings = new WorkerSettingsStore(host, pluginId);
    private readonly IBrowserAutomationBroker _browser = new WorkerBrowserBroker(host);
    private readonly IPluginActivity _activity = host.Activity;
    private readonly IMediaToolBroker _mediaTools = host.MediaTools;
    private readonly IPluginEmailSender _emailSender = new WorkerPluginEmailSender(host);
    private readonly ICatalogScanStore _catalogScanStore = new WorkerCatalogScanStore(host);
    private readonly IPluginDataStore _pluginDataStore = new WorkerPluginDataStore(host, pluginId);
    private readonly IPluginPublicHttpBroker _publicHttpBroker = new WorkerPluginPublicHttpBroker(host);

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IPluginSettingsStore)) return _settings;
        if (serviceType == typeof(IBrowserAutomationBroker))
            return hostCapabilities.Contains(PluginHostCapabilityNames.BrowserAutomation) ? _browser : null;
        if (serviceType == typeof(IPluginActivity)) return _activity;
        if (serviceType == typeof(IMediaToolBroker))
            return hostCapabilities.Contains(PluginHostCapabilityNames.MediaTools) ? _mediaTools : null;
        if (serviceType == typeof(IPluginEmailSender))
            return hostCapabilities.Contains(PluginHostCapabilityNames.EmailSending) ? _emailSender : null;
        if (serviceType == typeof(ICatalogScanStore))
            return hostCapabilities.Contains(PluginHostCapabilityNames.CatalogScanning) ? _catalogScanStore : null;
        if (serviceType == typeof(IPluginDataStore))
            return hostCapabilities.Contains(PluginHostCapabilityNames.PluginData) ? _pluginDataStore : null;
        if (serviceType == typeof(IPluginPublicHttpBroker))
            return hostCapabilities.Contains(PluginHostCapabilityNames.PublicHttpFetch) ? _publicHttpBroker : null;
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceProviderIsService)) return this;
        return null;
    }

    public bool IsService(Type serviceType) =>
        serviceType == typeof(IPluginSettingsStore) ||
        serviceType == typeof(IBrowserAutomationBroker) && hostCapabilities.Contains(PluginHostCapabilityNames.BrowserAutomation) ||
        serviceType == typeof(IPluginActivity) ||
        serviceType == typeof(IMediaToolBroker) && hostCapabilities.Contains(PluginHostCapabilityNames.MediaTools) ||
        serviceType == typeof(IPluginEmailSender) && hostCapabilities.Contains(PluginHostCapabilityNames.EmailSending) ||
        serviceType == typeof(ICatalogScanStore) && hostCapabilities.Contains(PluginHostCapabilityNames.CatalogScanning) ||
        serviceType == typeof(IPluginDataStore) && hostCapabilities.Contains(PluginHostCapabilityNames.PluginData) ||
        serviceType == typeof(IPluginPublicHttpBroker) && hostCapabilities.Contains(PluginHostCapabilityNames.PublicHttpFetch) ||
        serviceType == typeof(IServiceProvider) ||
        serviceType == typeof(IServiceProviderIsService);
}

internal sealed class WorkerSettingsStore(PluginWorkerHostClient host, string pluginId) : IPluginSettingsStore
{
    public Task<string?> GetAsync(string pluginKey, string name, CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<string?>("settings", "get",
            new PluginWorkerSettingsArguments(pluginKey, name), cancellationToken);
    }

    public async Task SetAsync(string pluginKey, string name, string? value, CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        _ = await host.CallHostAsync<bool>("settings", "set",
            new PluginWorkerSettingsArguments(pluginKey, name, value), cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(string pluginKey, CancellationToken cancellationToken = default)
    {
        EnsureOwnNamespace(pluginKey);
        return host.CallHostAsync<IReadOnlyDictionary<string, string>>("settings", "get-all",
            new PluginWorkerSettingsArguments(pluginKey, ""), cancellationToken);
    }

    private void EnsureOwnNamespace(string pluginKey)
    {
        if (!string.Equals(pluginKey, pluginId.Split('.')[^1], StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The plugin worker may only access its own settings namespace.");
    }
}

internal sealed class WorkerBrowserBroker(PluginWorkerHostClient host) : IBrowserAutomationBroker
{
    public Task<string> GetPublicTextAsync(Uri uri, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<string>("browser.automation", "get-public-text", new { uri = uri.AbsoluteUri }, cancellationToken);

    public Task<string> GetPublicTextAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken = default) =>
        host.CallHostAsync<string>("browser.automation", "get-public-text",
            new { uri = uri.AbsoluteUri, headers = requestHeaders }, cancellationToken);

    public Task<string?> NavigateAndWaitForRequestAsync(
        BrowserAutomationRequest request,
        CancellationToken cancellationToken = default) =>
        host.CallHostAsync<string?>("browser.automation", "navigate-and-match", request, cancellationToken);
}
