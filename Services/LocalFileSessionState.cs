using System.Collections.Concurrent;

namespace MediaPager.App.Api.Services;

// Short-lived capability URLs for local library playback. The browser media element
// can't attach our bearer token, so (mirroring the streaming proxy in StreamingService)
// the client asks the API to mint a random, expiring token for a file and the media
// element plays /local/{capability} anonymously.
public sealed class LocalFileSessionState
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, (string Path, DateTimeOffset ExpiresAt)> sessions = new();

    public string Create(string path)
    {
        PurgeExpired();
        var token = Guid.NewGuid().ToString("N");
        sessions[token] = (path, DateTimeOffset.UtcNow + Lifetime);
        return token;
    }

    public string? TryGet(string token)
    {
        if (!sessions.TryGetValue(token, out var session)) return null;
        if (DateTimeOffset.UtcNow > session.ExpiresAt)
        {
            sessions.TryRemove(token, out _);
            return null;
        }
        return session.Path;
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in sessions)
        {
            if (now > pair.Value.ExpiresAt) sessions.TryRemove(pair.Key, out _);
        }
    }
}