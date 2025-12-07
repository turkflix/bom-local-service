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
    /// Gets the metadata file path for a given screenshot path
    /// </summary>
    public static string GetMetadataFilePath(string screenshotPath)
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
}

