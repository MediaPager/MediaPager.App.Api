using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
public sealed class StreamingController(
    IHttpClientFactory httpClientFactory,
    StreamSessionState sessions) : ControllerBase
{
    // GET /stream/{streamId}/{resourceId} — proxy a stream session through this API.
    [HttpGet("stream/{streamId}/{resourceId}")]
    [AllowAnonymous]
    public async Task<IActionResult> ProxyStream(string streamId, string resourceId, CancellationToken cancellationToken)
    {
        // ProxyResource may append an extension for ffmpeg's sake; strip it to find the resource.
        var dotIndex = resourceId.IndexOf('.');
        if (dotIndex > 0)
            resourceId = resourceId[..dotIndex];

        if (!sessions.TryGet(streamId, resourceId, out var session, out var upstreamUri))
            return NotFound(new { error = "Stream expired or not found." });

        if (!await StreamingService.IsSafeStreamUri(upstreamUri, cancellationToken))
            return BadRequest(new { error = "The stream URL is not a safe HTTPS URL." });

        using var client = httpClientFactory.CreateClient("stream");
        HttpResponseMessage? upstreamResponse = null;
        var currentUri = upstreamUri;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
            StreamingService.ApplyRequestHeaders(upstreamRequest, session.RequestHeaders);
            if (Request.Headers.TryGetValue("Range", out var range))
                upstreamRequest.Headers.TryAddWithoutValidation("Range", range.ToString());

            upstreamResponse = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!((int)upstreamResponse.StatusCode is >= 300 and < 400) || upstreamResponse.Headers.Location is null)
                break;

            var redirectUri = new Uri(currentUri, upstreamResponse.Headers.Location);
            upstreamResponse.Dispose();
            upstreamResponse = null;
            if (redirectCount == 5 || !await StreamingService.IsSafeStreamUri(redirectUri, cancellationToken))
                return Problem("The stream returned an unsafe or excessive redirect.", statusCode: 502);
            currentUri = redirectUri;
        }

        if (upstreamResponse is null)
            return Problem("The stream could not be reached.", statusCode: 502);

        using (upstreamResponse)
        {
            var mediaType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var declaredRootPlaylist = upstreamResponse.IsSuccessStatusCode &&
                resourceId == "root" &&
                session.RootContentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true;
            var isPlaylist = currentUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                declaredRootPlaylist;

            // Extensionless HLS references can be given a synthetic extension for ffmpeg, and
            // some upstreams label playlists as text/plain. Sniff the body before rewriting.
            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            var sniff = new byte[7];
            var sniffRead = await upstreamStream.ReadAsync(sniff.AsMemory(0, sniff.Length), cancellationToken);
            var looksLikePlaylist = sniffRead == sniff.Length &&
                Encoding.ASCII.GetString(sniff) == "#EXTM3U";

            if (upstreamResponse.IsSuccessStatusCode && (isPlaylist || looksLikePlaylist))
            {
                using var buffer = new MemoryStream();
                buffer.Write(sniff, 0, sniffRead);
                await upstreamStream.CopyToAsync(buffer, cancellationToken);
                var playlist = Encoding.UTF8.GetString(buffer.ToArray());
                var lines = playlist.Replace("\r\n", "\n").Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (lines[index].StartsWith('#'))
                    {
                        lines[index] = Regex.Replace(lines[index], "URI=\"([^\"]+)\"", match =>
                            $"URI=\"{StreamingService.ProxyResource(match.Groups[1].Value, currentUri, session)}\"",
                            RegexOptions.IgnoreCase);
                    }
                    else if (!string.IsNullOrWhiteSpace(lines[index]))
                    {
                        lines[index] = StreamingService.ProxyResource(lines[index].Trim(), currentUri, session);
                    }
                }

                var rewritten = string.Join('\n', lines);

                Response.StatusCode = (int)upstreamResponse.StatusCode;
                Response.ContentType = declaredRootPlaylist
                    ? session.RootContentType
                    : mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                        ? upstreamResponse.Content.Headers.ContentType!.ToString()
                        : "application/vnd.apple.mpegurl";
                await Response.WriteAsync(rewritten, cancellationToken);
                return new EmptyResult();
            }

            Response.StatusCode = (int)upstreamResponse.StatusCode;
            // The upstream often mislabels media segments as text/html, which makes the browser's
            // HLS engine reject them. Serve a correct media type by path, falling back to binary.
            var segmentPath = currentUri.AbsolutePath.ToLowerInvariant();
            Response.ContentType = upstreamResponse.IsSuccessStatusCode &&
                resourceId == "root" && !string.IsNullOrWhiteSpace(session.RootContentType)
                ? session.RootContentType
                : segmentPath switch
            {
                _ when segmentPath.EndsWith(".ts") => "video/mp2t",
                _ when segmentPath.EndsWith(".m4s") || segmentPath.EndsWith(".mp4") || segmentPath.EndsWith(".cmfv") => "video/mp4",
                _ when segmentPath.EndsWith(".m4a") || segmentPath.EndsWith(".aac") || segmentPath.EndsWith(".cmfa") => "audio/mp4",
                _ when segmentPath.EndsWith(".key") || segmentPath.EndsWith(".bin") => "application/octet-stream",
                _ when segmentPath.EndsWith(".vtt") => "text/vtt",
                _ => upstreamResponse.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                    ? "application/octet-stream"
                    : upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            };
            if (upstreamResponse.Content.Headers.ContentLength is long contentLength)
                Response.ContentLength = contentLength;
            if (upstreamResponse.Content.Headers.ContentRange is not null)
                Response.Headers.ContentRange = upstreamResponse.Content.Headers.ContentRange.ToString();
            if (upstreamResponse.Headers.AcceptRanges.Count > 0)
                Response.Headers.AcceptRanges = string.Join(", ", upstreamResponse.Headers.AcceptRanges);

            await Response.Body.WriteAsync(sniff.AsMemory(0, sniffRead), cancellationToken);
            await upstreamStream.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }
    }
}
