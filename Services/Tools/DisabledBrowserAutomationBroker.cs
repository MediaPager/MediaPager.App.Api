namespace MediaPager.App.Api.Services.Tools;

/// <summary>Fallback registration when the optional Playwright broker is compiled out.
/// The plugin contract stays intact; any plugin that requests browser automation fails
/// with an explicit error instead of taking a dependency on a browser runtime.</summary>
public sealed class DisabledBrowserAutomationBroker : IBrowserAutomationBroker
{
    private const string DisabledMessage =
        "Browser automation is disabled in this build. Re-enable MEDIAPAGER_PLAYWRIGHT to use it.";

    public Task<string> GetPublicTextAsync(Uri uri, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(DisabledMessage);

    public Task<string> GetPublicTextAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(DisabledMessage);

    public Task<string?> NavigateAndWaitForRequestAsync(
        BrowserAutomationRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(DisabledMessage);
}
