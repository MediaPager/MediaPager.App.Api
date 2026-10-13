namespace MediaPager.App.Api.Helpers;

public static class FrontendUrlPolicy
{
    // Permit HTTPS publicly and HTTP only for loopback/localhost or RFC1918 IPv4 LANs.
    public static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }
}
