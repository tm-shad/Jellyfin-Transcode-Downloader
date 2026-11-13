# Named download endpoint for download managers and batches

Downloads were always fetched into the page as a Blob and saved with `<a download>`. That gives a
progress bar and a queue, but nothing outside the page can take part: a download manager (FDM on
Android, a desktop extension) never sees a link, and every handoff to the outside — the Android
app's browser handoff (ADR 0005), a copied link — saved `stream.mp4`, because Jellyfin's
`/Videos/{id}/stream.mp4` sends no `Content-Disposition`.

`NamedDownloadController` adds `GET|HEAD /TranscodeDownloader/Download/{itemId}/{fileName}`. It
forwards every query parameter except the token to `/Videos/{itemId}/stream.mp4` over loopback
(`127.0.0.1:{HttpPort}{BaseUrl}`, built with `IServerApplicationHost.GetLocalApiUrl`), passing the
caller's token as an `Authorization: MediaBrowser Token="…"` header so Jellyfin applies that
user's permissions, and streams the body back with `Content-Disposition: attachment` and the
sanitised filename. The filename is also the last path segment, for clients that ignore the
header. HEAD answers without starting a transcode, since download managers probe first.

Because the proxy sees the transfer end, it performs the ADR-0004 stop
(`DELETE /Videos/ActiveEncodings`) itself; external downloads need no client bookkeeping, and the
ADR-0005 deferred-stop queue is no longer fed (old entries are still flushed).

The client gains a remembered **Save with** choice (`page` | `external`). External mode, and the
Android app (forced external), hand the named link out: the Web Share sheet on Android browsers
(download managers are share targets; a navigation would always stay in Chrome), a plain
navigation elsewhere. Seasons and series (Jellyfin's `downloadall` menu entry) become a batch of
their non-virtual episodes: queued in page mode, or listed with per-episode Send/Copy and Copy-all
in external mode, since a page can only hand off one link per user gesture.

Tokens are sent as `ApiKey=` (and the episode list uses the `Authorization` header) because
Jellyfin 12 disables the legacy `api_key` parameter by default.
