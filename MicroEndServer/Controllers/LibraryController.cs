using Microsoft.AspNetCore.Mvc;
using MicroEndServer.Services;

namespace MicroEndServer.Controllers;

/// <summary>
/// API response for library header
/// </summary>
public class LibraryHeaderResponse
{
    /// <summary>
    /// Total file length in bytes
    /// </summary>
    public long TotalLength { get; set; }
    
    /// <summary>
    /// Base64 encoded header bytes
    /// </summary>
    public string HeaderBytes { get; set; } = string.Empty;
}

/// <summary>
/// API controller for library file operations
/// Provides endpoints for downloading library headers and image data
/// </summary>
[ApiController]
[Route("api")]
public class LibraryController : ControllerBase
{
    private readonly LibraryParser _parser;
    private readonly ILogger<LibraryController> _logger;

    public LibraryController(LibraryParser parser, ILogger<LibraryController> logger)
    {
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/ping - Check server availability
    /// </summary>
    /// <returns>"pong" if server is available</returns>
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok("pong");
    }

    /// <summary>
    /// GET /api/debug/path/{path}/{name} - Debug path resolution
    /// Returns detailed information about how a path is resolved
    /// </summary>
    /// <param name="path">Library file path (with / encoded as _)</param>
    /// <param name="name">Library file name</param>
    /// <returns>PathDebugInfo with resolution details</returns>
    [HttpGet("debug/path/{path}/{name}")]
    public IActionResult DebugPath(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Path and name are required");
        }

        try
        {
            var debugInfo = _parser.GetPathDebugInfo(path, name);
            _logger.LogInformation("Path debug: {Path}/{Name} -> {FullPath} (exists: {Exists})", 
                path, name, debugInfo.FullPath, debugInfo.FileExists);
            return Ok(debugInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error debugging path: {Path}/{Name}", path, name);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// GET /api/libheader/{path}/{name} - Get library file header
    /// Returns JSON containing TotalLength and Base64-encoded HeaderBytes
    /// </summary>
    /// <param name="path">Library file path (with / encoded as _)</param>
    /// <param name="name">Library file name</param>
    /// <returns>LibraryHeaderResponse or error status</returns>
    [HttpGet("libheader/{path}/{name}")]
    public IActionResult GetLibraryHeader(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Path and name are required");
        }

        try
        {
            string filePath = _parser.GetFilePath(path, name);
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Library file not found: {Path}/{Name}", path, name);
                return NotFound($"Library file not found: {path}/{name}");
            }

            var headerResult = _parser.ParseHeader(filePath);
            
            if (headerResult == null)
            {
                _logger.LogError("Failed to parse library header: {Path}/{Name}", path, name);
                return StatusCode(500, "Failed to parse library header");
            }

            // Return binary response for compatibility with client
            // Format: TotalLength (4 bytes, int) + HeaderLength (4 bytes, int) + HeaderBytes
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            writer.Write((int)headerResult.TotalLength);
            writer.Write(headerResult.HeaderBytes.Length);
            writer.Write(headerResult.HeaderBytes);
            
            return File(ms.ToArray(), "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting library header: {Path}/{Name}", path, name);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// GET /api/libimage/{path}/{name}/{index} - Get specific image data from library
    /// Returns raw binary data for the specified image index
    /// </summary>
    /// <param name="path">Library file path (with / encoded as _)</param>
    /// <param name="name">Library file name</param>
    /// <param name="index">Image index</param>
    /// <returns>Binary image data or error status</returns>
    [HttpGet("libimage/{path}/{name}/{index}")]
    public IActionResult GetLibraryImage(string path, string name, int index)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Path and name are required");
        }

        if (index < 0)
        {
            return BadRequest("Index must be non-negative");
        }

        try
        {
            string filePath = _parser.GetFilePath(path, name);
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Library file not found: {Path}/{Name}", path, name);
                return NotFound($"Library file not found: {path}/{name}");
            }

            var imageData = _parser.GetImageData(filePath, index);
            
            if (imageData == null)
            {
                _logger.LogWarning("Image not found: {Path}/{Name} index {Index}", path, name, index);
                return NotFound($"Image not found at index {index}");
            }

            // Return binary response with position and length prefix for compatibility
            // Format: Position (4 bytes, int) + Length (4 bytes, int) + ImageData
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            // Position is not needed by client, but included for compatibility
            // Client uses the position from its local index
            writer.Write(0); // Position placeholder
            writer.Write(imageData.Length);
            writer.Write(imageData);
            
            return File(ms.ToArray(), "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting library image: {Path}/{Name} index {Index}", path, name, index);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
