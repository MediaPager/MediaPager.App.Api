using System.Net;
using System.Net.Sockets;

// Host-side stream plumbing: the SSRF guard that validates any upstream URL before we
// fetch or proxy it, and the HLS resource rewriter used by the playback proxy.
// Resolving a title to an upstream URL is a stream-provider plugin's job — the host
// never talks to a specific provider itself.
namespace MediaPager.App.Api.Services;

public static class StreamingService
{
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

    private static bool IsPublicAddress(IPAddress address)
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

    // Rewrite an HLS resource reference so the client fetches it through this API.
    public static string ProxyResource(string rawUrl, Uri parent, StreamSession session)
    {
        if (!Uri.TryCreate(parent, rawUrl, out var target) || target.Scheme != Uri.UriSchemeHttps)
            return rawUrl;

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
