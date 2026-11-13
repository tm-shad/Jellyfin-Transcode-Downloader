using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Serves a transcoded download under a real filename, so it can be handed to a download
    /// manager (FDM, the browser's own downloader, a share target) instead of being buffered
    /// into the page as a blob.
    ///
    /// Jellyfin's <c>/Videos/{id}/stream.mp4</c> sends no <c>Content-Disposition</c>, so anything
    /// that downloads it by URL saves <c>stream.mp4</c>. This endpoint proxies that same stream
    /// over loopback and adds an <c>attachment</c> disposition with the name from the route; the
    /// last path segment is the filename too, for clients that ignore the header.
    ///
    /// The transcode itself is still Jellyfin's: every query parameter except the token is
    /// forwarded unchanged. Because the proxy sees the download end, it also performs the
    /// ADR-0004 cleanup (DELETE /Videos/ActiveEncodings) itself — no client-side bookkeeping.
    /// </summary>
    [ApiController]
    [Route("TranscodeDownloader")]
    public class NamedDownloadController : ControllerBase
    {
        private static readonly string[] TokenQueryKeys = { "ApiKey", "api_key" };

        private readonly IServerApplicationHost _appHost;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NamedDownloadController> _logger;

        public NamedDownloadController(
            IServerApplicationHost appHost,
            IHttpClientFactory httpClientFactory,
            ILogger<NamedDownloadController> logger)
        {
            _appHost = appHost;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// HEAD /TranscodeDownloader/Download/{itemId}/{fileName} — answers the probe most
        /// download managers send before the real request, without starting a transcode.
        /// </summary>
        [HttpHead("Download/{itemId}/{fileName}")]
        [Authorize]
        public ActionResult HeadNamedDownload([FromRoute] Guid itemId, [FromRoute] string fileName)
        {
            ApplyDownloadHeaders(SanitizeFileName(fileName));
            Response.ContentType = "video/mp4";
            return new EmptyResult();
        }

        /// <summary>
        /// GET /TranscodeDownloader/Download/{itemId}/{fileName}?{stream.mp4 query} — streams the
        /// transcode as an attachment named <paramref name="fileName"/>.
        /// </summary>
        [HttpGet("Download/{itemId}/{fileName}")]
        [Authorize]
        public async Task<ActionResult> GetNamedDownload([FromRoute] Guid itemId, [FromRoute] string fileName)
        {
            var authorization = BuildUpstreamAuthorization();
            if (authorization is null) return Unauthorized();

            var forwarded = Request.Query
                .Where(pair => !TokenQueryKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var query = QueryString.Create(forwarded);

            var localBase = _appHost.GetLocalApiUrl("127.0.0.1", Uri.UriSchemeHttp, _appHost.HttpPort);
            var upstreamUrl = $"{localBase}/Videos/{itemId:N}/stream.mp4{query.ToUriComponent()}";

            var deviceId = FirstValue(forwarded, "DeviceId");
            var playSessionId = FirstValue(forwarded, "PlaySessionId");

            var client = _httpClientFactory.CreateClient();
            // A transcode streams for as long as it takes; the client disconnecting is the only
            // thing that should end it.
            client.Timeout = Timeout.InfiniteTimeSpan;

            var aborted = HttpContext.RequestAborted;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

                using var upstream = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, aborted)
                    .ConfigureAwait(false);

                if (!upstream.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[TranscodeDownloader] Named download of {ItemId} failed upstream with HTTP {Status}",
                        itemId,
                        (int)upstream.StatusCode);
                    return StatusCode((int)upstream.StatusCode);
                }

                HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                Response.StatusCode = StatusCodes.Status200OK;
                Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "video/mp4";
                ApplyDownloadHeaders(SanitizeFileName(fileName));

                await using var body = await upstream.Content.ReadAsStreamAsync(aborted).ConfigureAwait(false);
                await body.CopyToAsync(Response.Body, 81920, aborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                // The download manager hung up; disposing the upstream response closes the
                // loopback connection and Jellyfin's kill timer takes the transcode from there.
            }
            catch (IOException) when (aborted.IsCancellationRequested)
            {
                // Same as above, surfaced as a broken pipe on the response body.
            }
            finally
            {
                await StopActiveEncodingAsync(client, localBase, authorization, deviceId, playSessionId)
                    .ConfigureAwait(false);
            }

            return new EmptyResult();
        }

        /// <summary>
        /// The loopback request needs the caller's own credentials so Jellyfin applies that
        /// user's permissions. A download manager can only carry the token in the URL, so the
        /// query is the usual source; an Authorization header (in-page fetch) is passed through.
        /// </summary>
        private string? BuildUpstreamAuthorization()
        {
            if (Request.Headers.TryGetValue(HeaderNames.Authorization, out var header)
                && !StringValues.IsNullOrEmpty(header))
            {
                return header.ToString();
            }

            foreach (var key in TokenQueryKeys)
            {
                if (Request.Query.TryGetValue(key, out var token) && !StringValues.IsNullOrEmpty(token))
                {
                    return $"MediaBrowser Token=\"{token}\"";
                }
            }

            return null;
        }

        /// <summary>
        /// Jellyfin never deletes the temp file of a progressive transcode whose ffmpeg exited on
        /// its own (ADR 0004). The proxy sees the download end, so it stops the job itself.
        /// Best-effort: failure only leaves a file in the transcode folder until the next restart.
        /// </summary>
        private async Task StopActiveEncodingAsync(
            HttpClient client,
            string localBase,
            string authorization,
            string? deviceId,
            string? playSessionId)
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(playSessionId)) return;

            try
            {
                var stopUrl = $"{localBase}/Videos/ActiveEncodings"
                    + $"?deviceId={Uri.EscapeDataString(deviceId)}&playSessionId={Uri.EscapeDataString(playSessionId)}";
                using var stop = new HttpRequestMessage(HttpMethod.Delete, stopUrl);
                stop.Headers.TryAddWithoutValidation("Authorization", authorization);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var _ = await client.SendAsync(stop, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[TranscodeDownloader] Could not stop transcode for {DeviceId}", deviceId);
            }
        }

        private void ApplyDownloadHeaders(string fileName)
        {
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(fileName);
            Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
            Response.Headers[HeaderNames.CacheControl] = "no-store";
        }

        private static string? FirstValue(IEnumerable<KeyValuePair<string, StringValues>> pairs, string key)
        {
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !StringValues.IsNullOrEmpty(pair.Value))
                {
                    return pair.Value.ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// The name comes from the client, so it is reduced to something every filesystem
        /// accepts: no path separators or reserved characters, and always an .mp4 extension.
        /// </summary>
        internal static string SanitizeFileName(string? fileName)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '"', '<', '>', ':', '|', '?', '*', '\\', '/' };
            var builder = new StringBuilder();
            foreach (var c in fileName ?? string.Empty)
            {
                if (char.IsControl(c)) continue;
                builder.Append(invalid.Contains(c) ? '_' : c);
            }

            var name = builder.ToString().Trim().Trim('.');
            if (name.Length > 200) name = name.Substring(0, 200).Trim();
            if (name.Length == 0) name = "download";
            if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name += ".mp4";
            return name;
        }
    }
}
