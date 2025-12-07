namespace BomLocalService.Utilities;

public static class FilePathHelper
{
    /// <summary>
    /// Gets the cache directory path from configuration or returns default
    /// </summary>
    public static string GetCacheDirectory(IConfiguration configuration)
    {
        return configuration.GetValue<string>("CacheDirectory") 
            ?? Path.Combine(AppContext.BaseDirectory, "cache");
    }

    /// <summary>
    /// Gets the metadata file path for a given screenshot path (legacy method for backward compatibility)
    /// </summary>
    [Obsolete("Use GetMetadataFilePath(string cacheFolderPath) for folder-based storage")]
    public static string GetMetadataFilePathFromScreenshot(string screenshotPath)
    {
        return Path.ChangeExtension(screenshotPath, ".json");
    }

    /// <summary>
    /// Gets the cache file pattern for a location key (e.g., "Pomona_QLD_*.png")
    /// </summary>
    public static string GetCacheFilePattern(string locationKey)
    {
        var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
        return $"{safeLocationKey}_*.png";
    }

    /// <summary>
    /// Gets the cache file pattern for suburb and state
    /// </summary>
    public static string GetCacheFilePattern(string suburb, string state)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        return GetCacheFilePattern(locationKey);
    }

    /// <summary>
    /// Gets the cache folder path for a location and timestamp.
    /// Format: "{CacheDirectory}/{LocationKey}_{Timestamp}"
    /// Example: "/app/cache/Pomona_QLD_20251207_000906"
    /// </summary>
    public static string GetCacheFolderPath(string cacheDirectory, string suburb, string state, string timestamp)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
        return Path.Combine(cacheDirectory, $"{safeLocationKey}_{timestamp}");
    }

    /// <summary>
    /// Gets the frame file path within a cache folder.
    /// Format: "{CacheFolderPath}/frame_{FrameIndex}.png"
    /// </summary>
    public static string GetFrameFilePath(string cacheFolderPath, int frameIndex)
    {
        return Path.Combine(cacheFolderPath, $"frame_{frameIndex}.png");
    }

    /// <summary>
    /// Gets the metadata file path within a cache folder.
    /// Format: "{CacheFolderPath}/metadata.json"
    /// </summary>
    public static string GetMetadataFilePath(string cacheFolderPath)
    {
        return Path.Combine(cacheFolderPath, "metadata.json");
    }

    /// <summary>
    /// Gets the cache folder pattern for finding existing folders.
    /// Format: "{LocationKey}_*"
    /// </summary>
    public static string GetCacheFolderPattern(string suburb, string state)
    {
        var locationKey = LocationHelper.GetLocationKey(suburb, state);
        var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
        return $"{safeLocationKey}_*";
    }

    /// <summary>
    /// Gets the lock file path for a cache folder.
    /// This file indicates the folder is currently being written to.
    /// Format: "{CacheFolderPath}/.writing"
    /// </summary>
    public static string GetCacheLockFilePath(string cacheFolderPath)
    {
        return Path.Combine(cacheFolderPath, ".writing");
    }
}

