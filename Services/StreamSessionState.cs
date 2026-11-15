using System.Collections.Concurrent;

// Shared state for proxied stream sessions, plus the SSRF guard used to validate any
// upstream URL before we fetch or proxy it. Extracted from Program.cs so the
// streaming endpoints and the session record live together.
namespace MediaPager.App.Api.Services;

public sealed class StreamSession(
    string id,
    string? rootContentType,
    IReadOnlyDictionary<string, string> requestHeaders)
{
    public string Id { get; } = id;
    public string? RootContentType { get; } = rootContentType;

    /// <summary>Upstream request headers the provider asked the host to forward for this
    /// session's root URL, playlists, and segments.</summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; } = requestHeaders;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public ConcurrentDictionary<string, Uri> Resources { get; } = new();
}

public sealed class StreamSessionState
{
    private readonly ConcurrentDictionary<string, StreamSession> streams = new();

    public StreamSession Create(
        Uri rootUri,
        string? rootContentType = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        // Drop sessions older than 2 hours before adding a new one.
        foreach (var expired in streams.Where(entry => DateTimeOffset.UtcNow - entry.Value.CreatedAt > TimeSpan.FromHours(2)))
            streams.TryRemove(expired.Key, out _);

        var session = new StreamSession(
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            rootContentType,
            StreamingService.NormalizeRequestHeaders(requestHeaders));
        session.Resources["root"] = rootUri;
        streams[session.Id] = session;
        return session;
    }

    public bool TryGet(string streamId, string resourceId, out StreamSession session, out Uri uri)
    {
        session = null!;
        uri = null!;
        if (!streams.TryGetValue(streamId, out var found) ||
            DateTimeOffset.UtcNow - found.CreatedAt > TimeSpan.FromHours(2))
            return false;
        if (!found.Resources.TryGetValue(resourceId, out var target))
            return false;
        session = found;
        uri = target;
        return true;
    }
}
