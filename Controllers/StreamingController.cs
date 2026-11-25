using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
public sealed class StreamingController(
    IHttpClientFactory httpClientFactory,
    StreamSessionState sessions) : ControllerBase
{
    private const int MaximumUpstreamAttempts = 3;

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
            try
            {
                upstreamResponse = await SendWithRetryAsync(
                    client,
                    currentUri,
                    session.RequestHeaders,
                    Request.Headers.TryGetValue("Range", out var range) ? range.ToString() : null,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Problem("The upstream media resource could not be reached after retries.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Problem("The upstream media resource timed out after retries.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
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
            if (!upstreamResponse.IsSuccessStatusCode)
                return Problem("The upstream did not return a successful media response.", statusCode: StatusCodes.Status502BadGateway);

            var mediaType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var declaredRootPlaylist =
                resourceId == "root" &&
                session.RootContentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true;
            var isPlaylist = currentUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                declaredRootPlaylist;

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            var sniff = await ReadPrefixAsync(upstreamStream, StreamingService.MediaSniffBytes, cancellationToken);
            var looksLikePlaylist = StreamingService.LooksLikeHlsPlaylist(sniff);

            if (isPlaylist || looksLikePlaylist)
            {
                if (!looksLikePlaylist || upstreamResponse.Content.Headers.ContentLength is > StreamingService.MaximumPlaylistBytes)
                    return Problem("The upstream response was not a valid supported media playlist.", statusCode: StatusCodes.Status502BadGateway);

                using var buffer = new MemoryStream();
                buffer.Write(sniff);
                if (!await CopyRemainingBoundedAsync(upstreamStream, buffer, StreamingService.MaximumPlaylistBytes, cancellationToken))
                    return Problem("The upstream media playlist exceeded the size limit.", statusCode: StatusCodes.Status502BadGateway);
                var playlist = Encoding.UTF8.GetString(buffer.ToArray());
                if (!StreamingService.IsValidHlsPlaylist(playlist))
                    return Problem("The upstream response did not contain a playable HLS playlist.", statusCode: StatusCodes.Status502BadGateway);

                var lines = playlist.Replace("\r\n", "\n").Split('\n');
                try
                {
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
                }
                catch (InvalidDataException)
                {
                    return Problem("The media playlist contains an unsupported or unsafe resource.", statusCode: StatusCodes.Status502BadGateway);
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

            if (!StreamingService.TryClassifyMediaPayload(
                    currentUri,
                    mediaType,
                    sniff,
                    allowHlsAuxiliaryResource: resourceId != "root",
                    upstreamResponse.Content.Headers.ContentLength,
                    out var validatedContentType))
                return Problem("The upstream response was not a recognized supported media stream.",
                    statusCode: StatusCodes.Status502BadGateway);

            Response.StatusCode = (int)upstreamResponse.StatusCode;
            Response.ContentType = validatedContentType;
            if (upstreamResponse.Content.Headers.ContentLength is long contentLength)
                Response.ContentLength = contentLength;
            if (upstreamResponse.Content.Headers.ContentRange is not null)
                Response.Headers.ContentRange = upstreamResponse.Content.Headers.ContentRange.ToString();
            if (upstreamResponse.Headers.AcceptRanges.Count > 0)
                Response.Headers.AcceptRanges = string.Join(", ", upstreamResponse.Headers.AcceptRanges);

            await Response.Body.WriteAsync(sniff, cancellationToken);
            await upstreamStream.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }
    }

    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Uri uri,
        IReadOnlyDictionary<string, string> requestHeaders,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            StreamingService.ApplyRequestHeaders(request, requestHeaders);
            if (!string.IsNullOrWhiteSpace(rangeHeader))
                request.Headers.TryAddWithoutValidation("Range", rangeHeader);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaximumUpstreamAttempts)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt < MaximumUpstreamAttempts)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            if (attempt >= MaximumUpstreamAttempts || !IsTransient(response.StatusCode))
                return response;

            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            await Task.Delay(retryAfter is { } delay && delay > TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, 1_000))
                : RetryDelay(attempt), cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode is >= 500 and <= 599;

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt == 1 ? 150 : 350);

    private static async Task<byte[]> ReadPrefixAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    private static async Task<bool> CopyRemainingBoundedAsync(
        Stream source,
        Stream destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return true;
            if (destination.Length + read > maximumBytes) return false;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
