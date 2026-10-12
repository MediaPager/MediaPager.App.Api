using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MediaPager.App.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace MediaPager.App.Api.Services.Workers;

internal static class PluginWorkerCommand
{
    public static bool IsWorkerMode(string[] args) => args.Length > 0 && args[0] == "--plugin-worker";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            Console.Error.WriteLine("Plugin worker expects: --plugin-worker <assembly-path> <plugin-id> [worker-home].");
            return 2;
        }

        var assemblyPath = Path.GetFullPath(args[1]);
        var pluginId = args[2];
        var workerHome = args.Length == 4
            ? Path.GetFullPath(args[3])
            : Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
              ?? Path.GetTempPath();
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
            var services = new PluginWorkerServiceProvider(host, pluginId);
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
            var navigation = plugins[0] as IPluginNavigation;
            if (stream is null && actionProvider is null)
                throw new InvalidOperationException($"Worker '{pluginId}' does not expose a supported capability.");

            // Start the duplex pipe reader before querying plugin actions: some plugins
            // ask host brokers while computing availability during GetActionsAsync.
            var workerLoop = Task.Run(() => host.RunAsync((request, cancellationToken) =>
                DispatchAsync(workerPlugin, request, cancellationToken)));
            var actions = actionProvider is null
                ? []
                : (await actionProvider.GetActionsAsync(CancellationToken.None)).ToArray();
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
                navigation?.MainNav.ToArray() ?? []);
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

    private static async Task<JsonElement> DispatchAsync(
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
            "actions.invoke" when mediaPlugin is IPluginActions actions => await DispatchActionAsync(actions, request, cancellationToken),
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

internal sealed class WorkerMediaToolBroker(PluginWorkerHostClient host) : IMediaToolBroker
{
    public Task<bool> IsDestinationAvailableAsync(int catalogId, string catalogTypeSlug, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<bool>("media.tools", "destination-available",
            new { catalogId, catalogTypeSlug }, cancellationToken);

    public Task<bool> HasOtherStreamItemActionAsync(MediaKind kind, CancellationToken cancellationToken = default) =>
        host.CallHostAsync<bool>("media.tools", "has-other-stream-action", new { kind }, cancellationToken);

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

internal sealed class PluginWorkerServiceProvider(PluginWorkerHostClient host, string pluginId)
    : IServiceProvider, IServiceProviderIsService
{
    private readonly IPluginSettingsStore _settings = new WorkerSettingsStore(host, pluginId);
    private readonly IBrowserAutomationBroker _browser = new WorkerBrowserBroker(host);
    private readonly IPluginActivity _activity = host.Activity;
    private readonly IMediaToolBroker _mediaTools = host.MediaTools;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IPluginSettingsStore)) return _settings;
        if (serviceType == typeof(IBrowserAutomationBroker)) return _browser;
        if (serviceType == typeof(IPluginActivity)) return _activity;
        if (serviceType == typeof(IMediaToolBroker)) return _mediaTools;
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceProviderIsService)) return this;
        return null;
    }

    public bool IsService(Type serviceType) =>
        serviceType == typeof(IPluginSettingsStore) ||
        serviceType == typeof(IBrowserAutomationBroker) ||
        serviceType == typeof(IPluginActivity) ||
        serviceType == typeof(IMediaToolBroker) ||
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

    public Task<string?> NavigateAndWaitForRequestAsync(
        BrowserAutomationRequest request,
        CancellationToken cancellationToken = default) =>
        host.CallHostAsync<string?>("browser.automation", "navigate-and-match", request, cancellationToken);
}
