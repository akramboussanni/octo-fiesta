using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Services;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace octo_fiesta.Controllers;

[ApiController]
[Route("api/manual")]
public class ManualDownloadController : ControllerBase
{
    private readonly IMusicMetadataService _metadataService;
    private readonly IDownloadService _downloadService;
    private readonly ILogger<ManualDownloadController> _logger;

    public ManualDownloadController(IMusicMetadataService metadataService, IDownloadService downloadService, ILogger<ManualDownloadController> logger)
    {
        _metadataService = metadataService;
        _downloadService = downloadService;
        _logger = logger;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "Query is required" });
        try
        {
            var results = await _metadataService.SearchSongsAsync(q, 30);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("download")]
    public IActionResult Download([FromBody] DownloadRequest request)
    {
        if (string.IsNullOrEmpty(request.Provider) || string.IsNullOrEmpty(request.Id))
            return BadRequest(new { error = "Provider and Id are required" });

        // Fire and forget download to keep UI responsive
        Task.Run(async () =>
        {
            try
            {
                await _downloadService.DownloadSongAsync(request.Provider, request.Id, CancellationToken.None);
                _logger.LogInformation("Successfully downloaded {Id} from {Provider}", request.Id, request.Provider);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download {Id} from {Provider}", request.Id, request.Provider);
            }
        });

        return Accepted(new { success = true, message = "Download started in background" });
    }
}

public class DownloadRequest
{
    public string? Provider { get; set; }
    public string? Id { get; set; }
}
