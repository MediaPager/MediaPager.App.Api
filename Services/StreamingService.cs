using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;

// Host-side stream plumbing: the SSRF guard that validates any upstream URL before we
// fetch or proxy it, and the HLS resource rewriter used by the playback proxy.
// Resolving a title to an upstream URL is a stream-provider plugin's job — the host
// never talks to a specific provider itself.
namespace MediaPager.App.Api.Services;

public static class StreamingService
{
    public const int MediaSniffBytes = 4096;
    public const int MaximumPlaylistBytes = 2 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> NoRequestHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // A plugin may attach upstream request headers (for example a referer the upstream
    // requires). Drop framing/hop-by-hop headers so a plugin cannot corrupt the proxied
    // request, and bound the count and sizes.
    private static readonly HashSet<string> ReservedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Connection", "Keep-Alive",
        "Transfer-Encoding", "Upgrade", "TE", "Trailer", "Range", "Expect",
        "Proxy-Connection", "Proxy-Authorization",
    };

    public static IReadOnlyDictionary<string, string> NormalizeRequestHeaders(
        IReadOnlyDictionary<string, string>? requestHeaders)
    {
        if (requestHeaders is null || requestHeaders.Count == 0)
            return NoRequestHeaders;

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in requestHeaders)
        {
            if (normalized.Count >= 16) break;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) continue;
            if (value is null || value.Length > 1024 || value.Contains('\r') || value.Contains('\n')) continue;
            var headerName = name.Trim();
            if (ReservedRequestHeaders.Contains(headerName)) continue;
            normalized[headerName] = value.Trim();
        }

        return normalized;
    }

    public static void ApplyRequestHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> requestHeaders)
    {
        foreach (var (name, value) in requestHeaders)
            request.Headers.TryAddWithoutValidation(name, value);
    }

    // Only allow HTTPS URLs that resolve to public IP addresses (SSRF guard).
    public static async Task<bool> IsSafeStreamUri(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        try
        {
            var addresses = IPAddress.TryParse(uri.DnsSafeHost, out var address)
                ? [address]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19);
        }

        return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6Loopback) && !address.IsIPv6LinkLocal &&
            !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && (bytes[0] & 0xfe) != 0xfc;
    }

    /// <summary>Recognize the supported media payloads from bytes, not just a caller-controlled
    /// extension or upstream Content-Type. HLS manifests are validated separately.</summary>
    public static bool TryClassifyMediaPayload(
        Uri uri,
        string? declaredContentType,
        ReadOnlySpan<byte> prefix,
        bool allowHlsAuxiliaryResource,
        long? contentLength,
        out string mediaType)
    {
        mediaType = "application/octet-stream";
        var declared = declaredContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";

        if (IsTransportStream(prefix))
        {
            mediaType = "video/mp2t";
            return true;
        }
        if (IsIsoBaseMedia(prefix))
        {
            mediaType = declared.StartsWith("audio/", StringComparison.Ordinal) ? declared : "video/mp4";
            return true;
        }
        if (HasBytes(prefix, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            mediaType = declared.StartsWith("audio/", StringComparison.Ordinal) ? declared : "video/webm";
            return true;
        }
        if (HasBytes(prefix, [0x00, 0x00, 0x01, 0xBA]))
        {
            mediaType = "video/mpeg";
            return true;
        }
        if (HasBytes(prefix, [0x4F, 0x67, 0x67, 0x53]))
        {
            mediaType = declared is "video/ogg" or "audio/ogg" ? declared : "application/ogg";
            return true;
        }
        if (HasBytes(prefix, [0x66, 0x4C, 0x61, 0x43]))
        {
            mediaType = "audio/flac";
            return true;
        }
        if (HasBytes(prefix, [0x49, 0x44, 0x33]) || LooksLikeMpegAudioFrame(prefix))
        {
            mediaType = "audio/mpeg";
            return true;
        }
        if (LooksLikeAdts(prefix))
        {
            mediaType = "audio/aac";
            return true;
        }
        if (allowHlsAuxiliaryResource && HasAsciiPrefix(prefix, "WEBVTT") &&
            (declared == "text/vtt" || uri.AbsolutePath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)))
        {
            mediaType = "text/vtt";
            return true;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (allowHlsAuxiliaryResource &&
            (string.Equals(extension, ".key", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase)) &&
            (contentLength is null or <= 64) && prefix.Length is > 0 and <= 64)
        {
            mediaType = "application/octet-stream";
            return true;
        }

        return false;
    }

    public static bool LooksLikeHlsPlaylist(ReadOnlySpan<byte> prefix)
    {
        var offset = prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF ? 3 : 0;
        while (offset < prefix.Length && prefix[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
        return prefix[offset..].StartsWith(Encoding.ASCII.GetBytes("#EXTM3U"));
    }

    public static bool IsValidHlsPlaylist(string playlist)
    {
        if (!playlist.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
            return false;
        return playlist.Contains("#EXTINF", StringComparison.Ordinal) ||
            playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) ||
            playlist.Contains("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase) ||
            playlist.Contains("#EXT-X-PART", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransportStream(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset + (188 * 2) < data.Length; offset++)
        {
            if (data[offset] != 0x47) continue;
            var packetCount = 1;
            while (offset + (packetCount * 188) < data.Length && data[offset + (packetCount * 188)] == 0x47)
                packetCount++;
            if (packetCount >= 3) return true;
        }
        return false;
    }

    private static bool IsIsoBaseMedia(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        var boxSize = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if (boxSize is < 8 and not 0) return false;
        var type = Encoding.ASCII.GetString(data.Slice(4, 4));
        return type is "ftyp" or "styp" or "moof" or "moov" or "sidx" or "free" or "skip" or "mdat" or "emsg";
    }

    private static bool LooksLikeMpegAudioFrame(ReadOnlySpan<byte> data)
    {
        for (var index = 0; index + 1 < Math.Min(data.Length, 16); index++)
        {
            if (data[index] == 0xFF && (data[index + 1] & 0xE0) == 0xE0 && (data[index + 1] & 0x06) != 0)
                return true;
        }
        return false;
    }

    private static bool LooksLikeAdts(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xF6) == 0xF0;

    private static bool HasAsciiPrefix(ReadOnlySpan<byte> data, string value) =>
        data.StartsWith(Encoding.ASCII.GetBytes(value));

    private static bool HasBytes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.StartsWith(prefix);

    // Rewrite an HLS resource reference so the client fetches it through this API.
    public static string ProxyResource(string rawUrl, Uri parent, StreamSession session)
    {
        if (!Uri.TryCreate(parent, rawUrl, out var target) ||
            target.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(target.UserInfo))
            throw new InvalidDataException("A media manifest referenced a non-HTTPS or invalid resource.");

        // HLS playlists normally use relative references, while capability URLs can carry a
        // short-lived token in the parent query. Relative resolution drops that query, so
        // carry it forward when the reference does not supply one of its own.
        if (string.IsNullOrEmpty(target.Query) && !string.IsNullOrEmpty(parent.Query))
        {
            target = new Uri(
                target.GetLeftPart(UriPartial.Path) + parent.Query + target.Fragment,
                UriKind.Absolute);
        }

        var resourceId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        session.Resources[resourceId] = target;

        // ffmpeg's HLS demuxer rejects segment URLs without a whitelisted extension,
        // so carry the upstream extension over (segments default to .ts).
        var extension = Path.GetExtension(target.AbsolutePath);
        if (string.IsNullOrEmpty(extension) || extension.Length > 5)
            extension = target.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? ".m3u8" : ".ts";
        return $"/stream/{session.Id}/{resourceId}{extension}";
    }
}
