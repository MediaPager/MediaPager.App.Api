using System.Text;
using System.Net;
using MediaPager.App.Api.Services;
using MediaPager.App.Api.Controllers;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class StreamingServiceTests
{
    [Fact]
    public async Task ProxyRequestRetriesTransientUpstreamFailures()
    {
        var calls = 0;
        using var client = new HttpClient(new TestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(
                calls == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
        }));

        using var response = await StreamingController.SendWithRetryAsync(
            client,
            new Uri("https://media.example/segment.ts"),
            new Dictionary<string, string>(),
            rangeHeader: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ProxyRequestDoesNotRetryPermanentClientErrors()
    {
        var calls = 0;
        using var client = new HttpClient(new TestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        using var response = await StreamingController.SendWithRetryAsync(
            client,
            new Uri("https://media.example/segment.ts"),
            new Dictionary<string, string>(),
            rangeHeader: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void HlsManifestIsRecognizedButHeaderOnlyTextIsRejectedAsAPlayableManifest()
    {
        var manifest = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nsegment.ts\n");
        Assert.True(StreamingService.LooksLikeHlsPlaylist(manifest));
        Assert.True(StreamingService.IsValidHlsPlaylist(Encoding.UTF8.GetString(manifest)));

        const string headerOnly = "#EXTM3U\n";
        Assert.True(StreamingService.LooksLikeHlsPlaylist(Encoding.UTF8.GetBytes(headerOnly)));
        Assert.False(StreamingService.IsValidHlsPlaylist(headerOnly));
    }

    [Fact]
    public void MediaClassificationRequiresRecognizedPayloadBytes()
    {
        var transportStream = new byte[188 * 3];
        transportStream[0] = 0x47;
        transportStream[188] = 0x47;
        transportStream[376] = 0x47;
        Assert.True(StreamingService.TryClassifyMediaPayload(
            new Uri("https://media.example/live"), "text/html", transportStream, false, transportStream.Length, out var mediaType));
        Assert.Equal("video/mp2t", mediaType);

        Assert.False(StreamingService.TryClassifyMediaPayload(
            new Uri("https://media.example/page"), "text/html", Encoding.UTF8.GetBytes("<html>not media</html>"), false, null, out _));
    }

    [Fact]
    public void IsoBaseMediaRequiresARecognizedBoxSignature()
    {
        var mp4 = new byte[16];
        mp4[3] = 16;
        Encoding.ASCII.GetBytes("ftyp").CopyTo(mp4, 4);
        Assert.True(StreamingService.TryClassifyMediaPayload(
            new Uri("https://media.example/extensionless"), "application/octet-stream", mp4, false, mp4.Length, out var mediaType));
        Assert.Equal("video/mp4", mediaType);

        var invalid = new byte[16];
        Assert.False(StreamingService.TryClassifyMediaPayload(
            new Uri("https://media.example/extensionless"), "application/octet-stream", invalid, false, invalid.Length, out _));
    }

    [Fact]
    public void ProxyResourceRejectsNonHttpsManifestReferences()
    {
        var session = new StreamSessionState().Create(new Uri("https://media.example/master.m3u8"));

        Assert.Throws<InvalidDataException>(() => StreamingService.ProxyResource(
            "http://media.example/segment.ts", new Uri("https://media.example/master.m3u8"), session));
        Assert.Throws<InvalidDataException>(() => StreamingService.ProxyResource(
            "javascript:alert(1)", new Uri("https://media.example/master.m3u8"), session));
    }

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
