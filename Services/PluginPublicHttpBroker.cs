using MediaPager.App.Core.Services;
using MediaPager.App.PluginContracts;
using System.Net;
using System.Net.Sockets;

namespace MediaPager.App.Api.Services;

/// <summary>Host-side, bounded HTTPS document fetches for explicitly granted plugins.</summary>
public sealed class PluginPublicHttpBroker(IHttpClientFactory httpClientFactory) : IPluginPublicHttpBroker
{
    public const int MaximumResponseBytes = 64 * 1024 * 1024;
    private const int MaximumRedirects = 5;

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = ConnectToValidatedPublicAddressAsync,
    };

    public async Task<PluginPublicHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var current = uri;
        using var client = httpClientFactory.CreateClient("plugin-public-fetch");

        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            if (!await StreamingService.IsSafeStreamUri(current, cancellationToken))
                throw new InvalidOperationException("The public-fetch capability only accepts public HTTPS URLs.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("*/*");
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is null || redirectCount == MaximumRedirects)
                    throw new InvalidOperationException("The public-fetch response returned an invalid or excessive redirect.");
                current = new Uri(current, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumResponseBytes)
                throw new InvalidOperationException("The public response exceeds the 64 MiB size limit.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                if (destination.Length + read > MaximumResponseBytes)
                    throw new InvalidOperationException("The public response exceeds the 64 MiB size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return new PluginPublicHttpResponse(
                destination.ToArray(),
                response.Content.Headers.ContentType?.ToString(),
                current);
        }

        throw new InvalidOperationException("The public-fetch redirect limit was exceeded.");
    }

    private static async ValueTask<Stream> ConnectToValidatedPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !StreamingService.IsPublicAddress(address)))
            throw new HttpRequestException("The public-fetch destination resolved to a non-public address.");

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastException = exception;
                if (exception is OperationCanceledException) throw;
            }
        }

        throw new HttpRequestException("The public-fetch destination could not be reached.", lastException);
    }
}
