using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MediaPager.App.Core.Plugins;
using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Services.Workers;

internal sealed class PluginWorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly string _pluginId;
    private readonly string _settingsKey;
    private readonly string _workerHome;
    private readonly IPluginSettingsRepository _settings;
    private readonly IPluginActivity _activity;
    private readonly IBrowserAutomationBroker _browser;
    private readonly IMediaToolBroker _mediaTools;
    private readonly IPluginEmailSender? _emailSender;
    private readonly IReadOnlySet<string> _hostCapabilities;
    private readonly Action<string> _cancelHostJob;
    private readonly ILogger _logger;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly StreamReader _stderr;
    private readonly string? _windowsAppContainerProfileName;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PluginWorkerMessage>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _hostCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IPluginJob> _activityJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenRegistration> _activityJobCancellations = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<PluginWorkerDescription> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private long _nextId;
    private int _disposed;

    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public PluginWorkerProcess(
        string pluginDirectory,
        string pluginId,
        IPluginSettingsRepository settings,
        IPluginActivity activity,
        IBrowserAutomationBroker browser,
        IMediaToolBroker mediaTools,
        IPluginEmailSender? emailSender,
        IReadOnlySet<string> hostCapabilities,
        Action<string> cancelHostJob,
        ILogger logger)
    {
        _pluginId = pluginId;
        _settingsKey = pluginId.Split('.')[^1];
        _settings = settings;
        _activity = activity;
        _browser = browser;
        _mediaTools = mediaTools;
        _emailSender = emailSender;
        _hostCapabilities = new HashSet<string>(hostCapabilities, StringComparer.OrdinalIgnoreCase);
        _cancelHostJob = cancelHostJob;
        _logger = logger;
        _workerHome = Path.Combine(Path.GetTempPath(), "mediapager-workers",
            pluginId.Replace('.', '_') + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workerHome);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_workerHome,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var pluginAssembly = Path.Combine(pluginDirectory, $"{Path.GetFileName(pluginDirectory)}.dll");
        if (!File.Exists(pluginAssembly))
            throw new FileNotFoundException("The worker plugin assembly is missing.", pluginAssembly);

        if (OperatingSystem.IsWindows())
        {
            var launch = WindowsAppContainerWorker.Start(
                typeof(PluginWorkerCommand).Assembly.Location,
                pluginAssembly,
                pluginDirectory,
                _workerHome,
                pluginId,
                _hostCapabilities);
            _process = launch.Process;
            _stdin = launch.StandardInput;
            _stdout = launch.StandardOutput;
            _stderr = launch.StandardError;
            _windowsAppContainerProfileName = launch.ProfileName;
            _logger.LogInformation("Started Windows AppContainer worker process {ProcessId} for {PluginId}.",
                _process.Id, pluginId);
        }
        else
        {
            var startInfo = CreateStartInfo(pluginAssembly, pluginId, _workerHome, _hostCapabilities);
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start worker process for '{pluginId}'.");
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
            _stderr = _process.StandardError;
            _logger.LogInformation("Started worker process {ProcessId} for {PluginId}: {Executable} {Arguments}.",
                _process.Id, pluginId, startInfo.FileName, string.Join(' ', startInfo.ArgumentList));
        }
        _stdoutTask = Task.Run(ReadStdoutAsync);
        _stderrTask = Task.Run(ReadStderrAsync);
    }

    public PluginWorkerDescription StartAndDescribe() =>
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    public async Task<T?> InvokeAsync<T>(string operation, object arguments, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var waiter = new TaskCompletionSource<PluginWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, waiter)) throw new InvalidOperationException("Duplicate plugin worker request id.");

        try
        {
            await WriteMessageAsync(new PluginWorkerMessage(
                "request", id, operation, Arguments: PluginWorkerJson.ToElement(arguments)), cancellationToken);
            PluginWorkerMessage response;
            try
            {
                response = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(75), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await WriteMessageAsync(new PluginWorkerMessage("cancel", id), CancellationToken.None);
                throw;
            }
            catch (TimeoutException)
            {
                await WriteMessageAsync(new PluginWorkerMessage("cancel", id), CancellationToken.None);
                throw new TimeoutException($"Plugin worker '{_pluginId}' did not complete '{operation}' in time.");
            }

            if (!response.Success)
            {
                var error = new PluginError(
                    response.ErrorCode ?? "plugin.worker.failed",
                    response.ErrorMessage ?? "The plugin worker operation failed.");
                throw new PluginOperationException(error);
            }
            return PluginWorkerJson.Deserialize<T>(response.Result);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (await _stdout.ReadLineAsync() is { } line)
            {
                PluginWorkerMessage? message;
                try { message = JsonSerializer.Deserialize<PluginWorkerMessage>(line, PluginWorkerJson.Options); }
                catch (JsonException exception)
                {
                    var prefix = line[..Math.Min(line.Length, 160)];
                    _logger.LogError(exception, "Worker {PluginId} emitted invalid protocol output: {Prefix}", _pluginId, prefix);
                    _ready.TrySetException(new InvalidDataException($"Plugin worker '{_pluginId}' emitted non-protocol output."));
                    try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return;
                }
                if (message is null) continue;
                if (message.ProtocolVersion != PluginWorkerProtocol.CurrentVersion)
                {
                    _ready.TrySetException(new InvalidDataException(
                        $"Worker '{_pluginId}' uses unsupported protocol version {message.ProtocolVersion}."));
                    try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return;
                }
                switch (message.Type)
                {
                    case "ready":
                        if (!message.Success || message.Result is null)
                            _ready.TrySetException(new InvalidOperationException(
                                message.ErrorMessage ?? $"Worker '{_pluginId}' failed to initialize."));
                        else
                            _ready.TrySetResult(PluginWorkerJson.Deserialize<PluginWorkerDescription>(message.Result));
                        break;
                    case "response":
                        if (_pending.TryGetValue(message.Id, out var waiter)) waiter.TrySetResult(message);
                        break;
                    case "host.call":
                        _ = HandleHostCallAsync(message);
                        break;
                    case "host.cancel":
                        if (_hostCalls.TryGetValue(message.Id, out var cancellation)) cancellation.Cancel();
                        break;
                    default:
                        _logger.LogWarning("Worker {PluginId} sent unsupported protocol message {Type}.", _pluginId, message.Type);
                        break;
                }
            }
            StopWorkerJobs();
            _ready.TrySetException(new EndOfStreamException($"Plugin worker '{_pluginId}' exited before initialization."));
            foreach (var waiter in _pending.Values)
                waiter.TrySetException(new EndOfStreamException($"Plugin worker '{_pluginId}' closed its protocol pipe."));
        }
        catch (Exception exception)
        {
            StopWorkerJobs();
            _ready.TrySetException(exception);
            foreach (var waiter in _pending.Values) waiter.TrySetException(exception);
        }
    }

    private async Task HandleHostCallAsync(PluginWorkerMessage message)
    {
        using var cancellation = new CancellationTokenSource();
        if (!_hostCalls.TryAdd(message.Id, cancellation)) return;
        try
        {
            var result = await DispatchHostCallAsync(message, cancellation.Token);
            await WriteMessageAsync(new PluginWorkerMessage("host.response", message.Id, Result: result), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await WriteMessageAsync(new PluginWorkerMessage("host.response", message.Id, Success: false,
                ErrorCode: "worker.host_call.cancelled", ErrorMessage: "The host capability call was cancelled."), CancellationToken.None);
        }
        catch (PluginOperationException exception)
        {
            await WriteMessageAsync(new PluginWorkerMessage("host.response", message.Id, Success: false,
                ErrorCode: exception.Error.Code, ErrorMessage: exception.Error.Message), CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Worker {PluginId} host call {Capability}.{Operation} failed.",
                _pluginId, message.Capability, message.Operation);
            await WriteMessageAsync(new PluginWorkerMessage("host.response", message.Id, Success: false,
                ErrorCode: "worker.host_call.failed", ErrorMessage: "The requested host capability call failed."), CancellationToken.None);
        }
        finally
        {
            _hostCalls.TryRemove(message.Id, out _);
        }
    }

    private async Task<JsonElement> DispatchHostCallAsync(PluginWorkerMessage message, CancellationToken cancellationToken)
    {
        if (message.Capability == "settings")
        {
            var request = PluginWorkerJson.Deserialize<PluginWorkerSettingsArguments>(message.Arguments);
            if (!string.Equals(request.PluginKey, _settingsKey, StringComparison.OrdinalIgnoreCase))
                throw new PluginOperationException(new PluginError("plugin.settings.denied", "The plugin may only access its own settings."));
            return message.Operation switch
            {
                "get" => PluginWorkerJson.ToElement(await _settings.GetAsync(_pluginId, request.Name, cancellationToken)),
                "set" => await SetSettingAsync(request, cancellationToken),
                "get-all" => PluginWorkerJson.ToElement(await _settings.GetAllAsync(_pluginId, cancellationToken)),
                _ => throw new PluginOperationException(new PluginError("plugin.settings.operation", "Unsupported settings operation.")),
            };
        }

        if (message.Capability == "browser.automation" &&
            _hostCapabilities.Contains(PluginHostCapabilityNames.BrowserAutomation))
        {
            return message.Operation switch
            {
                "get-public-text" => PluginWorkerJson.ToElement(await _browser.GetPublicTextAsync(
                    new Uri(PluginWorkerJson.Deserialize<WorkerPublicUri>(message.Arguments).Uri), cancellationToken)),
                "navigate-and-match" => PluginWorkerJson.ToElement(await _browser.NavigateAndWaitForRequestAsync(
                    PluginWorkerJson.Deserialize<BrowserAutomationRequest>(message.Arguments), cancellationToken)),
                _ => throw new PluginOperationException(new PluginError("browser.operation.denied", "Unsupported browser operation.")),
            };
        }

        if (message.Capability == "activity")
            return await DispatchActivityCallAsync(message, cancellationToken);

        if (message.Capability == "media.tools" &&
            _hostCapabilities.Contains(PluginHostCapabilityNames.MediaTools))
            return await DispatchMediaToolCallAsync(message, cancellationToken);

        if (message.Capability == "email.sending" &&
            _hostCapabilities.Contains(PluginHostCapabilityNames.EmailSending) && _emailSender is not null)
        {
            if (message.Operation != "send")
                throw new PluginOperationException(new PluginError("plugin.email.operation", "Unsupported email operation."));
            var request = PluginWorkerJson.Deserialize<PluginWorkerEmailArguments>(message.Arguments);
            return PluginWorkerJson.ToElement(await _emailSender.SendAsync(request.Message, cancellationToken));
        }

        throw new PluginOperationException(new PluginError("plugin.capability.denied", "The host did not grant this capability."));
    }

    private async Task<JsonElement> DispatchActivityCallAsync(PluginWorkerMessage message, CancellationToken cancellationToken)
    {
        switch (message.Operation)
        {
            case "notify":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerNotifyArguments>(message.Arguments);
                _activity.Notify(request.Title, request.Message, request.Level);
                return PluginWorkerJson.ToElement(true);
            }
            case "notify-error":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerNotifyErrorArguments>(message.Arguments);
                _activity.NotifyError(request.Title, request.Error, request.Level);
                return PluginWorkerJson.ToElement(true);
            }
            case "begin-job":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerActivityBeginJobArguments>(message.Arguments);
                var job = _activity.BeginJob(request.Title);
                if (!_activityJobs.TryAdd(job.Id, job))
                    throw new InvalidOperationException("The host activity store returned a duplicate job id.");
                CancellationTokenRegistration registration = default;
                registration = job.CancellationToken.Register(() =>
                {
                    _ = WriteMessageAsync(new PluginWorkerMessage(
                        "host.event", job.Id, Operation: "activity.cancel"), CancellationToken.None);
                    _activityJobs.TryRemove(job.Id, out _);
                    _activityJobCancellations.TryRemove(job.Id, out _);
                    registration.Unregister();
                });
                _activityJobCancellations[job.Id] = registration;
                return PluginWorkerJson.ToElement(new PluginWorkerJobHandle(job.Id));
            }
            case "report":
            case "complete":
            case "fail":
            case "fail-error":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerJobArguments>(message.Arguments);
                if (!_activityJobs.TryGetValue(request.JobId, out var job))
                    throw new PluginOperationException(new PluginError("activity.job.not_found", "The plugin job is no longer active."));
                switch (message.Operation)
                {
                    case "report": job.Report(request.Progress ?? 0, request.Status); break;
                    case "complete":
                        job.Complete(request.Status);
                        RemoveActivityJob(request.JobId);
                        break;
                    case "fail":
                        job.Fail(request.Status ?? "Plugin job failed.");
                        RemoveActivityJob(request.JobId);
                        break;
                    case "fail-error":
                        job.FailWithError(request.Error ?? new PluginError("plugin.failed", request.Status ?? "Plugin job failed."));
                        RemoveActivityJob(request.JobId);
                        break;
                }
                return PluginWorkerJson.ToElement(true);
            }
            case "clear-job":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerActivityClearArguments>(message.Arguments);
                _activityJobs.TryGetValue(request.JobId, out var job);
                var removed = _activity.ClearJob(job ?? new PluginActivityJobReference(request.JobId));
                RemoveActivityJob(request.JobId);
                return PluginWorkerJson.ToElement(removed);
            }
            case "jobs":
                return PluginWorkerJson.ToElement(_activity.Jobs);
            case "notifications":
                return PluginWorkerJson.ToElement(_activity.Notifications);
            default:
                throw new PluginOperationException(new PluginError("activity.operation.denied", "Unsupported activity operation."));
        }
    }

    private async Task<JsonElement> DispatchMediaToolCallAsync(PluginWorkerMessage message, CancellationToken cancellationToken)
    {
        switch (message.Operation)
        {
            case "destination-available":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerDestinationArguments>(message.Arguments);
                return PluginWorkerJson.ToElement(await _mediaTools.IsDestinationAvailableAsync(
                    request.CatalogId, request.CatalogTypeSlug, cancellationToken));
            }
            case "has-other-stream-action":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerActionPresenceArguments>(message.Arguments);
                return PluginWorkerJson.ToElement(await _mediaTools.HasOtherStreamItemActionAsync(
                    request.PluginId, request.Kind, cancellationToken));
            }
            case "has-online-stream-provider":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerMediaKindArguments>(message.Arguments);
                return PluginWorkerJson.ToElement(await _mediaTools.HasOnlineStreamProviderAsync(
                    request.Kind, cancellationToken));
            }
            case "resolve-stream":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerMediaStreamArguments>(message.Arguments);
                return PluginWorkerJson.ToElement(await _mediaTools.ResolveStreamAsync(request.Context, cancellationToken));
            }
            case "transfer":
            {
                var request = PluginWorkerJson.Deserialize<PluginWorkerTransferArguments>(message.Arguments);
                var progress = new Progress<MediaToolProgress>(update =>
                {
                    _ = WriteMessageAsync(new PluginWorkerMessage(
                        "host.event", message.Id, Operation: "media.progress",
                        Result: PluginWorkerJson.ToElement(update)), CancellationToken.None);
                });
                if (request.Request.JobId is { } jobId && _activityJobs.TryGetValue(jobId, out var job))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.CancellationToken);
                    await _mediaTools.TransferAsync(request.Request, progress, linked.Token);
                }
                else
                {
                    await _mediaTools.TransferAsync(request.Request, progress, cancellationToken);
                }
                return PluginWorkerJson.ToElement(true);
            }
            default:
                throw new PluginOperationException(new PluginError("media.tool.operation.denied", "Unsupported media-tool operation."));
        }
    }

    private void RemoveActivityJob(string jobId)
    {
        _activityJobs.TryRemove(jobId, out _);
        if (_activityJobCancellations.TryRemove(jobId, out var cancellation)) cancellation.Dispose();
    }

    private sealed class PluginActivityJobReference(string id) : IPluginJob
    {
        public string Id { get; } = id;
        public bool IsCancellationRequested => false;
        public CancellationToken CancellationToken => CancellationToken.None;
        public void Report(double progress, string? status = null) { }
        public void Complete(string? status = null) { }
        public void Fail(string status) { }
    }

    private void StopWorkerJobs()
    {
        foreach (var jobId in _activityJobs.Keys.ToArray())
        {
            try { _cancelHostJob(jobId); }
            catch (Exception exception) { _logger.LogDebug(exception, "Could not cancel worker job {JobId}.", jobId); }
            RemoveActivityJob(jobId);
        }
    }

    private async Task<JsonElement> SetSettingAsync(PluginWorkerSettingsArguments request, CancellationToken cancellationToken)
    {
        await _settings.SetAsync(_pluginId, request.Name, request.Value, cancellationToken);
        return PluginWorkerJson.ToElement(true);
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _stderr.ReadLineAsync() is { } line)
                _logger.LogInformation("[plugin-worker:{PluginId}] {Line}", _pluginId, line);
        }
        catch (ObjectDisposedException) { }
    }

    private async Task WriteMessageAsync(PluginWorkerMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stdin.WriteLineAsync(JsonSerializer.Serialize(message, PluginWorkerJson.Options));
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string pluginAssembly,
        string pluginId,
        string workerHome,
        IReadOnlySet<string> hostCapabilities)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The API process path is unavailable for starting plugin workers.");
        var apiAssembly = typeof(PluginWorkerCommand).Assembly.Location;
        var processName = Path.GetFileNameWithoutExtension(processPath);
        var runningApiAppHost = string.Equals(processName, "MediaPager.App.Api", StringComparison.OrdinalIgnoreCase);
        var dotnetHost = Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "../../../",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var workerExecutable = runningApiAppHost || string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : File.Exists(dotnetHost) ? dotnetHost : "dotnet";
        var useMacNetworkSandbox = OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec");
        var startInfo = new ProcessStartInfo
        {
            FileName = useMacNetworkSandbox ? "/usr/bin/sandbox-exec" : workerExecutable,
            WorkingDirectory = workerHome,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (useMacNetworkSandbox)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("no-network");
            startInfo.ArgumentList.Add(workerExecutable);
        }
        if (!runningApiAppHost)
            startInfo.ArgumentList.Add(apiAssembly);
        startInfo.ArgumentList.Add("--plugin-worker");
        startInfo.ArgumentList.Add(pluginAssembly);
        startInfo.ArgumentList.Add(pluginId);
        startInfo.ArgumentList.Add(workerHome);
        startInfo.ArgumentList.Add(string.Join(',', hostCapabilities.Order(StringComparer.OrdinalIgnoreCase)));

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = string.Empty;
        if (!string.IsNullOrWhiteSpace(systemRoot)) startInfo.Environment["SystemRoot"] = systemRoot;
        if (!string.IsNullOrWhiteSpace(windir)) startInfo.Environment["WINDIR"] = windir;
        startInfo.Environment[OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"] = workerHome;
        startInfo.Environment["TMP"] = workerHome;
        startInfo.Environment["TEMP"] = workerHome;
        startInfo.Environment["TMPDIR"] = workerHome;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        return startInfo;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginWorkerProcess));
        if (_process.HasExited) throw new EndOfStreamException($"Plugin worker '{_pluginId}' is not running.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopWorkerJobs();
        foreach (var cancellation in _hostCalls.Values) cancellation.Cancel();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _logger.LogDebug(exception, "Could not terminate plugin worker {PluginId}.", _pluginId);
        }
        finally
        {
            _stdin.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
            if (_windowsAppContainerProfileName is not null)
                WindowsAppContainerWorker.DeleteProfile(_windowsAppContainerProfileName);
            _process.Dispose();
            if (!WindowsAppContainerWorker.TryDeleteWorkerHome(_workerHome))
                _logger.LogWarning("Could not remove temporary plugin worker directory {WorkerHome}.", _workerHome);
        }
    }

    private sealed record WorkerPublicUri(string Uri);
}
