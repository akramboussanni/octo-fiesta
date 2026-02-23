using System.Net;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace octo_fiesta.Services.YouTube;

/// <summary>
/// Metadata service for YouTube using the YouTube Data API v3.
/// Maps YouTube videos to Songs, playlists to Albums/ExternalPlaylists, and channels to Artists.
/// When MusicOnly is enabled (default), restricts video/channel searches to the music topic
/// (topicId=/m/04rlf) so results closely mirror YouTube Music.
/// </summary>
public class YouTubeMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly bool _musicOnly;

    private const string BaseUrl = "https://www.googleapis.com/youtube/v3";

    // YouTube music topic ID (Freebase: /m/04rlf) — used by YouTube Music itself
    private const string MusicTopicId = "/m/04rlf";

    public YouTubeMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<YouTubeSettings> settings)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKey = settings.Value.ApiKey;
        _musicOnly = settings.Value.MusicOnly;
    }

    // ─────────────────────────── Search ───────────────────────────

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return new List<Song>();

            var topicFilter = _musicOnly ? $"&topicId={Uri.EscapeDataString(MusicTopicId)}" : "";
            var url = $"{BaseUrl}/search?part=snippet&type=video&q={Uri.EscapeDataString(query)}&maxResults={limit}{topicFilter}&key={_apiKey}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Song>();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
                return new List<Song>();

            var songs = new List<Song>();
            var videoIds = new List<string>();

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) ||
                    !idEl.TryGetProperty("videoId", out var videoIdEl))
                    continue;

                var videoId = videoIdEl.GetString();
                if (string.IsNullOrEmpty(videoId)) continue;

                videoIds.Add(videoId);
                songs.Add(ParseSearchResultVideo(item, videoId));
            }

            // Batch-fetch durations via videos.list (1 quota unit for all)
            if (videoIds.Count > 0)
            {
                await EnrichWithDurationsAsync(songs, videoIds);
            }

            return songs;
        }
        catch
        {
            return new List<Song>();
        }
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return new List<Album>();

            // YouTube has no "album" concept — search playlists as the closest equivalent.
            // Append "album" to the query when MusicOnly is set to bias results toward music albums.
            var effectiveQuery = _musicOnly ? $"{query} album" : query;
            var url = $"{BaseUrl}/search?part=snippet&type=playlist&q={Uri.EscapeDataString(effectiveQuery)}&maxResults={limit}&key={_apiKey}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Album>();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
                return new List<Album>();

            var albums = new List<Album>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) ||
                    !idEl.TryGetProperty("playlistId", out var playlistIdEl))
                    continue;

                var playlistId = playlistIdEl.GetString();
                if (string.IsNullOrEmpty(playlistId)) continue;

                albums.Add(ParseSearchResultPlaylistAsAlbum(item, playlistId));
            }

            return albums;
        }
        catch
        {
            return new List<Album>();
        }
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return new List<Artist>();

            var topicFilter = _musicOnly ? $"&topicId={Uri.EscapeDataString(MusicTopicId)}" : "";
            var url = $"{BaseUrl}/search?part=snippet&type=channel&q={Uri.EscapeDataString(query)}&maxResults={limit}{topicFilter}&key={_apiKey}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Artist>();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
                return new List<Artist>();

            var artists = new List<Artist>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) ||
                    !idEl.TryGetProperty("channelId", out var channelIdEl))
                    continue;

                var channelId = channelIdEl.GetString();
                if (string.IsNullOrEmpty(channelId)) continue;

                artists.Add(ParseSearchResultChannel(item, channelId));
            }

            return artists;
        }
        catch
        {
            return new List<Artist>();
        }
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var songsTask = SearchSongsAsync(query, songLimit);
        var albumsTask = SearchAlbumsAsync(query, albumLimit);
        var artistsTask = SearchArtistsAsync(query, artistLimit);

        await Task.WhenAll(songsTask, albumsTask, artistsTask);

        return new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };
    }

    // ─────────────────────────── Get by ID ───────────────────────────

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "youtube") return null;
        if (string.IsNullOrWhiteSpace(_apiKey)) return null;

        try
        {
            var url = $"{BaseUrl}/videos?part=snippet,contentDetails&id={Uri.EscapeDataString(externalId)}&key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            return ParseVideoFull(items[0], externalId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "youtube") return null;
        if (string.IsNullOrWhiteSpace(_apiKey)) return null;

        try
        {
            // Get playlist metadata
            var playlistUrl = $"{BaseUrl}/playlists?part=snippet,contentDetails&id={Uri.EscapeDataString(externalId)}&key={_apiKey}";
            var playlistResponse = await _httpClient.GetAsync(playlistUrl);
            if (!playlistResponse.IsSuccessStatusCode) return null;

            var playlistJson = await playlistResponse.Content.ReadAsStringAsync();
            var playlistDoc = JsonDocument.Parse(playlistJson);

            if (!playlistDoc.RootElement.TryGetProperty("items", out var playlistItems) || playlistItems.GetArrayLength() == 0)
                return null;

            var album = ParsePlaylistAsAlbum(playlistItems[0], externalId);

            // Fetch tracks (up to 200 — 4 pages at 50 each)
            album.Songs = await GetPlaylistTracksInternalAsync(externalId, maxItems: 200);
            album.SongCount = album.Songs.Count;

            return album;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "youtube") return null;
        if (string.IsNullOrWhiteSpace(_apiKey)) return null;

        try
        {
            var url = $"{BaseUrl}/channels?part=snippet,statistics&id={Uri.EscapeDataString(externalId)}&key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            return ParseChannelFull(items[0], externalId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "youtube") return new List<Album>();
        if (string.IsNullOrWhiteSpace(_apiKey)) return new List<Album>();

        try
        {
            // Returns playlists for a given channel
            var url = $"{BaseUrl}/playlists?part=snippet,contentDetails&channelId={Uri.EscapeDataString(externalId)}&maxResults=50&key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<Album>();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
                return new List<Album>();

            var albums = new List<Album>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var playlistId = idEl.GetString();
                if (string.IsNullOrEmpty(playlistId)) continue;

                albums.Add(ParsePlaylistAsAlbum(item, playlistId));
            }

            return albums;
        }
        catch
        {
            return new List<Album>();
        }
    }

    // ─────────────────────────── Playlists ───────────────────────────

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return new List<ExternalPlaylist>();

            var url = $"{BaseUrl}/search?part=snippet&type=playlist&q={Uri.EscapeDataString(query)}&maxResults={limit}&key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<ExternalPlaylist>();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items))
                return new List<ExternalPlaylist>();

            var playlists = new List<ExternalPlaylist>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) ||
                    !idEl.TryGetProperty("playlistId", out var playlistIdEl))
                    continue;

                var playlistId = playlistIdEl.GetString();
                if (string.IsNullOrEmpty(playlistId)) continue;

                playlists.Add(ParseSearchResultPlaylist(item, playlistId));
            }

            return playlists;
        }
        catch
        {
            return new List<ExternalPlaylist>();
        }
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "youtube") return null;
        if (string.IsNullOrWhiteSpace(_apiKey)) return null;

        try
        {
            var url = $"{BaseUrl}/playlists?part=snippet,contentDetails&id={Uri.EscapeDataString(externalId)}&key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            return ParsePlaylistFull(items[0], externalId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "youtube") return new List<Song>();
        if (string.IsNullOrWhiteSpace(_apiKey)) return new List<Song>();

        try
        {
            return await GetPlaylistTracksInternalAsync(externalId, maxItems: 200);
        }
        catch
        {
            return new List<Song>();
        }
    }

    // ─────────────────────────── Internal helpers ───────────────────────────

    /// <summary>
    /// Fetches playlist video items page by page and resolves full video details
    /// (including duration) via a single batched videos.list call per page.
    /// </summary>
    private async Task<List<Song>> GetPlaylistTracksInternalAsync(string playlistId, int maxItems = 200)
    {
        var songs = new List<Song>();
        string? nextPageToken = null;
        int fetched = 0;

        do
        {
            var pageTokenParam = nextPageToken != null ? $"&pageToken={Uri.EscapeDataString(nextPageToken)}" : "";
            var itemsUrl = $"{BaseUrl}/playlistItems?part=snippet&playlistId={Uri.EscapeDataString(playlistId)}&maxResults=50{pageTokenParam}&key={_apiKey}";

            var itemsResponse = await _httpClient.GetAsync(itemsUrl);
            if (!itemsResponse.IsSuccessStatusCode) break;

            var itemsJson = await itemsResponse.Content.ReadAsStringAsync();
            var itemsDoc = JsonDocument.Parse(itemsJson);

            if (!itemsDoc.RootElement.TryGetProperty("items", out var pageItems))
                break;

            // Collect video IDs from this page (skip deleted/private videos that have no videoId)
            var videoIds = new List<string>();
            foreach (var item in pageItems.EnumerateArray())
            {
                if (item.TryGetProperty("snippet", out var snippet) &&
                    snippet.TryGetProperty("resourceId", out var resourceId) &&
                    resourceId.TryGetProperty("videoId", out var videoIdEl))
                {
                    var videoId = videoIdEl.GetString();
                    if (!string.IsNullOrEmpty(videoId))
                        videoIds.Add(videoId);
                }
            }

            if (videoIds.Count > 0)
            {
                // Batch details lookup for this page of videos
                var videoSongs = await FetchVideoDetailsAsync(videoIds);
                songs.AddRange(videoSongs);
            }

            fetched += videoIds.Count;

            nextPageToken = itemsDoc.RootElement.TryGetProperty("nextPageToken", out var npt)
                ? npt.GetString()
                : null;

        } while (nextPageToken != null && fetched < maxItems);

        return songs;
    }

    /// <summary>
    /// Fetches full video details (snippet + contentDetails) for the given video IDs in one API call.
    /// </summary>
    private async Task<List<Song>> FetchVideoDetailsAsync(IEnumerable<string> videoIds)
    {
        var ids = string.Join(",", videoIds.Select(Uri.EscapeDataString));
        var url = $"{BaseUrl}/videos?part=snippet,contentDetails&id={ids}&key={_apiKey}";

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new List<Song>();

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out var items))
            return new List<Song>();

        var songs = new List<Song>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl)) continue;
            var videoId = idEl.GetString();
            if (string.IsNullOrEmpty(videoId)) continue;

            songs.Add(ParseVideoFull(item, videoId));
        }

        return songs;
    }

    /// <summary>
    /// Enriches already-parsed search result songs with durations from a batch videos.list call.
    /// </summary>
    private async Task EnrichWithDurationsAsync(List<Song> songs, List<string> videoIds)
    {
        try
        {
            var ids = string.Join(",", videoIds.Select(Uri.EscapeDataString));
            var url = $"{BaseUrl}/videos?part=contentDetails&id={ids}&key={_apiKey}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items)) return;

            var durationMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var vid = idEl.GetString();
                if (string.IsNullOrEmpty(vid)) continue;

                if (item.TryGetProperty("contentDetails", out var cd) &&
                    cd.TryGetProperty("duration", out var durEl))
                {
                    durationMap[vid] = ParseIsoDuration(durEl.GetString());
                }
            }

            foreach (var song in songs)
            {
                if (song.ExternalId != null && durationMap.TryGetValue(song.ExternalId, out var dur))
                    song.Duration = dur > 0 ? dur : null;
            }
        }
        catch
        {
            // Non-fatal — duration remains null
        }
    }

    // ─────────────────────────── Parsers ───────────────────────────

    /// <summary>Parses a video item returned by search.list into a Song (no duration).</summary>
    private static Song ParseSearchResultVideo(JsonElement item, string videoId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");
        var channelTitle = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() ?? "" : "");
        var channelId = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelId", out var ci)
            ? ci.GetString() : null;

        var (thumbMedium, thumbHigh) = GetThumbnails(snippet);
        var publishedAt = GetPublishedYear(snippet);

        return new Song
        {
            Id = $"ext-youtube-song-{videoId}",
            Title = title,
            Artist = channelTitle,
            Artists = !string.IsNullOrEmpty(channelTitle) ? new List<string> { channelTitle } : new List<string>(),
            ArtistId = !string.IsNullOrEmpty(channelId) ? $"ext-youtube-artist-{channelId}" : null,
            Album = "",
            Year = publishedAt,
            CoverArtUrl = thumbMedium,
            CoverArtUrlLarge = thumbHigh,
            IsLocal = false,
            ExternalProvider = "youtube",
            ExternalId = videoId
        };
    }

    /// <summary>Parses a full video item returned by videos.list (includes duration).</summary>
    private static Song ParseVideoFull(JsonElement item, string videoId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var contentDetails = item.TryGetProperty("contentDetails", out var cd) ? cd : default;

        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");
        var channelTitle = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() ?? "" : "");
        var channelId = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelId", out var ci)
            ? ci.GetString() : null;
        var description = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("description", out var desc)
            ? desc.GetString() : null;

        int? duration = null;
        if (contentDetails.ValueKind != JsonValueKind.Undefined &&
            contentDetails.TryGetProperty("duration", out var durEl))
        {
            var parsed = ParseIsoDuration(durEl.GetString());
            if (parsed > 0) duration = parsed;
        }

        var (thumbMedium, thumbHigh) = GetThumbnails(snippet);
        var publishedAt = GetPublishedYear(snippet);

        return new Song
        {
            Id = $"ext-youtube-song-{videoId}",
            Title = title,
            Artist = channelTitle,
            Artists = !string.IsNullOrEmpty(channelTitle) ? new List<string> { channelTitle } : new List<string>(),
            ArtistId = !string.IsNullOrEmpty(channelId) ? $"ext-youtube-artist-{channelId}" : null,
            Album = "",
            Duration = duration,
            Year = publishedAt,
            CoverArtUrl = thumbMedium,
            CoverArtUrlLarge = thumbHigh,
            IsLocal = false,
            ExternalProvider = "youtube",
            ExternalId = videoId
        };
    }

    /// <summary>Parses a playlist search result item into an Album (no track list).</summary>
    private static Album ParseSearchResultPlaylistAsAlbum(JsonElement item, string playlistId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");
        var channelTitle = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() ?? "" : "");
        var channelId = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelId", out var ci)
            ? ci.GetString() : null;

        var (thumbMedium, _) = GetThumbnails(snippet);

        return new Album
        {
            Id = $"ext-youtube-album-{playlistId}",
            Title = title,
            Artist = channelTitle,
            ArtistId = !string.IsNullOrEmpty(channelId) ? $"ext-youtube-artist-{channelId}" : null,
            CoverArtUrl = thumbMedium,
            IsLocal = false,
            ExternalProvider = "youtube",
            ExternalId = playlistId
        };
    }

    /// <summary>Parses a full playlists.list item into an Album (no track list).</summary>
    private static Album ParsePlaylistAsAlbum(JsonElement item, string playlistId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var contentDetails = item.TryGetProperty("contentDetails", out var cd) ? cd : default;

        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");
        var channelTitle = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() ?? "" : "");
        var channelId = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelId", out var ci)
            ? ci.GetString() : null;

        int? itemCount = contentDetails.ValueKind != JsonValueKind.Undefined &&
                         contentDetails.TryGetProperty("itemCount", out var ic)
            ? ic.GetInt32()
            : null;

        var (thumbMedium, thumbHigh) = GetThumbnails(snippet);
        var publishedAt = GetPublishedYear(snippet);

        return new Album
        {
            Id = $"ext-youtube-album-{playlistId}",
            Title = title,
            Artist = channelTitle,
            ArtistId = !string.IsNullOrEmpty(channelId) ? $"ext-youtube-artist-{channelId}" : null,
            Year = publishedAt,
            SongCount = itemCount,
            CoverArtUrl = thumbMedium,
            CoverArtUrlLarge = thumbHigh,
            IsLocal = false,
            ExternalProvider = "youtube",
            ExternalId = playlistId
        };
    }

    /// <summary>Parses a channel search result item into an Artist.</summary>
    private static Artist ParseSearchResultChannel(JsonElement item, string channelId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() ?? ""
            : (snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
                ? t.GetString() ?? "" : ""));

        var (thumbMedium, _) = GetThumbnails(snippet);

        return new Artist
        {
            Id = $"ext-youtube-artist-{channelId}",
            Name = title,
            ImageUrl = thumbMedium,
            IsLocal = false,
            ExternalProvider = "youtube",
            ExternalId = channelId
        };
    }

    /// <summary>Parses a full channels.list item into an Artist.</summary>
    private static Artist ParseChannelFull(JsonElement item, string channelId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var statistics = item.TryGetProperty("statistics", out var stats) ? stats : default;

        var name = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");

        var (thumbMedium, _) = GetThumbnails(snippet);

        return new Artist
        {
            Id = $"ext-youtube-artist-{channelId}",
            Name = name,
            ImageUrl = thumbMedium,
            IsLocal = false,
            ExternalProvider = "youtube",
            ExternalId = channelId
        };
    }

    /// <summary>Parses a full playlists.list item into an ExternalPlaylist.</summary>
    private static ExternalPlaylist ParsePlaylistFull(JsonElement item, string playlistId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;
        var contentDetails = item.TryGetProperty("contentDetails", out var cd) ? cd : default;

        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");
        var description = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("description", out var desc)
            ? desc.GetString() : null;
        var channelTitle = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() : null);

        int itemCount = contentDetails.ValueKind != JsonValueKind.Undefined &&
                        contentDetails.TryGetProperty("itemCount", out var ic)
            ? ic.GetInt32() : 0;

        DateTime? publishedAt = null;
        if (snippet.ValueKind != JsonValueKind.Undefined &&
            snippet.TryGetProperty("publishedAt", out var pubEl) &&
            DateTime.TryParse(pubEl.GetString(), out var parsedDate))
        {
            publishedAt = parsedDate;
        }

        var (thumbMedium, _) = GetThumbnails(snippet);

        return new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId("youtube", playlistId),
            Name = title,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            CuratorName = channelTitle,
            Provider = "youtube",
            ExternalId = playlistId,
            TrackCount = itemCount,
            Duration = 0, // YouTube API doesn't expose total playlist duration
            CoverUrl = thumbMedium,
            CreatedDate = publishedAt
        };
    }

    /// <summary>Parses a playlist search result item into an ExternalPlaylist.</summary>
    private static ExternalPlaylist ParseSearchResultPlaylist(JsonElement item, string playlistId)
    {
        var snippet = item.TryGetProperty("snippet", out var s) ? s : default;

        var title = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("title", out var t)
            ? t.GetString() ?? "" : "");
        var channelTitle = Decode(snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("channelTitle", out var ct)
            ? ct.GetString() : null);
        var description = snippet.ValueKind != JsonValueKind.Undefined && snippet.TryGetProperty("description", out var desc)
            ? desc.GetString() : null;

        var (thumbMedium, _) = GetThumbnails(snippet);

        return new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId("youtube", playlistId),
            Name = title,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            CuratorName = channelTitle,
            Provider = "youtube",
            ExternalId = playlistId,
            TrackCount = 0,
            Duration = 0,
            CoverUrl = thumbMedium
        };
    }

    // ─────────────────────────── Static utility ───────────────────────────

    /// <summary>
    /// Decodes HTML entities returned by the YouTube Data API in snippet text fields.
    /// e.g. "&amp;" → "&", "&#39;" → "'", "&quot;" → '"'
    /// </summary>
    private static string Decode(string? value) => WebUtility.HtmlDecode(value) ?? "";

    /// <summary>
    /// Returns (medium, high) thumbnail URLs from a snippet element.
    /// Prefers standard → medium for the small size, and maxres → high for the large size.
    /// </summary>
    private static (string? medium, string? high) GetThumbnails(JsonElement snippet)
    {
        if (snippet.ValueKind == JsonValueKind.Undefined ||
            !snippet.TryGetProperty("thumbnails", out var thumbs))
            return (null, null);

        string? medium = null;
        string? high = null;

        foreach (var size in new[] { "standard", "medium", "default" })
        {
            if (thumbs.TryGetProperty(size, out var t) && t.TryGetProperty("url", out var u))
            {
                medium ??= u.GetString();
                break;
            }
        }

        foreach (var size in new[] { "maxres", "high", "standard" })
        {
            if (thumbs.TryGetProperty(size, out var t) && t.TryGetProperty("url", out var u))
            {
                high ??= u.GetString();
                break;
            }
        }

        return (medium, high);
    }

    /// <summary>Extracts the 4-digit year from a snippet's publishedAt field, or null.</summary>
    private static int? GetPublishedYear(JsonElement snippet)
    {
        if (snippet.ValueKind == JsonValueKind.Undefined) return null;
        if (!snippet.TryGetProperty("publishedAt", out var pubEl)) return null;

        var dateStr = pubEl.GetString();
        if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 &&
            int.TryParse(dateStr[..4], out var year))
        {
            return year;
        }

        return null;
    }

    /// <summary>
    /// Parses an ISO 8601 duration string (e.g., "PT1H2M3S") into total seconds.
    /// </summary>
    private static int ParseIsoDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return 0;

        var match = Regex.Match(duration, @"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?");
        if (!match.Success) return 0;

        var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
        var minutes = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var seconds = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

        return hours * 3600 + minutes * 60 + seconds;
    }
}
