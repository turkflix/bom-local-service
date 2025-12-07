using BomLocalService.Models;

namespace BomLocalService.Utilities;

public static class ResponseBuilder
{
    /// <summary>
    /// Creates a RadarResponse from a cache folder path, frames, and metadata
    /// </summary>
    public static RadarResponse CreateRadarResponse(
        string cacheFolderPath, 
        List<RadarFrame> frames,
        LastUpdatedInfo? metadata = null,
        string? suburb = null,
        string? state = null,
        bool? cacheIsValid = null,
        DateTime? cacheExpiresAt = null,
        bool isUpdating = false)
    {
        var folderInfo = new DirectoryInfo(cacheFolderPath);
        var lastWriteTime = folderInfo.Exists 
            ? folderInfo.LastWriteTime 
            : DateTime.UtcNow;

        // Generate URLs for each frame if suburb and state are provided
        if (!string.IsNullOrEmpty(suburb) && !string.IsNullOrEmpty(state))
        {
            var encodedSuburb = Uri.EscapeDataString(suburb);
            var encodedState = Uri.EscapeDataString(state);
            foreach (var frame in frames)
            {
                frame.ImageUrl = $"/api/radar/{encodedSuburb}/{encodedState}/frame/{frame.FrameIndex}";
            }
        }

        var response = new RadarResponse
        {
            Frames = frames,
            LastUpdated = lastWriteTime,
            ObservationTime = metadata?.ObservationTime ?? DateTime.UtcNow,
            ForecastTime = metadata?.ForecastTime ?? DateTime.UtcNow,
            WeatherStation = metadata?.WeatherStation,
            Distance = metadata?.Distance,
            CacheIsValid = cacheIsValid ?? false,
            CacheExpiresAt = cacheExpiresAt,
            IsUpdating = isUpdating,
            NextUpdateTime = cacheExpiresAt ?? (isUpdating ? DateTime.UtcNow.AddMinutes(2) : null) // Estimate 2 min for update if in progress
        };

        return response;
    }
    
    /// <summary>
    /// Legacy method for backward compatibility - creates response with single frame
    /// </summary>
    [Obsolete("Use CreateRadarResponse with cacheFolderPath and frames")]
    public static RadarResponse CreateRadarResponse(
        string imagePath, 
        LastUpdatedInfo? metadata = null)
    {
        var lastWriteTime = File.Exists(imagePath) 
            ? File.GetLastWriteTime(imagePath) 
            : DateTime.UtcNow;

        if (metadata == null)
        {
            return new RadarResponse
            {
                Frames = new List<RadarFrame>(),
                LastUpdated = lastWriteTime,
                ObservationTime = DateTime.UtcNow,
                ForecastTime = DateTime.UtcNow
            };
        }

        return new RadarResponse
        {
            Frames = new List<RadarFrame>(),
            LastUpdated = lastWriteTime,
            ObservationTime = metadata.ObservationTime,
            ForecastTime = metadata.ForecastTime,
            WeatherStation = metadata.WeatherStation,
            Distance = metadata.Distance
        };
    }
}

