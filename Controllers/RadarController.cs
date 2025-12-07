using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace BomLocalService.Controllers;

[ApiController]
[Route("api/radar")]
public class RadarController : ControllerBase
{
    private readonly IBomRadarService _bomRadarService;
    private readonly ILogger<RadarController> _logger;

    public RadarController(IBomRadarService bomRadarService, ILogger<RadarController> logger)
    {
        _bomRadarService = bomRadarService;
        _logger = logger;
    }

    /// <summary>
    /// Get radar screenshot for a location. Returns cached screenshot if available, or triggers background update.
    /// </summary>
    [HttpGet("{suburb}/{state}")]
    public async Task<ActionResult> GetScreenshot(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            // Check if cache needs updating and trigger if needed (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _bomRadarService.TriggerCacheUpdateAsync(suburb, state, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background cache update failed for {Suburb}, {State}", suburb, state);
                }
            });

            // Return cached screenshot if it exists (no waiting)
            var result = await _bomRadarService.GetCachedScreenshotAsync(suburb, state, cancellationToken);
            
            if (result == null)
            {
                return NotFound(new { 
                    error = "Screenshot not found in cache. Cache update has been triggered in background. Please retry in a few moments.",
                    retryAfter = 30, // seconds
                    refreshEndpoint = $"/api/radar/{suburb}/{state}/refresh"
                });
            }
            
            // Return the image file
            if (System.IO.File.Exists(result.ImagePath))
            {
                var imageBytes = await System.IO.File.ReadAllBytesAsync(result.ImagePath, cancellationToken);
                var contentType = "image/png";
                return File(imageBytes, contentType, Path.GetFileName(result.ImagePath));
            }

            return NotFound(new { error = "Screenshot file not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached radar screenshot for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting the radar screenshot", message = ex.Message });
        }
    }

    /// <summary>
    /// Get metadata about the cached radar data for a location (observation time, forecast time, etc.)
    /// </summary>
    [HttpGet("{suburb}/{state}/metadata")]
    public async Task<ActionResult<LastUpdatedInfo>> GetMetadata(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var result = await _bomRadarService.GetLastUpdatedInfoAsync(suburb, state, cancellationToken);
            
            if (result == null)
            {
                return NotFound(new { error = "No cached data found. Use POST /api/radar/{suburb}/{state}/refresh to trigger cache update." });
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while getting metadata", message = ex.Message });
        }
    }

    /// <summary>
    /// Manually trigger a cache refresh for a location. Returns status of the update operation.
    /// </summary>
    [HttpPost("{suburb}/{state}/refresh")]
    public async Task<ActionResult<CacheUpdateStatus>> RefreshCache(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var status = await _bomRadarService.TriggerCacheUpdateAsync(suburb, state, cancellationToken);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering cache update for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while triggering cache update", message = ex.Message });
        }
    }

    /// <summary>
    /// Delete cached data for a location.
    /// </summary>
    [HttpDelete("{suburb}/{state}")]
    public async Task<ActionResult> DeleteCache(string suburb, string state, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(new { error = validationError });
            }

            var deleted = await _bomRadarService.DeleteCachedLocationAsync(suburb, state, cancellationToken);
            
            if (deleted)
            {
                return Ok(new { message = $"Cache deleted for {suburb}, {state}" });
            }
            else
            {
                return NotFound(new { error = $"No cached data found for {suburb}, {state}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cached location for suburb: {Suburb}, state: {State}", suburb, state);
            return StatusCode(500, new { error = "An error occurred while deleting cached location", message = ex.Message });
        }
    }
}

