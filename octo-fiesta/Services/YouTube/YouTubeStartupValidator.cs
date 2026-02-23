using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.YouTube;

/// <summary>
/// Validates the YouTube provider at startup:
///   1. Checks that the YouTube Data API v3 key is configured and reachable.
///   2. Verifies that yt-dlp is installed and executable.
///   3. Verifies that ffmpeg is installed (required for audio conversion).
/// </summary>
public class YouTubeStartupValidator : BaseStartupValidator
{
    private readonly YouTubeSettings _settings;

    public override string ServiceName => "YouTube";

    public YouTubeStartupValidator(IOptions<YouTubeSettings> settings, HttpClient httpClient)
        : base(httpClient)
    {
        _settings = settings.Value;
    }

    public override async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var apiKey = _settings.ApiKey;
        var ytDlpPath = string.IsNullOrWhiteSpace(_settings.YtDlpPath) ? "yt-dlp" : _settings.YtDlpPath;
        var audioFormat = _settings.AudioFormat;
        var musicOnly = _settings.MusicOnly;

        // ── API key ─────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteStatus("YouTube API Key", "NOT CONFIGURED", ConsoleColor.Red);
            WriteDetail("Set the YouTube__ApiKey environment variable");
            WriteDetail("Get a free key at https://console.developers.google.com/ (YouTube Data API v3)");
            return ValidationResult.NotConfigured("YouTube API key not configured");
        }

        WriteStatus("YouTube API Key", MaskSecret(apiKey), ConsoleColor.Cyan);
        WriteStatus("YouTube Audio Format", string.IsNullOrWhiteSpace(audioFormat) ? "mp3" : audioFormat, ConsoleColor.Cyan);
        WriteStatus("YouTube Music-Only Filter", musicOnly ? "enabled (topicId=/m/04rlf)" : "disabled", ConsoleColor.Cyan);

        // ── Validate API key with a lightweight channels.list call ───────────
        await ValidateApiKeyAsync(apiKey, cancellationToken);

        // ── yt-dlp ──────────────────────────────────────────────────────────
        await ValidateYtDlpAsync(ytDlpPath, cancellationToken);

        // ── ffmpeg ──────────────────────────────────────────────────────────
        await ValidateFfmpegAsync(cancellationToken);

        return ValidationResult.Success("YouTube validation completed");
    }

    // ── Private validators ────────────────────────────────────────────────

    private async Task ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        const string fieldName = "YouTube API Key";

        try
        {
            // Use the cheapest possible call: retrieve 1 result from videos.list
            // (1 quota unit). We check that the response is a valid JSON API response.
            var url = $"https://www.googleapis.com/youtube/v3/videos?part=id&chart=mostPopular&maxResults=1&key={apiKey}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Try to parse Google API error message
                string detail;
                try
                {
                    var errorDoc = JsonDocument.Parse(errorBody);
                    detail = errorDoc.RootElement
                        .GetProperty("error")
                        .GetProperty("message")
                        .GetString() ?? response.ReasonPhrase ?? "Unknown error";
                }
                catch
                {
                    detail = response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
                }

                WriteStatus(fieldName, "INVALID", ConsoleColor.Red);
                WriteDetail(detail);
                return;
            }

            WriteStatus(fieldName, "VALID", ConsoleColor.Green);
        }
        catch (TaskCanceledException)
        {
            WriteStatus(fieldName, "TIMEOUT", ConsoleColor.Yellow);
            WriteDetail("Could not reach the YouTube API within the timeout period");
        }
        catch (HttpRequestException ex)
        {
            WriteStatus(fieldName, "UNREACHABLE", ConsoleColor.Yellow);
            WriteDetail(ex.Message);
        }
        catch (Exception ex)
        {
            WriteStatus(fieldName, "ERROR", ConsoleColor.Red);
            WriteDetail(ex.Message);
        }
    }

    private async Task ValidateYtDlpAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        const string fieldName = "yt-dlp";

        try
        {
            var version = await RunToolAsync(ytDlpPath, "--version", cancellationToken);
            if (!string.IsNullOrWhiteSpace(version))
            {
                WriteStatus(fieldName, $"FOUND ({version.Trim()})", ConsoleColor.Green);
            }
            else
            {
                WriteStatus(fieldName, "NOT FOUND", ConsoleColor.Red);
                WriteDetail($"Install yt-dlp: pip install yt-dlp  (current path: \"{ytDlpPath}\")");
            }
        }
        catch (Exception ex)
        {
            WriteStatus(fieldName, "NOT FOUND", ConsoleColor.Red);
            WriteDetail($"Install yt-dlp: pip install yt-dlp  ({ex.Message})");
        }
    }

    private async Task ValidateFfmpegAsync(CancellationToken cancellationToken)
    {
        const string fieldName = "ffmpeg";

        try
        {
            var output = await RunToolAsync("ffmpeg", "-version", cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
            {
                // Extract first line (e.g. "ffmpeg version 6.1 Copyright …")
                var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                WriteStatus(fieldName, $"FOUND ({firstLine})", ConsoleColor.Green);
            }
            else
            {
                WriteStatus(fieldName, "NOT FOUND", ConsoleColor.Yellow);
                WriteDetail("ffmpeg is required for audio conversion. Install: https://ffmpeg.org/download.html");
            }
        }
        catch (Exception ex)
        {
            WriteStatus(fieldName, "NOT FOUND", ConsoleColor.Yellow);
            WriteDetail($"ffmpeg is required for audio conversion: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs an external tool with the given arguments and returns its stdout output.
    /// Returns an empty string if the tool exits with a non-zero code.
    /// </summary>
    private static async Task<string> RunToolAsync(string tool, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        var stdout = await stdoutTask;
        return process.ExitCode == 0 ? stdout : string.Empty;
    }
}
