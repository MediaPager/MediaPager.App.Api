using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace MediaPager.App.Api.Services.Agents;

/// <summary>Polls every loaded <see cref="IAgentPlugin"/> and starts due agents in the
/// background. Next-run state is keyed by plugin id + agent id, and one agent never overlaps
/// itself. Each run is exposed as a cancellable, observable plugin job.</summary>
public sealed class AgentRuntimeService(
    PluginRegistry registry,
    PluginActivityStore activityStore,
    IConfiguration configuration,
    ILogger<AgentRuntimeService> logger) : BackgroundService
{
    private const double DefaultPollIntervalSeconds = 30;

    private readonly TimeSpan _pollInterval = ResolvePollInterval(configuration);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly ConcurrentDictionary<AgentKey, DateTimeOffset> _nextRuns = new();
    private readonly ConcurrentDictionary<AgentKey, byte> _active = new();

    /// <summary>One polling pass. Public so a host/test can drive the scheduler without waiting
    /// for the hosted-service timer.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var plugin in registry.ResolveAll<IAgentPlugin>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<AgentDescriptor> descriptors;
                try
                {
                    descriptors = await plugin.GetAgentsAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not list agents for plugin {PluginId}.", plugin.Descriptor.Id);
                    continue;
                }

                foreach (var descriptor in descriptors.Where(entry => entry.Enabled))
                {
                    if (string.IsNullOrWhiteSpace(descriptor.Id)) continue;
                    var key = new AgentKey(plugin.Descriptor.Id, descriptor.Id);
                    if (_active.ContainsKey(key)) continue;
                    if (_nextRuns.TryGetValue(key, out var next) && next > now) continue;

                    _nextRuns[key] = now.Add(IntervalOf(descriptor));
                    if (!_active.TryAdd(key, 0)) continue;
                    try
                    {
                        // Run on a background task but finish it before the next agent so a
                        // cycle is deterministic and no two runs of one agent overlap.
                        await Task.Run(
                            () => RunScheduledAsync(plugin, descriptor, cancellationToken),
                            CancellationToken.None);
                    }
                    finally
                    {
                        _active.TryRemove(key, out _);
                    }
                }
            }
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>Run one agent immediately with <see cref="AgentRunRequest.Force"/> and push its
    /// next scheduled run out by its own interval. Used by POST /plugins/{key}/agents/{id}/run.</summary>
    public async Task<AgentRunResult> RunManuallyAsync(
        IAgentPlugin plugin,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var descriptors = await plugin.GetAgentsAsync(cancellationToken);
        var descriptor = descriptors.FirstOrDefault(entry =>
            string.Equals(entry.Id, agentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Agent '{agentId}' is not declared by plugin '{plugin.Descriptor.Id}'.");

        var key = new AgentKey(plugin.Descriptor.Id, descriptor.Id);
        if (!_active.TryAdd(key, 0))
            throw new InvalidOperationException($"Agent '{descriptor.Name}' is already running.");
        try
        {
            _nextRuns[key] = DateTimeOffset.UtcNow.Add(IntervalOf(descriptor));
            return await ExecuteAsync(plugin, descriptor, force: true, cancellationToken);
        }
        finally
        {
            _active.TryRemove(key, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Agent polling cycle failed; it will be retried next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task RunScheduledAsync(
        IAgentPlugin plugin,
        AgentDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExecuteAsync(plugin, descriptor, force: false, cancellationToken);
            if (!result.Success)
                logger.LogWarning(
                    "Agent {AgentId} of {PluginId} reported errors: {Errors}",
                    descriptor.Id, plugin.Descriptor.Id, string.Join("; ", result.Errors ?? []));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Agent {AgentId} of {PluginId} was cancelled during shutdown.",
                descriptor.Id, plugin.Descriptor.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Agent {AgentId} of {PluginId} failed.",
                descriptor.Id, plugin.Descriptor.Id);
        }
    }

    private async Task<AgentRunResult> ExecuteAsync(
        IAgentPlugin plugin,
        AgentDescriptor descriptor,
        bool force,
        CancellationToken cancellationToken)
    {
        var job = activityStore.CreateJob(plugin.Descriptor.Id, $"Agent: {descriptor.Name}");
        job.Report(0, "starting");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, job.CancellationToken);
        try
        {
            job.Report(0.1, "running");
            var result = await plugin.RunAsync(
                new AgentRunRequest(descriptor.Id, Force: force), linked.Token);
            if (!result.Success)
            {
                var message = string.Join("; ", result.Errors ?? []);
                job.FailWithError(new PluginError("agent.run.failed", message));
                logger.LogWarning("Agent {AgentId} of {PluginId} failed: {Message}",
                    descriptor.Id, plugin.Descriptor.Id, message);
            }
            else
            {
                job.Report(1, result.Summary);
                job.Complete(result.Summary);
                logger.LogInformation("Agent {AgentId} of {PluginId} completed: {Summary}",
                    descriptor.Id, plugin.Descriptor.Id, result.Summary);
            }
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            job.Fail("Cancelled.");
            logger.LogInformation("Agent {AgentId} of {PluginId} was cancelled.",
                descriptor.Id, plugin.Descriptor.Id);
            throw;
        }
        catch (Exception exception)
        {
            job.FailWithError(new PluginError("agent.run.exception", exception.Message));
            logger.LogError(exception, "Agent {AgentId} of {PluginId} threw during a run.",
                descriptor.Id, plugin.Descriptor.Id);
            throw;
        }
    }

    private static TimeSpan IntervalOf(AgentDescriptor descriptor) =>
        descriptor.Interval > TimeSpan.Zero ? descriptor.Interval : TimeSpan.FromMinutes(5);

    private static TimeSpan ResolvePollInterval(IConfiguration configuration)
    {
        var seconds = configuration.GetValue<double?>("Agents:PollIntervalSeconds")
                      ?? DefaultPollIntervalSeconds;
        if (double.IsFinite(seconds) && seconds > 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, 86_400));
        return TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
    }

    private readonly record struct AgentKey(string PluginId, string AgentId);
}
