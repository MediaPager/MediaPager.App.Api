using System.Net;
using System.Text;
using System.Text.Json;
using MediaPager.Plugins.Subtitles.OpenSubtitles.OpenSubtitles;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class OpenSubtitlesApiTests
{
    [Fact]
    public async Task Fetch_requires_username_and_password()
    {
        var api = new OpenSubtitlesApi(new HttpClient(new QueueHandler()));

        var error = await Assert.ThrowsAsync<OpenSubtitlesApiException>(() =>
            api.FetchSrtAsync("api-key", null, null, 123, CancellationToken.None));

        Assert.Contains("username and password", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_reports_rejected_account_credentials_without_echoing_them()
    {
        var handler = new QueueHandler(JsonResponse(HttpStatusCode.Unauthorized, "{}"));
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<OpenSubtitlesApiException>(() =>
            api.FetchSrtAsync("api-key", "alice", "very-secret-password", 123, CancellationToken.None));

        Assert.Contains("rejected the account login", error.Message);
        Assert.DoesNotContain("very-secret-password", error.Message);
    }

    [Fact]
    public async Task Fetch_uses_login_token_and_returns_subtitle_content()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-a\"}"),
            JsonResponse(HttpStatusCode.OK, "{\"link\":\"https://files.example.test/subtitle.srt\"}"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHello"),
            });
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        var result = await api.FetchSrtAsync("api-key", "alice", "account-password", 123, CancellationToken.None);

        Assert.Contains("Hello", result);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/api/v1/login", handler.Requests[0].Path);
        Assert.Equal("alice", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("username").GetString());
        Assert.Equal("/api/v1/download", handler.Requests[1].Path);
        Assert.Equal("Bearer token-a", handler.Requests[1].Authorization);
        Assert.Null(handler.Requests[2].ApiKey);
        Assert.Null(handler.Requests[2].Authorization);
    }

    [Fact]
    public async Task Fetch_refreshes_a_rejected_token_once()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-old\"}"),
            JsonResponse(HttpStatusCode.Unauthorized, "{}"),
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-new\"}"),
            JsonResponse(HttpStatusCode.OK, "{\"link\":\"https://files.example.test/subtitle.srt\"}"),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("subtitle") });
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        var result = await api.FetchSrtAsync("api-key", "alice", "account-password", 123, CancellationToken.None);

        Assert.Equal("subtitle", result);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("Bearer token-old", handler.Requests[1].Authorization);
        Assert.Equal("/api/v1/login", handler.Requests[2].Path);
        Assert.Equal("Bearer token-new", handler.Requests[3].Authorization);
    }

    [Fact]
    public async Task Fetch_does_not_reuse_a_token_after_credentials_change()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-a\"}"),
            JsonResponse(HttpStatusCode.OK, "{\"link\":\"https://files.example.test/a.srt\"}"),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("subtitle-a") },
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-b\"}"),
            JsonResponse(HttpStatusCode.OK, "{\"link\":\"https://files.example.test/b.srt\"}"),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("subtitle-b") });
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        await api.FetchSrtAsync("api-key", "alice", "password-a", 123, CancellationToken.None);
        var result = await api.FetchSrtAsync("api-key", "bob", "password-b", 456, CancellationToken.None);

        Assert.Equal("subtitle-b", result);
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/api/v1/login"));
        Assert.Equal("bob", JsonDocument.Parse(handler.Requests[3].Body).RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Fetch_reports_file_link_failure_without_echoing_the_link()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-a\"}"),
            JsonResponse(HttpStatusCode.OK, "{\"link\":\"https://files.example.test/subtitle.srt?token=secret\"}"),
            JsonResponse(HttpStatusCode.Forbidden, "{}"));
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<OpenSubtitlesApiException>(() =>
            api.FetchSrtAsync("api-key", "alice", "account-password", 123, CancellationToken.None));

        Assert.Contains("HTTP 403", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Fetch_reports_an_expired_file_link_as_an_error()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-a\"}"),
            JsonResponse(HttpStatusCode.OK, "{\"link\":\"https://files.example.test/subtitle.srt?token=secret\"}"),
            JsonResponse(HttpStatusCode.NotFound, "{}"));
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<OpenSubtitlesApiException>(() =>
            api.FetchSrtAsync("api-key", "alice", "account-password", 123, CancellationToken.None));

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Fetch_returns_null_for_a_missing_file()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, "{\"token\":\"token-a\"}"),
            JsonResponse(HttpStatusCode.NotFound, "{}"));
        var api = new OpenSubtitlesApi(new HttpClient(handler));

        var result = await api.FetchSrtAsync("api-key", "alice", "account-password", 123, CancellationToken.None);

        Assert.Null(result);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(string Method, string Path, string? ApiKey, string? Authorization, string Body);

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("Api-Key", out var apiKeyValues);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                apiKeyValues?.FirstOrDefault(),
                request.Headers.Authorization?.ToString(),
                body));
            if (_responses.Count == 0)
                throw new InvalidOperationException("No fake OpenSubtitles response was queued.");
            return _responses.Dequeue();
        }
    }
}
