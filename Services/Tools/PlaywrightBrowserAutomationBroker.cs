using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MediaPager.App.PluginContracts;
using Microsoft.Playwright;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Runs browser and public-text requests in the host process with a narrow SDK
/// contract. Plugins receive only the result string, never the Playwright/browser objects.</summary>
public sealed class PlaywrightBrowserAutomationBroker(
    PlaywrightBrowserInstaller browserInstaller,
    ManagedToolsBootstrapState bootstrapState,
    ILogger<PlaywrightBrowserAutomationBroker> logger) : IBrowserAutomationBroker, IAsyncDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HttpClient _publicHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }) { Timeout = TimeSpan.FromSeconds(20) };
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<string> GetPublicTextAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!await StreamingService.IsSafeStreamUri(uri, cancellationToken))
            throw new InvalidOperationException("The browser broker only fetches public HTTPS URLs.");

        using var response = await _publicHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Redirects are not followed by the public-fetch broker.");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !await StreamingService.IsSafeStreamUri(finalUri, cancellationToken))
            throw new InvalidOperationException("The public-fetch response resolved to an unsafe URL.");
        if (response.Content.Headers.ContentLength is long length && length > MaximumResponseBytes)
            throw new InvalidOperationException("The public response exceeds the broker's size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("The public response exceeds the broker's size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    public async Task<string?> NavigateAndWaitForRequestAsync(
        BrowserAutomationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (!await StreamingService.IsSafeStreamUri(request.PageUri, cancellationToken))
            throw new InvalidOperationException("The browser broker only navigates to public HTTPS URLs.");

        await bootstrapState.WaitAsync(cancellationToken);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await browserInstaller.EnsureChromiumInstalledAsync(cancellationToken);
            var browser = await GetBrowserAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { UserAgent = request.UserAgent });
            try
            {
                var page = await context.NewPageAsync();
                await page.RouteAsync("**/*", async route =>
                {
                    try
                    {
                        var target = new Uri(route.Request.Url);
                        if (!await StreamingService.IsSafeStreamUri(target, timeout.Token) ||
                            IsBlocked(target.AbsoluteUri, request.BlockedUrlSubstrings))
                        {
                            await route.AbortAsync();
                            return;
                        }
                        await route.ContinueAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        try { await route.AbortAsync(); } catch { }
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(exception, "Blocked an invalid browser subrequest.");
                        try { await route.AbortAsync(); } catch { }
                    }
                });
                await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");

                var matchingRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                page.Request += (_, browserRequest) =>
                {
                    if (browserRequest.Url.Contains(request.RequestUrlContains, StringComparison.OrdinalIgnoreCase))
                        matchingRequest.TrySetResult(browserRequest.Url);
                };

                await page.GotoAsync(request.PageUri.AbsoluteUri, new PageGotoOptions
                {
                    Timeout = Math.Min(request.TimeoutSeconds * 1000, 45_000),
                });

                using var clickCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var clickTask = string.IsNullOrWhiteSpace(request.ClickSelector)
                    ? Task.CompletedTask
                    : ClickFramesUntilRequestAsync(page, request.ClickSelector, matchingRequest.Task, clickCancellation.Token);
                var winner = await Task.WhenAny(matchingRequest.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                clickCancellation.Cancel();
                try { await clickTask; }
                catch (OperationCanceledException) when (clickCancellation.IsCancellationRequested) { }

                if (winner == matchingRequest.Task)
                    return await matchingRequest.Task;
                return null;
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is { IsConnected: true }) return _browser;
        await _browserGate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsConnected: true }) return _browser;
            _playwright?.Dispose();
            try
            {
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = ["--disable-blink-features=AutomationControlled"],
                });
                return _browser;
            }
            catch (Exception exception)
            {
                _playwright?.Dispose();
                _playwright = null;
                throw new PluginOperationException(new PluginError(
                    "browser.chromium.unavailable",
                    "The managed Chromium browser could not start.",
                    "The host provisions the pinned browser in its tools cache. On Linux, install the desktop browser runtime libraries required by Playwright."), exception);
            }
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private static async Task ClickFramesUntilRequestAsync(
        IPage page,
        string selector,
        Task<string> matchingRequest,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!matchingRequest.IsCompleted && await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var frame in page.Frames)
            {
                if (matchingRequest.IsCompleted) return;
                try
                {
                    await frame.EvaluateAsync<bool>(
                        "selector => { const element = document.querySelector(selector); if (!element) return false; element.click(); return true; }",
                        selector);
                }
                catch (PlaywrightException)
                {
                    // A frame may navigate or detach while the page is resolving.
                }
            }
        }
    }

    private static bool IsBlocked(string url, IReadOnlyList<string> blockedSubstrings) =>
        blockedSubstrings.Any(token => url.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void ValidateRequest(BrowserAutomationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserAgent) || request.UserAgent.Length > 1024)
            throw new ArgumentException("A browser user agent of at most 1024 characters is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RequestUrlContains) || request.RequestUrlContains.Length > 256)
            throw new ArgumentException("A bounded request URL match string is required.", nameof(request));
        if (request.TimeoutSeconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(request), "Browser resolve timeout must be between 1 and 60 seconds.");
        if (request.BlockedUrlSubstrings is null || request.BlockedUrlSubstrings.Count > 100 ||
            request.BlockedUrlSubstrings.Any(token => string.IsNullOrWhiteSpace(token) || token.Length > 128))
            throw new ArgumentException("The browser block list must contain at most 100 bounded URL tokens.", nameof(request));
        if (request.ClickSelector is { Length: > 256 })
            throw new ArgumentException("The browser click selector is too long.", nameof(request));
    }

    public async ValueTask DisposeAsync()
    {
        _publicHttp.Dispose();
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch (PlaywrightException) { }
        }
        _playwright?.Dispose();
        _browserGate.Dispose();
        _operationGate.Dispose();
    }
}
