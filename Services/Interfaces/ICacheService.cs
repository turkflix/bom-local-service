using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for managing cached radar screenshot files and metadata.
/// Handles file system operations for storing and retrieving cached BOM radar screenshots.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets the cached screenshot file path and associated metadata for a location.
    /// Searches for PNG files matching the location pattern and loads the corresponding metadata JSON file.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Tuple containing the screenshot file path and metadata, or (null, null) if not found</returns>
    Task<(string? screenshotPath, LastUpdatedInfo? metadata)> GetCachedScreenshotWithMetadataAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves metadata JSON file alongside a screenshot file.
    /// Creates a JSON file with the same base name as the screenshot but with .json extension.
    /// </summary>
    /// <param name="screenshotPath">Full path to the screenshot PNG file</param>
    /// <param name="metadata">Metadata to save (observation time, forecast time, weather station, distance)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveMetadataAsync(string screenshotPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if cached metadata is still valid (not expired).
    /// Cache is valid if observation time + expiration buffer (typically 15.5 minutes) is still in the future.
    /// BOM updates observations every 15 minutes, so the buffer accounts for timing variations.
    /// </summary>
    /// <param name="metadata">The metadata to validate, or null</param>
    /// <returns>True if metadata exists and cache is still valid, false otherwise</returns>
    bool IsCacheValid(LastUpdatedInfo? metadata);
    
    /// <summary>
    /// Gets the file system path to the most recent cached screenshot for a location.
    /// Returns files ordered by creation time, most recent first.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>File system path to the cached screenshot, or empty string if not found</returns>
    Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes all cached files (PNG screenshots and JSON metadata) for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>True if any files were deleted, false if no cached files existed</returns>
    Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the cache directory path where screenshots and metadata are stored.
    /// </summary>
    /// <returns>The full path to the cache directory</returns>
    string GetCacheDirectory();
}

