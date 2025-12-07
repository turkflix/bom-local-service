using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public class BomRadarService : IBomRadarService, IDisposable
{
    private readonly ILogger<BomRadarService> _logger;
    private readonly ICacheService _cacheService;
    private readonly IBrowserService _browserService;
    private readonly IScrapingService _scrapingService;
    private readonly IDebugService _debugService;
    private readonly double _cacheExpirationMinutes;

    public BomRadarService(
        ILogger<BomRadarService> logger,
        ICacheService cacheService,
        IBrowserService browserService,
        IScrapingService scrapingService,
        IDebugService debugService,
        IConfiguration configuration)
    {
        _logger = logger;
        _cacheService = cacheService;
        _browserService = browserService;
        _scrapingService = scrapingService;
        _debugService = debugService;
        _cacheExpirationMinutes = configuration.GetValue<double>("CacheExpirationMinutes", 15.5);
    }

    public async Task<RadarScreenshotResponse?> GetCachedScreenshotAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var (cachedPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, cancellationToken);
        
        if (string.IsNullOrEmpty(cachedPath) || !File.Exists(cachedPath))
        {
            return null;
        }

        return ResponseBuilder.CreateRadarScreenshotResponse(cachedPath, cachedMetadata);
    }

    public async Task<CacheUpdateStatus> TriggerCacheUpdateAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var status = new CacheUpdateStatus();
        var (cachedPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, cancellationToken);
        
        status.CacheExists = !string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath);
        
        if (cachedMetadata != null)
        {
            status.CacheIsValid = _cacheService.IsCacheValid(cachedMetadata);
            status.CacheExpiresAt = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        }

        // Check if we need to update
        bool needsUpdate = !status.CacheExists || !status.CacheIsValid;
        
        if (needsUpdate)
        {
            // Trigger async update (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchAndCacheScreenshotAsync(suburb, state, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during background cache update for {Suburb}, {State}", suburb, state);
                }
            }, cancellationToken);
            
            status.UpdateTriggered = true;
            status.Message = status.CacheExists 
                ? "Cache is stale, update triggered" 
                : "No cache exists, update triggered";
            
            // Calculate next update time
            if (status.CacheExpiresAt.HasValue && status.CacheExpiresAt.Value > DateTime.UtcNow)
            {
                status.NextUpdateTime = status.CacheExpiresAt.Value;
            }
            else
            {
                status.NextUpdateTime = DateTime.UtcNow.AddMinutes(_cacheExpirationMinutes);
            }
        }
        else
        {
            status.UpdateTriggered = false;
            status.Message = "Cache is valid, no update needed";
            status.NextUpdateTime = status.CacheExpiresAt;
        }

        return status;
    }

    private async Task<RadarScreenshotResponse> FetchAndCacheScreenshotAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting radar screenshot for suburb: {Suburb}, state: {State}", suburb, state);

        // Check cache FIRST, before acquiring semaphore (cached requests shouldn't block)
        var (cachedPath, cachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, cancellationToken);
        
        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath) && cachedMetadata != null)
        {
            var isValid = _cacheService.IsCacheValid(cachedMetadata);
            if (isValid)
            {
                _logger.LogInformation("Returning valid cached screenshot for {Suburb}, {State} (no semaphore needed)", suburb, state);
                return ResponseBuilder.CreateRadarScreenshotResponse(cachedPath, cachedMetadata);
            }
            else
            {
                var nextUpdate = cachedMetadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
                var timeUntilExpiry = nextUpdate - DateTime.UtcNow;
                _logger.LogInformation("Cached screenshot exists but is stale (observation time: {ObservationTime}, expired {TimeAgo} ago), fetching new one", 
                    cachedMetadata.ObservationTime, -timeUntilExpiry);
            }
        }
        else if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
        {
            _logger.LogWarning("Cached screenshot found but no metadata file exists, fetching new one");
        }
        else
        {
            _logger.LogInformation("No cached screenshot found for {Suburb}, {State}, fetching new one", suburb, state);
        }

        // Only acquire semaphore if we need to fetch new screenshot
        var semaphore = _browserService.GetSemaphore();
        await semaphore.WaitAsync(cancellationToken);
        
        // Generate request ID for debugging
        var requestId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var debugFolder = _debugService.CreateRequestFolder(requestId);
        
        IBrowserContext? context = null;
        try
        {
            // Double-check cache after acquiring semaphore (another request might have just created it)
            var (recheckCachedPath, recheckCachedMetadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, cancellationToken);
            if (!string.IsNullOrEmpty(recheckCachedPath) && File.Exists(recheckCachedPath) && recheckCachedMetadata != null && _cacheService.IsCacheValid(recheckCachedMetadata))
            {
                _logger.LogInformation("Cache became valid while waiting for semaphore, returning cached screenshot");
                return ResponseBuilder.CreateRadarScreenshotResponse(recheckCachedPath, recheckCachedMetadata);
            }

            // Need to capture new screenshot
            context = await _browserService.CreateContextAsync();
            var (page, consoleMessages, networkRequests) = await _browserService.CreatePageWithDebugAsync(context, requestId);

            try
            {
                return await _scrapingService.ScrapeRadarScreenshotAsync(
                    suburb,
                    state,
                    debugFolder,
                    page,
                    consoleMessages,
                    networkRequests,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during radar screenshot capture (RequestId: {RequestId})", requestId);
                throw;
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting radar screenshot for suburb: {Suburb}, state: {State} (RequestId: {RequestId})", suburb, state, requestId);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<LastUpdatedInfo?> GetLastUpdatedInfoAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        // Return cached metadata if available, otherwise return null
        var (_, metadata) = await _cacheService.GetCachedScreenshotWithMetadataAsync(suburb, state, cancellationToken);
        
        if (metadata != null)
        {
            _logger.LogDebug("Returning cached last updated info for {Suburb}, {State}", suburb, state);
            return metadata;
        }

        _logger.LogDebug("No cached metadata found for {Suburb}, {State}", suburb, state);
        return null;
    }

    public Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        return _cacheService.GetCachedScreenshotPathAsync(suburb, state, cancellationToken);
    }

    public Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        return _cacheService.DeleteCachedLocationAsync(suburb, state, cancellationToken);
    }

    public void Dispose()
    {
        _browserService?.Dispose();
    }
}
