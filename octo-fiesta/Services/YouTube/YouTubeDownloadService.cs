using System.Diagnostics;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Subsonic;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.YouTube;

// yt-dlp error messages that indicate the video is unavailable in this region/account.
// These are not transient failures — retrying the same ID will always fail.
file static class YtDlpUnavailableMessages
{
    internal static readonly string[] Fragments =
    [
        "this video is not available",
        "video unavailable",
        "this video has been removed",
        "this video is private",
        "who has the link can watch",
        "age-restricted",
        "not available in your country",
        "has been restricted"
    ];

    internal static bool IsUnavailable(string errorMessage) =>
        Fragments.Any(f => errorMessage.Contains(f, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Download service for YouTube using yt-dlp.
/// Requires yt-dlp and ffmpeg to be installed on the system/PATH.
/// Downloads audio only (no video) and re-encodes to the configured format.
/// </summary>
public class YouTubeDownloadService : BaseDownloadService
{
    private readonly string _ytDlpPath;
    private readonly string _audioFormat;
    private readonly string _audioQuality;
    private readonly YouTubeMetadataService _youtubeMetadata;

    protected override string ProviderName => "youtube";

    public YouTubeDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<YouTubeSettings> youtubeSettings,
        IServiceProvider serviceProvider,
        ILogger<YouTubeDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        var yt = youtubeSettings.Value;
        _ytDlpPath = string.IsNullOrWhiteSpace(yt.YtDlpPath) ? "yt-dlp" : yt.YtDlpPath;
        _audioFormat = NormalizeAudioFormat(yt.AudioFormat);
        _audioQuality = string.IsNullOrWhiteSpace(yt.AudioQuality) ? "0" : yt.AudioQuality;
        // Cast is safe — YouTube provider always wires YouTubeMetadataService as IMusicMetadataService
        _youtubeMetadata = (YouTubeMetadataService)metadataService;
    }

    // ─────────────────────────── BaseDownloadService Implementation ───────────────────────────

    public override async Task<bool> IsAvailableAsync()
    {
        try
        {
            var version = await RunYtDlpAsync("--version", CancellationToken.None, timeoutSeconds: 10);
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "yt-dlp not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-youtube-album-";
        return albumId.StartsWith(prefix) ? albumId[prefix.Length..] : null;
    }

    protected override string? GetTargetQuality() => $"YOUTUBE_{_audioFormat.ToUpperInvariant()}";

    protected override async Task<DownloadResult> DownloadTrackAsync(
        string trackId, Song song, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Downloading YouTube track {TrackId}: {Title} - {Artist}", trackId, song.Title, song.Artist);
        return await DownloadFromVideoIdAsync(trackId, song, cancellationToken);
    }

    /// <summary>
    /// Downloads audio for <paramref name="videoId"/>.
    /// If yt-dlp reports the video as unavailable (geo-blocked, removed, private, …)
    /// it falls back to a YouTube search for "artist title" and retries the first result.
    /// </summary>
    private async Task<DownloadResult> DownloadFromVideoIdAsync(
        string videoId, Song song, CancellationToken cancellationToken, bool isFallback = false)
    {
        var videoUrl = $"https://www.youtube.com/watch?v={videoId}";
        var extension = GetExtensionForFormat(_audioFormat);

        // Use a temp directory for staging so yt-dlp's intermediate files don't land in the library
        var tempDir = Path.Combine(Path.GetTempPath(), $"octo-fiesta-yt-{videoId}");
        Directory.CreateDirectory(tempDir);

        // yt-dlp will create: {tempDir}/{videoId}.{ext}
        var tempOutputTemplate = Path.Combine(tempDir, $"{videoId}.%(ext)s");

        try
        {
            var args = BuildYtDlpArguments(videoUrl, tempOutputTemplate);
            string output;
            try
            {
                output = await RunYtDlpAsync(args, cancellationToken, timeoutSeconds: 600);
            }
            catch (InvalidOperationException ex) when (!isFallback && YtDlpUnavailableMessages.IsUnavailable(ex.Message))
            {
                // The exact video is unavailable in this region — try to find an alternative.
                Logger.LogWarning(
                    "Video {VideoId} is unavailable ({Reason}). Searching for a fallback.",
                    videoId, ex.Message.Split('\n')[0].Trim());

                var fallbackId = await FindFallbackVideoIdAsync(song, cancellationToken);
                if (fallbackId == null)
                    throw new InvalidOperationException(
                        $"Video {videoId} is unavailable and no fallback could be found for '{song.Title}' by '{song.Artist}'.", ex);

                Logger.LogInformation("Using fallback video {FallbackId} for '{Title}'", fallbackId, song.Title);

                // Clean up this temp dir before recursing
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }

                return await DownloadFromVideoIdAsync(fallbackId, song, cancellationToken, isFallback: true);
            }

            Logger.LogDebug("yt-dlp output: {Output}", output);

            // Locate the downloaded file — yt-dlp may produce a different extension if conversion failed
            var downloadedFile = FindDownloadedFile(tempDir, videoId, extension);
            if (downloadedFile == null)
                throw new FileNotFoundException($"yt-dlp completed but no output file found in {tempDir} for video {videoId}");

            var actualExtension = Path.GetExtension(downloadedFile);

            // Build the final destination path
            var artistForPath = song.AlbumArtist ?? song.Artist;
            var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
            var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, actualExtension);

            var albumFolder = Path.GetDirectoryName(outputPath)!;
            EnsureDirectoryExists(albumFolder);
            outputPath = PathHelper.ResolveUniquePath(outputPath);

            // Move from temp directory to final destination
            IOFile.Move(downloadedFile, outputPath, overwrite: false);

            // Write ID3/Vorbis metadata and embed cover art
            await WriteMetadataAsync(outputPath, song, cancellationToken);

            var quality = $"YOUTUBE_{_audioFormat.ToUpperInvariant()}";
            return new DownloadResult(outputPath, quality);
        }
        finally
        {
            // Always clean up the temp staging directory
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clean up temp directory: {TempDir}", tempDir);
            }
        }
    }

    /// <summary>
    /// Searches YouTube for the song by "artist title" and returns the first video ID found,
    /// or <c>null</c> if no results are available.
    /// </summary>
    private async Task<string?> FindFallbackVideoIdAsync(Song song, CancellationToken cancellationToken)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(song.Artist)
                ? song.Title
                : $"{song.Artist} {song.Title}";

            Logger.LogDebug("Searching YouTube for fallback: {Query}", query);
            var results = await _youtubeMetadata.SearchSongsAsync(query, limit: 5);
            return results.FirstOrDefault()?.ExternalId;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Fallback search failed for '{Title}' by '{Artist}'", song.Title, song.Artist);
            return null;
        }
    }

    // ─────────────────────────── yt-dlp helpers ───────────────────────────

    /// <summary>
    /// Builds the yt-dlp argument string for audio-only download and conversion.
    /// </summary>
    private string BuildYtDlpArguments(string videoUrl, string outputTemplate)
    {
        // -x  = extract audio (audio-only, no video stream)
        // --audio-format = output container format (mp3, m4a, opus …)
        // --audio-quality = VBR quality (0=best … 9=worst) for lossy formats
        // --no-playlist = prevent downloading an entire playlist when given a playlist URL
        // --no-warnings = suppress non-critical warnings from yt-dlp stdout
        // --no-progress = suppress the download progress bar (cleaner logs)
        // -o = output filename template
        return $"\"{videoUrl}\" -x --audio-format {_audioFormat} --audio-quality {_audioQuality} --no-playlist --no-warnings --no-progress -o \"{outputTemplate}\"";
    }

    /// <summary>
    /// Runs yt-dlp with the given arguments and returns combined stdout output.
    /// Throws <see cref="InvalidOperationException"/> if yt-dlp exits with a non-zero code.
    /// </summary>
    private async Task<string> RunYtDlpAsync(string arguments, CancellationToken cancellationToken, int timeoutSeconds = 600)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        // Read stdout and stderr concurrently to prevent buffer deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out — kill the process
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"yt-dlp timed out after {timeoutSeconds} seconds");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var errorMessage = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            throw new InvalidOperationException($"yt-dlp failed (exit code {process.ExitCode}): {errorMessage}");
        }

        return stdout;
    }

    /// <summary>
    /// Scans <paramref name="directory"/> for a file named <c>{videoId}.{ext}</c>.
    /// Tries the expected extension first, then falls back to any file starting with the video ID.
    /// </summary>
    private static string? FindDownloadedFile(string directory, string videoId, string expectedExtension)
    {
        // Preferred: exact expected path
        var preferred = Path.Combine(directory, $"{videoId}{expectedExtension}");
        if (IOFile.Exists(preferred)) return preferred;

        // Fallback: any file starting with the videoId (yt-dlp may use a different extension)
        var candidates = Directory.GetFiles(directory, $"{videoId}.*");
        return candidates.Length > 0 ? candidates[0] : null;
    }

    // ─────────────────────────── Format helpers ───────────────────────────

    /// <summary>
    /// Normalises the configured AudioFormat value.
    /// "bestaudio" and "best" are mapped to "mp3" for maximum Navidrome/TagLib compatibility.
    /// </summary>
    private static string NormalizeAudioFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return "mp3";

        return format.ToLowerInvariant() switch
        {
            "bestaudio" or "best" => "mp3",
            var f => f
        };
    }

    /// <summary>Returns the file extension (with leading dot) for the given yt-dlp audio format.</summary>
    private static string GetExtensionForFormat(string format) => format.ToLowerInvariant() switch
    {
        "mp3" => ".mp3",
        "m4a" or "aac" => ".m4a",
        "opus" => ".opus",
        "vorbis" => ".ogg",
        "flac" => ".flac",
        "wav" => ".wav",
        _ => ".mp3"
    };
}
