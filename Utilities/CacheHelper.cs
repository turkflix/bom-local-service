using BomLocalService.Models;
using Microsoft.Extensions.Configuration;

namespace BomLocalService.Utilities;

/// <summary>
/// Utility methods for cache folder validation and operations.
/// </summary>
public static class CacheHelper
{
    /// <summary>
    /// Gets the configured frame count for a data type from configuration.
    /// </summary>
    public static int GetFrameCountForDataType(IConfiguration configuration, CachedDataType dataType)
    {
        var dataTypeName = dataType.ToString();
        var frameCount = configuration.GetValue<int>($"CachedDataTypes:{dataTypeName}:FrameCount", 7);
        return frameCount;
    }

    /// <summary>
    /// Checks if a cache folder has complete data for a specific data type.
    /// </summary>
    public static bool IsCacheFolderCompleteForDataType(string cacheFolderPath, CachedDataType dataType, IConfiguration configuration)
    {
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return false;
        }

        var dataTypeFolder = FilePathHelper.GetDataTypeFolderPath(cacheFolderPath, dataType);
        if (!Directory.Exists(dataTypeFolder))
        {
            return false;
        }

        var expectedFrameCount = GetFrameCountForDataType(configuration, dataType);
        for (int i = 0; i < expectedFrameCount; i++)
        {
            var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, dataType, i);
            if (!File.Exists(framePath))
            {
                return false;
            }
        }

        // Check for frames.json in data type folder
        var framesMetadataPath = FilePathHelper.GetFramesMetadataFilePath(cacheFolderPath, dataType);
        if (!File.Exists(framesMetadataPath))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a cache folder is complete (has radar data - for backward compatibility).
    /// </summary>
    public static bool IsCacheFolderComplete(string cacheFolderPath, IConfiguration configuration)
    {
        return IsCacheFolderCompleteForDataType(cacheFolderPath, CachedDataType.Radar, configuration);
    }

    /// <summary>
    /// Calculates the estimated cache update duration in seconds.
    /// This is used as a fallback when metrics-based estimation is not available yet.
    /// Calculates based on frame count and configured wait times.
    /// </summary>
    public static int GetEstimatedUpdateDurationSeconds(IConfiguration configuration, CachedDataType dataType = CachedDataType.Radar)
    {
        // Calculate based on actual wait times and frame count
        var frameCount = GetFrameCountForDataType(configuration, dataType);
        var tileRenderWaitMs = configuration.GetValue<int>("Screenshot:TileRenderWaitMs", 5000);
        var dynamicContentWaitMs = configuration.GetValue<int>("Screenshot:DynamicContentWaitMs", 2000);
        
        // Rough calculation:
        // - Initial page load and navigation: ~10-15 seconds
        // - Per frame: tileRenderWaitMs (default 5s) + overhead (~1-2s for clicking, waiting, etc.)
        // - Final processing and metadata saving: ~5 seconds
        var perFrameSeconds = (tileRenderWaitMs + 1500) / 1000.0; // Add 1.5s overhead per frame
        var baseOverheadSeconds = 15; // Initial load + final processing
        var estimatedSeconds = (int)Math.Ceiling(baseOverheadSeconds + (frameCount * perFrameSeconds));
        
        return estimatedSeconds;
    }
}

