using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using System.Text.Json;

namespace BomLocalService.Services;

public class CacheService : ICacheService
{
    private readonly ILogger<CacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly double _cacheExpirationMinutes;

    public CacheService(ILogger<CacheService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        _cacheExpirationMinutes = configuration.GetValue<double>("CacheExpirationMinutes", 15.5);
        
        // Ensure cache directory exists
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Gets the cached screenshot path and metadata for a location
    /// </summary>
    public async Task<(string? screenshotPath, LastUpdatedInfo? metadata)> GetCachedScreenshotWithMetadataAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFilePattern(suburb, state);
        _logger.LogDebug("Looking for cached files matching pattern: {Pattern} in directory: {Directory}", pattern, _cacheDirectory);
        
        var files = Directory.GetFiles(_cacheDirectory, pattern)
            .OrderByDescending(f => 
            {
                // Try to extract timestamp from filename first
                var timestamp = LocationHelper.ParseTimestampFromFilename(f);
                if (timestamp.HasValue)
                {
                    return timestamp.Value;
                }
                // Fallback to file write time
                return File.GetLastWriteTime(f);
            })
            .ToList();

        var screenshotPath = files.FirstOrDefault();
        if (string.IsNullOrEmpty(screenshotPath) || !File.Exists(screenshotPath))
        {
            return (null, null);
        }

        var metadata = await LoadMetadataAsync(screenshotPath, cancellationToken);
        return (screenshotPath, metadata);
    }

    /// <summary>
    /// Saves metadata alongside a screenshot
    /// </summary>
    public async Task SaveMetadataAsync(string screenshotPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadataPath = FilePathHelper.GetMetadataFilePath(screenshotPath);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            // Use CancellationToken.None for file writes to avoid cancellation issues
            // The screenshot is already saved, metadata save is best-effort
            await File.WriteAllTextAsync(metadataPath, json, CancellationToken.None);
            _logger.LogDebug("Saved metadata to: {Path}", metadataPath);
        }
        catch (Exception ex)
        {
            // Log but don't fail - screenshot is already saved
            _logger.LogWarning(ex, "Failed to save metadata for screenshot: {Path}", screenshotPath);
        }
    }

    /// <summary>
    /// Loads metadata for a screenshot
    /// </summary>
    public async Task<LastUpdatedInfo?> LoadMetadataAsync(string screenshotPath, CancellationToken cancellationToken = default)
    {
        var metadataPath = FilePathHelper.GetMetadataFilePath(screenshotPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return JsonSerializer.Deserialize<LastUpdatedInfo>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load metadata from: {Path}", metadataPath);
            return null;
        }
    }

    /// <summary>
    /// Checks if cache is still valid based on observation time
    /// </summary>
    public bool IsCacheValid(LastUpdatedInfo? metadata)
    {
        if (metadata == null)
        {
            return false;
        }

        // BOM updates observations at :00, :15, :30, :45 (every 15 minutes by default)
        // Cache expiration includes buffer (e.g., 15.5 minutes = 15 min + 30 sec buffer)
        var nextUpdateTime = metadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        return DateTime.UtcNow < nextUpdateTime;
    }

    /// <summary>
    /// Gets the path to the cached screenshot for a location (simple version, ordered by creation time)
    /// </summary>
    public Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFilePattern(suburb, state);
        var files = Directory.GetFiles(_cacheDirectory, pattern)
            .OrderByDescending(f => File.GetCreationTime(f))
            .ToList();

        return Task.FromResult(files.FirstOrDefault() ?? string.Empty);
    }

    /// <summary>
    /// Deletes all cached files for a location
    /// </summary>
    public Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFilePattern(suburb, state);
        var deleted = false;

        try
        {
            // Delete all PNG files for this location
            var pngFiles = Directory.GetFiles(_cacheDirectory, pattern);
            foreach (var pngFile in pngFiles)
            {
                try
                {
                    File.Delete(pngFile);
                    _logger.LogInformation("Deleted cached screenshot: {File}", pngFile);
                    deleted = true;

                    // Also delete associated metadata JSON file
                    var metadataFile = FilePathHelper.GetMetadataFilePath(pngFile);
                    if (File.Exists(metadataFile))
                    {
                        File.Delete(metadataFile);
                        _logger.LogInformation("Deleted metadata file: {File}", metadataFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete file: {File}", pngFile);
                }
            }

            if (deleted)
            {
                _logger.LogInformation("Deleted all cached files for location: {Suburb}, {State}", suburb, state);
            }
            else
            {
                _logger.LogDebug("No cached files found to delete for location: {Suburb}, {State}", suburb, state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cached location: {Suburb}, {State}", suburb, state);
            return Task.FromResult(false);
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Gets the cache directory path
    /// </summary>
    public string GetCacheDirectory() => _cacheDirectory;
}

