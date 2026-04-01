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

    // Explicitly add an endpoint at the root level /portal to absolutely bypass proxying logic.
    [HttpGet("/portal")]
    public IActionResult Portal()
    {
        var html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Octo-Fiesta Portal</title>
            <meta name="description" content="Manually search and request tracks to add directly to Navidrome via Octo-Fiesta">
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;800&display=swap');
                :root {
                    --bg-color: #0b0914;
                    --glass-bg: rgba(255, 255, 255, 0.05);
                    --glass-border: rgba(255, 255, 255, 0.1);
                    --primary: #9d4edd;
                    --primary-hover: #c77dff;
                    --text-main: #f8f9fa;
                    --text-muted: #adb5bd;
                }
                * {
                    box-sizing: border-box;
                    margin: 0;
                    padding: 0;
                    font-family: 'Outfit', sans-serif;
                }
                body {
                    background-color: var(--bg-color);
                    background-image: 
                        radial-gradient(circle at 15% 50%, rgba(157, 78, 221, 0.15) 0%, transparent 50%),
                        radial-gradient(circle at 85% 30%, rgba(36, 0, 70, 0.2) 0%, transparent 50%);
                    color: var(--text-main);
                    min-height: 100vh;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    overflow-x: hidden;
                    background-attachment: fixed;
                }
                header {
                    width: 100%;
                    padding: 4rem 1rem 2rem;
                    text-align: center;
                }
                h1 {
                    font-size: 4rem;
                    font-weight: 800;
                    background: linear-gradient(135deg, #e0aaff, #9d4edd);
                    background-clip: text;
                    -webkit-background-clip: text;
                    -webkit-text-fill-color: transparent;
                    margin-bottom: 0.5rem;
                    letter-spacing: -1px;
                }
                p.subtitle {
                    font-size: 1.15rem;
                    color: var(--text-muted);
                    max-width: 600px;
                    margin: 0 auto 2rem;
                    font-weight: 300;
                }
                .search-container {
                    width: 100%;
                    max-width: 800px;
                    padding: 0 1rem;
                    position: relative;
                    z-index: 10;
                }
                .search-box {
                    display: flex;
                    gap: 1rem;
                    background: var(--glass-bg);
                    border: 1px solid var(--glass-border);
                    padding: 0.75rem;
                    border-radius: 100px;
                    backdrop-filter: blur(16px);
                    -webkit-backdrop-filter: blur(16px);
                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
                    transition: all 0.3s ease;
                }
                .search-box:focus-within {
                    border-color: rgba(157, 78, 221, 0.5);
                    box-shadow: 0 12px 40px rgba(157, 78, 221, 0.2);
                }
                input[type="text"] {
                    flex: 1;
                    background: transparent;
                    border: none;
                    color: var(--text-main);
                    font-size: 1.2rem;
                    padding: 0.5rem 1rem;
                    outline: none;
                }
                input[type="text"]::placeholder {
                    color: rgba(255, 255, 255, 0.3);
                }
                button.search-btn {
                    background: var(--primary);
                    color: white;
                    border: none;
                    padding: 0.8rem 2.5rem;
                    border-radius: 100px;
                    font-size: 1.1rem;
                    font-weight: 600;
                    cursor: pointer;
                    transition: all 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275);
                    display: flex;
                    align-items: center;
                    gap: 0.5rem;
                }
                button.search-btn:hover {
                    background: var(--primary-hover);
                    transform: translateY(-2px);
                    box-shadow: 0 4px 15px rgba(157, 78, 221, 0.4);
                }
                .loader {
                    display: none;
                    margin: 4rem auto;
                    width: 60px;
                    height: 60px;
                    border: 3px solid rgba(157, 78, 221, 0.2);
                    border-radius: 50%;
                    border-top-color: var(--primary);
                    animation: spin 1s ease-in-out infinite;
                }
                @keyframes spin {
                    to { transform: rotate(360deg); }
                }
                .results {
                    width: 100%;
                    max-width: 1200px;
                    padding: 4rem 1.5rem;
                    display: grid;
                    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
                    gap: 2.5rem;
                }
                .song-card {
                    background: rgba(255, 255, 255, 0.03);
                    border: 1px solid var(--glass-border);
                    border-radius: 20px;
                    overflow: hidden;
                    backdrop-filter: blur(10px);
                    -webkit-backdrop-filter: blur(10px);
                    transition: all 0.4s cubic-bezier(0.175, 0.885, 0.32, 1.275);
                    display: flex;
                    flex-direction: column;
                    position: relative;
                }
                .song-card::before {
                    content: '';
                    position: absolute;
                    top: 0;
                    left: 0;
                    right: 0;
                    height: 100%;
                    background: linear-gradient(180deg, rgba(0,0,0,0) 50%, rgba(0,0,0,0.8) 100%);
                    z-index: 1;
                    pointer-events: none;
                    opacity: 0.5;
                    transition: opacity 0.3s;
                }
                .song-card:hover {
                    transform: translateY(-10px);
                    background: rgba(255, 255, 255, 0.05);
                    border-color: rgba(157, 78, 221, 0.4);
                    box-shadow: 0 20px 40px rgba(0, 0, 0, 0.5);
                }
                .song-card:hover::before {
                    opacity: 0.8;
                }
                .cover-art {
                    width: 100%;
                    aspect-ratio: 1;
                    object-fit: cover;
                    border-bottom: 1px solid rgba(255, 255, 255, 0.05);
                }
                .song-info {
                    padding: 1.5rem;
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    position: relative;
                    z-index: 2;
                }
                .song-title {
                    font-size: 1.25rem;
                    font-weight: 600;
                    margin-bottom: 0.4rem;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                .song-artist {
                    color: var(--text-muted);
                    font-size: 0.95rem;
                    margin-bottom: 1.5rem;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    font-weight: 400;
                }
                .download-btn {
                    margin-top: auto;
                    background: rgba(255, 255, 255, 0.05);
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    color: var(--text-main);
                    padding: 0.85rem;
                    border-radius: 12px;
                    font-weight: 600;
                    font-size: 1rem;
                    cursor: pointer;
                    transition: all 0.3s ease;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    gap: 0.6rem;
                    width: 100%;
                }
                .download-btn:hover:not(:disabled) {
                    background: rgba(157, 78, 221, 0.2);
                    border-color: var(--primary);
                    color: #fff;
                    transform: scale(1.02);
                }
                .download-btn:disabled {
                    opacity: 0.7;
                    cursor: default;
                }
                .success {
                    background: rgba(43, 147, 72, 0.2) !important;
                    border-color: #2b9348 !important;
                    color: #52b788 !important;
                }
                .error-msg {
                    color: #ff4d4d;
                    text-align: center;
                    margin-top: 2rem;
                    font-weight: 600;
                    font-size: 1.1rem;
                }
                @media (max-width: 600px) {
                    h1 { font-size: 2.8rem; }
                    .search-box { flex-direction: column; border-radius: 20px; }
                    button.search-btn { border-radius: 12px; justify-content: center; }
                    input[type="text"] { text-align: center; }
                }
            </style>
        </head>
        <body>
            <header>
                <h1>Octo-Fiesta</h1>
                <p class="subtitle">Search, request and automatically download tracks directly into your Navidrome library.</p>
            </header>
            <div class="search-container">
                <form class="search-box" id="searchForm">
                    <input type="text" id="searchInput" placeholder="Search for a song, artist, or album..." autocomplete="off">
                    <button type="submit" class="search-btn">
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line></svg>
                        Search
                    </button>
                </form>
            </div>
            <div class="loader" id="loader"></div>
            <div id="errorBox" class="error-msg"></div>
            <div class="results" id="resultsGrid"></div>

            <script>
                document.getElementById('searchForm').addEventListener('submit', async (e) => {
                    e.preventDefault();
                    const query = document.getElementById('searchInput').value.trim();
                    if (!query) return;

                    const loader = document.getElementById('loader');
                    const grid = document.getElementById('resultsGrid');
                    const errorBox = document.getElementById('errorBox');
                    
                    grid.innerHTML = '';
                    errorBox.innerHTML = '';
                    loader.style.display = 'block';

                    try {
                        const res = await fetch(`/api/manual/search?q=${encodeURIComponent(query)}`);
                        if (!res.ok) throw new Error('Search failed. The proxy might be offline or rate-limited.');
                        
                        const data = await res.json();
                        loader.style.display = 'none';

                        if (!data || data.length === 0) {
                            errorBox.innerHTML = 'No results found.';
                            return;
                        }

                        data.forEach(song => {
                            const card = document.createElement('div');
                            card.className = 'song-card';
                            
                            const coverUrl = song.coverArtUrlLarge || song.coverArtUrl || 'https://images.unsplash.com/photo-1614613535308-eb5fbd3d2c17?q=80&w=600&auto=format&fit=crop';

                            let localBtnHtml = `
                                <button class="download-btn" onclick="downloadSong(this, '${song.externalProvider}', '${song.externalId}')">
                                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="7 10 12 15 17 10"></polyline><line x1="12" y1="15" x2="12" y2="3"></line></svg>
                                    Request Download
                                </button>
                            `;

                            if (song.isLocal) {
                                localBtnHtml = `
                                <button class="download-btn success" disabled>
                                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>
                                    In Library
                                </button>
                                `;
                            } else if (!song.externalId || !song.externalProvider) {
                                localBtnHtml = `
                                <button class="download-btn" disabled>
                                    Unavailable
                                </button>
                                `;
                            }

                            card.innerHTML = `
                                <img src="${coverUrl}" alt="Cover for ${song.title}" class="cover-art" loading="lazy">
                                <div class="song-info">
                                    <div class="song-title" title="${song.title.replace(/"/g, '&quot;')}">${song.title}</div>
                                    <div class="song-artist" title="${song.artist.replace(/"/g, '&quot;')}">${song.artist}</div>
                                    ${localBtnHtml}
                                </div>
                            `;
                            grid.appendChild(card);
                        });

                    } catch (err) {
                        loader.style.display = 'none';
                        errorBox.innerHTML = err.message;
                    }
                });

                async function downloadSong(btn, provider, id) {
                    if (btn.disabled) return;
                    
                    const originalText = btn.innerHTML;
                    btn.innerHTML = `<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"></circle><polyline points="12 6 12 12 16 14"></polyline></svg> Sending...`;
                    btn.disabled = true;

                    try {
                        const res = await fetch('/api/manual/download', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ provider, id })
                        });

                        if (res.ok) {
                            btn.classList.add('success');
                            btn.innerHTML = `<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg> Requested!`;
                        } else {
                            throw new Error('Failed');
                        }
                    } catch (err) {
                        btn.innerHTML = 'Error';
                        setTimeout(() => {
                            btn.disabled = false;
                            btn.innerHTML = originalText;
                        }, 3000);
                    }
                }
            </script>
        </body>
        </html>
        """;
        
        return Content(html, "text/html");
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
