using BomLocalService.Models;

namespace BomLocalService.Services.Interfaces;

/// <summary>
/// Service interface for managing cached radar screenshot files and metadata.
/// Handles file system operations for storing and retrieving cached BOM radar screenshots.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets the cached screenshot folder path and associated metadata for a location.
    /// Searches for cache folders matching the location pattern and loads the corresponding metadata JSON file.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="excludeFolder">Optional folder path to exclude from search (e.g., folder currently being written to)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Tuple containing the cache folder path and metadata, or (null, null) if not found</returns>
    Task<(string? cacheFolderPath, LastUpdatedInfo? metadata)> GetCachedScreenshotWithMetadataAsync(
        string suburb, 
        string state, 
        string? excludeFolder = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all cached frames for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>List of cached frames, or empty list if not found</returns>
    Task<List<RadarFrame>> GetCachedFramesAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a specific cached frame for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="frameIndex">Frame index (0-6)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>The cached frame, or null if not found</returns>
    Task<RadarFrame?> GetCachedFrameAsync(
        string suburb, 
        string state, 
        int frameIndex,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves metadata JSON file in a cache folder.
    /// Creates a metadata.json file in the specified cache folder.
    /// </summary>
    /// <param name="cacheFolderPath">Full path to the cache folder</param>
    /// <param name="metadata">Metadata to save (observation time, forecast time, weather station, distance)</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveMetadataAsync(string cacheFolderPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves frame metadata (frame index and minutes ago) to a frames.json file in the cache folder.
    /// </summary>
    /// <param name="cacheFolderPath">Full path to the cache folder</param>
    /// <param name="frames">List of frames with their metadata</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    Task SaveFramesMetadataAsync(string cacheFolderPath, List<RadarFrame> frames, CancellationToken cancellationToken = default);
    
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
    /// Deletes all cached folders (containing frames and metadata) for a location.
    /// </summary>
    /// <param name="suburb">The suburb name (e.g., "Pomona", "Brisbane")</param>
    /// <param name="state">The Australian state abbreviation (e.g., "QLD", "NSW", "VIC")</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>True if any folders were deleted, false if no cached folders existed</returns>
    Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the cache directory path where screenshots and metadata are stored.
    /// </summary>
    /// <returns>The full path to the cache directory</returns>
    string GetCacheDirectory();
}

