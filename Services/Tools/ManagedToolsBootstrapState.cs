namespace MediaPager.App.Api.Services.Tools;

/// <summary>Signals when first-boot tool setup has settled. Media operations may wait on this
/// without delaying startup or unrelated API requests.</summary>
public sealed class ManagedToolsBootstrapState
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsComplete => _completion.Task.IsCompleted;

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        _completion.Task.WaitAsync(cancellationToken);

    internal void Complete() => _completion.TrySetResult();
}
