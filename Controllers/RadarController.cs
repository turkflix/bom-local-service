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
    private readonly ICacheService _cacheService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RadarController> _logger;

    public RadarController(
        IBomRadarService bomRadarService, 
        ICacheService cacheService,
        IConfiguration configuration,
        ILogger<RadarController> logger)
    {
        _bomRadarService = bomRadarService;
        _cacheService = cacheService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get radar data for a location. Returns JSON with all frames and their URLs. Triggers background update if cache is stale.
    /// </summary>
    [HttpGet("{state}/{suburb}")]
    public async Task<ActionResult<RadarResponse>> GetRadar(string state, string suburb, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(ApiErrorResponseBuilder.ValidationError(validationError));
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

            // Return cached radar data if available (no waiting)
            var result = await _bomRadarService.GetCachedRadarAsync(suburb, state, cancellationToken);
            
            if (result == null || result.Frames.Count == 0)
            {
                // Get cache status to include in 404 response
                // Use cache service directly since this is cache management, not radar-specific
                var cacheExpirationMinutesConfig = _configuration.GetValue<double?>("CacheExpirationMinutes");
                if (!cacheExpirationMinutesConfig.HasValue)
                {
                    throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");
                }
                var cacheExpirationMinutes = (int)cacheExpirationMinutesConfig.Value;
                
                var cacheManagementCheckIntervalMinutesConfig = _configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes");
                if (!cacheManagementCheckIntervalMinutesConfig.HasValue)
                {
                    throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");
                }
                var cacheManagementCheckIntervalMinutes = cacheManagementCheckIntervalMinutesConfig.Value;
                var cacheStatus = await _cacheService.GetCacheStatusAsync(
                    suburb, 
                    state, 
                    CachedDataType.Radar,
                    cacheExpirationMinutes,
                    cacheManagementCheckIntervalMinutes,
                    cancellationToken);
                
                // Determine if update was triggered (check if it's already in progress or was just triggered)
                var updateTriggered = cacheStatus.UpdateTriggered || 
                                     (cacheStatus.Message?.Contains("in progress") ?? false) ||
                                     (!cacheStatus.CacheExists && !cacheStatus.CacheIsValid);
                
                var errorResponse = ApiErrorResponseBuilder.CacheNotFound(suburb, state, cacheStatus, updateTriggered);
                
                // Enhance message based on cache status
                if (cacheStatus.UpdateFailed)
                {
                    errorResponse.Message = $"No cached data found. Previous update attempt failed: {cacheStatus.Error ?? "Unknown error"}. Retrying...";
                    errorResponse.Details!["previousUpdateFailed"] = true;
                    if (cacheStatus.Error != null)
                    {
                        errorResponse.Details!["previousError"] = cacheStatus.Error;
                    }
                    if (cacheStatus.ErrorCode != null)
                    {
                        errorResponse.Details!["previousErrorCode"] = cacheStatus.ErrorCode;
                    }
                }
                else if (cacheStatus.Message?.Contains("in progress") ?? false)
                {
                    errorResponse.Message = "No cached data found. Cache update is currently in progress. Please retry shortly.";
                }
                else if (!cacheStatus.CacheExists)
                {
                    errorResponse.Message = "No cached data found for this location (fresh start). Cache update has been triggered in background. Please retry in a few moments.";
                }
                
                return NotFound(errorResponse);
            }
            
            // Generate URLs for frames if not already set
            if (result.Frames.Any(f => string.IsNullOrEmpty(f.ImageUrl)))
            {
                foreach (var frame in result.Frames)
                {
                    frame.ImageUrl = $"/api/radar/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/frame/{frame.FrameIndex}";
                }
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached radar screenshots for suburb: {Suburb}, state: {State}", suburb, state);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting the radar screenshots", 
                ex);
            errorResponse.Details!["location"] = new { suburb, state };
            return StatusCode(500, errorResponse);
        }
    }

    /// <summary>
    /// Get a specific frame image for a location.
    /// </summary>
    [HttpGet("{state}/{suburb}/frame/{frameIndex}")]
    public async Task<ActionResult> GetFrame(string state, string suburb, int frameIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(ApiErrorResponseBuilder.ValidationError(validationError));
            }
            
            // Validate frame index
            if (frameIndex < 0 || frameIndex > 6)
            {
                return BadRequest(ApiErrorResponseBuilder.ValidationError(
                    $"Frame index must be between 0 and 6, got {frameIndex}", 
                    "frameIndex"));
            }
            
            RadarFrame? frame;
            
            // Support cacheFolder query parameter for historical data
            var cacheFolder = Request.Query["cacheFolder"].FirstOrDefault();
            if (!string.IsNullOrEmpty(cacheFolder))
            {
                frame = await _bomRadarService.GetFrameFromCacheFolderAsync(suburb, state, cacheFolder, frameIndex, cancellationToken);
            }
            else
            {
                frame = await _bomRadarService.GetCachedFrameAsync(suburb, state, frameIndex, cancellationToken);
            }
            
            if (frame == null || !System.IO.File.Exists(frame.ImagePath))
            {
                var errorResponse = ApiErrorResponseBuilder.NotFound(
                    "Frame", 
                    $"Frame {frameIndex} for {suburb}, {state}",
                    "The frame may not exist yet. Try refreshing the cache or checking if cache update is in progress.");
                errorResponse.Details!["frameIndex"] = frameIndex;
                errorResponse.Details!["location"] = new { suburb, state };
                if (!string.IsNullOrEmpty(cacheFolder))
                {
                    errorResponse.Details["cacheFolder"] = cacheFolder;
                }
                return NotFound(errorResponse);
            }
            
            var imageBytes = await System.IO.File.ReadAllBytesAsync(frame.ImagePath, cancellationToken);
            return File(imageBytes, "image/png", $"frame_{frameIndex}.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frame {FrameIndex} for suburb: {Suburb}, state: {State}", frameIndex, suburb, state);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting the frame", 
                ex);
            errorResponse.Details!["frameIndex"] = frameIndex;
            errorResponse.Details!["location"] = new { suburb, state };
            return StatusCode(500, errorResponse);
        }
    }

    /// <summary>
    /// Get metadata about the cached radar data for a location (observation time, forecast time, etc.)
    /// </summary>
    [HttpGet("{state}/{suburb}/metadata")]
    public async Task<ActionResult<LastUpdatedInfo>> GetMetadata(string state, string suburb, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(ApiErrorResponseBuilder.ValidationError(validationError));
            }

            var result = await _bomRadarService.GetLastUpdatedInfoAsync(suburb, state, cancellationToken);
            
            if (result == null)
            {
                var errorResponse = ApiErrorResponseBuilder.NotFound(
                    "Metadata", 
                    $"{suburb}, {state}",
                    "Use POST /api/cache/{suburb}/{state}/refresh to trigger cache update.");
                errorResponse.Details!["location"] = new { suburb, state };
                errorResponse.Suggestions = new Dictionary<string, object>
                {
                    { "action", "refresh_cache" },
                    { "refreshEndpoint", $"/api/cache/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/refresh" }
                };
                return NotFound(errorResponse);
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for suburb: {Suburb}, state: {State}", suburb, state);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting metadata", 
                ex);
            errorResponse.Details!["location"] = new { suburb, state };
            return StatusCode(500, errorResponse);
        }
    }

    /// <summary>
    /// Get historical radar data (frames from multiple cache folders) between specified timestamps.
    /// Radar frames can be joined across cache folders because they represent a historical time series.
    /// </summary>
    [HttpGet("{state}/{suburb}/timeseries")]
    public async Task<ActionResult<RadarTimeSeriesResponse>> GetRadarTimeSeries(
        string state, 
        string suburb, 
        [FromQuery] string? startTime = null, 
        [FromQuery] string? endTime = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidationHelper.ValidateLocation(suburb, state);
            if (validationError != null)
            {
                return BadRequest(ApiErrorResponseBuilder.ValidationError(validationError));
            }

            DateTime? start = null;
            DateTime? end = null;
            
            if (!string.IsNullOrEmpty(startTime))
            {
                if (!DateTime.TryParse(startTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedStart))
                {
                    return BadRequest(ApiErrorResponseBuilder.ValidationError(
                        "Invalid startTime format. Use ISO 8601 format (e.g., 2025-12-07T00:00:00Z)", 
                        "startTime"));
                }
                start = parsedStart.ToUniversalTime();
            }
            
            if (!string.IsNullOrEmpty(endTime))
            {
                if (!DateTime.TryParse(endTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedEnd))
                {
                    return BadRequest(ApiErrorResponseBuilder.ValidationError(
                        "Invalid endTime format. Use ISO 8601 format (e.g., 2025-12-07T12:00:00Z)", 
                        "endTime"));
                }
                end = parsedEnd.ToUniversalTime();
            }
            
            if (start.HasValue && end.HasValue && start.Value > end.Value)
            {
                return BadRequest(ApiErrorResponseBuilder.ValidationError(
                    "startTime must be before or equal to endTime",
                    "timeRange"));
            }

            // Validate time range size to prevent excessive data loading
            // Default: base limit on cache retention, but allow override via config
            var cacheRetentionHoursConfig = _configuration.GetValue<int?>("CacheRetentionHours");
            if (!cacheRetentionHoursConfig.HasValue)
            {
                throw new InvalidOperationException("CacheRetentionHours configuration is required. Set it in appsettings.json or via CACHERETENTIONHOURS environment variable.");
            }
            var cacheRetentionHours = cacheRetentionHoursConfig.Value;
            
            // TimeSeries:MaxTimeRangeHours is optional (nullable)
            var configuredMaxHours = _configuration.GetValue<int?>("TimeSeries:MaxTimeRangeHours");
            
            // Use configured value if set, otherwise use cache retention (with minimum of 24 hours)
            var maxTimeRangeHours = configuredMaxHours ?? Math.Max(cacheRetentionHours, 24);
            
            if (start.HasValue && end.HasValue)
            {
                var timeRange = end.Value - start.Value;
                if (timeRange.TotalHours > maxTimeRangeHours)
                {
                    var reason = configuredMaxHours.HasValue 
                        ? $"configured limit: {configuredMaxHours} hours"
                        : $"cache retention: {cacheRetentionHours} hours";
                    
                    return BadRequest(ApiErrorResponseBuilder.TimeRangeError(
                        $"Time range exceeds maximum allowed duration of {maxTimeRangeHours} hours (based on {reason}). Please specify a smaller range.",
                        null,
                        new { start = start.Value, end = end.Value, requestedHours = timeRange.TotalHours }));
                }
            }

            // Check if location has any cache at all (to distinguish from "no data in range")
            var cacheRange = await _bomRadarService.GetCacheRangeAsync(suburb, state, cancellationToken);
            var locationHasCache = cacheRange.TotalCacheFolders > 0;

            // If no cache exists for this location, trigger background update and return detailed 404
            if (!locationHasCache)
            {
                // Trigger background cache update (non-blocking)
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

                // Get cache status to include in 404 response
                var cacheExpirationMinutesConfig = _configuration.GetValue<double?>("CacheExpirationMinutes");
                if (!cacheExpirationMinutesConfig.HasValue)
                {
                    throw new InvalidOperationException("CacheExpirationMinutes configuration is required. Set it in appsettings.json or via CACHEEXPIRATIONMINUTES environment variable.");
                }
                var cacheExpirationMinutes = (int)cacheExpirationMinutesConfig.Value;
                
                var cacheManagementCheckIntervalMinutesConfig = _configuration.GetValue<int?>("CacheManagement:CheckIntervalMinutes");
                if (!cacheManagementCheckIntervalMinutesConfig.HasValue)
                {
                    throw new InvalidOperationException("CacheManagement:CheckIntervalMinutes configuration is required. Set it in appsettings.json or via CACHEMANAGEMENT__CHECKINTERVALMINUTES environment variable.");
                }
                var cacheManagementCheckIntervalMinutes = cacheManagementCheckIntervalMinutesConfig.Value;
                var cacheStatus = await _cacheService.GetCacheStatusAsync(
                    suburb, 
                    state, 
                    CachedDataType.Radar,
                    cacheExpirationMinutes,
                    cacheManagementCheckIntervalMinutes,
                    cancellationToken);
                
                var updateTriggered = cacheStatus.UpdateTriggered || 
                                     (cacheStatus.Message?.Contains("in progress") ?? false) ||
                                     (!cacheStatus.CacheExists && !cacheStatus.CacheIsValid);
                
                var errorResponse = ApiErrorResponseBuilder.CacheNotFound(suburb, state, cacheStatus, updateTriggered);
                errorResponse.Message = "No cached data found for this location. Cache update has been triggered in background. Please retry in a few moments.";
                errorResponse.Details!["requestedTimeRange"] = new { start, end };
                
                return NotFound(errorResponse);
            }

            // Location has cache, check if there's data in the requested time range
            var result = await _bomRadarService.GetRadarTimeSeriesAsync(suburb, state, start, end, cancellationToken);
            
            if (result.CacheFolders.Count == 0)
            {
                // Location has cache, but no data in the requested time range
                var oldestCache = cacheRange.OldestCache?.CacheTimestamp;
                var newestCache = cacheRange.NewestCache?.CacheTimestamp;
                
                var availableRange = new {
                    oldest = oldestCache,
                    newest = newestCache,
                    totalCacheFolders = cacheRange.TotalCacheFolders,
                    timeSpanMinutes = cacheRange.TimeSpanMinutes
                };
                
                var requestedRange = new {
                    start = start,
                    end = end
                };
                
                return NotFound(ApiErrorResponseBuilder.TimeRangeError(
                    "No historical data found for the specified time range.",
                    availableRange,
                    requestedRange));
            }
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting radar time series for suburb: {Suburb}, state: {State}", suburb, state);
            var errorResponse = ApiErrorResponseBuilder.InternalError(
                "An error occurred while getting radar time series", 
                ex);
            errorResponse.Details!["location"] = new { suburb, state };
            errorResponse.Details!["requestedTimeRange"] = new { startTime, endTime };
            return StatusCode(500, errorResponse);
        }
    }
}

