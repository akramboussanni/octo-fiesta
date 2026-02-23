namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the YouTube music provider.
/// Uses the YouTube Data API v3 for search/metadata and yt-dlp for downloading.
/// Requires ffmpeg on PATH for audio conversion.
/// </summary>
public class YouTubeSettings
{
    /// <summary>
    /// YouTube Data API v3 key (required for search and metadata).
    /// Obtain a free key at https://console.developers.google.com/
    /// Enable the "YouTube Data API v3" for your project.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Path to the yt-dlp executable.
    /// Defaults to "yt-dlp" (assumes it is available on the system PATH).
    /// On Docker, install with: pip install yt-dlp
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Target audio format for downloads.
    /// Accepted values: "mp3" (default), "m4a", "opus", "vorbis", "flac"
    /// Requires ffmpeg to be installed for format conversion.
    /// </summary>
    public string AudioFormat { get; set; } = "mp3";

    /// <summary>
    /// Audio quality for yt-dlp's --audio-quality flag (0 = best, 9 = worst).
    /// Only affects lossy formats (mp3, opus, vorbis). Default is "0" (best).
    /// </summary>
    public string AudioQuality { get; set; } = "0";

    /// <summary>
    /// When true (default), search results are filtered to music content only
    /// by applying the YouTube music topicId (/m/04rlf) filter to video and channel searches.
    /// Set to false to search all YouTube content.
    /// </summary>
    public bool MusicOnly { get; set; } = true;
}
