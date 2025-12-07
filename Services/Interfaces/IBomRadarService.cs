using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Main service interface for BOM radar screenshot operations.
/// Orchestrates cache management, browser automation, and web scraping to provide radar screenshots for Australian locations.
/// </summary>
public interface IBomRadarService
{
    /// <summary>
    /// Gets a cached radar screenshot for a location.
    /// Returns the screenshot response if available in cache, otherwise returns null.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Radar screenshot response with image path and metadata, or null if not cached</returns>
    Task<RadarScreenshotResponse?> GetCachedScreenshotAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a cache update for a location.
    /// If cache is missing or expired, initiates a background update and returns status information.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Status information about the cache update operation</returns>
    Task<CacheUpdateStatus> TriggerCacheUpdateAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata about the cached radar data for a location.
    /// Returns observation time, forecast time, weather station, and distance information.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Last updated information, or null if no cached data exists</returns>
    Task<LastUpdatedInfo?> GetLastUpdatedInfoAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the file system path to the cached screenshot for a location.
    /// Returns empty string if no cached screenshot exists.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>File system path to the cached screenshot, or empty string if not found</returns>
    Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cached files (screenshot and metadata) for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>True if files were deleted, false if no cached files existed</returns>
    Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default);
}

