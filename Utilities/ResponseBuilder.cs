using BomLocalService.Models;

namespace BomLocalService.Utilities;

public static class ResponseBuilder
{
    /// <summary>
    /// Creates a RadarScreenshotResponse from a file path and metadata
    /// </summary>
    public static RadarScreenshotResponse CreateRadarScreenshotResponse(
        string imagePath, 
        LastUpdatedInfo? metadata = null)
    {
        var lastWriteTime = File.Exists(imagePath) 
            ? File.GetLastWriteTime(imagePath) 
            : DateTime.UtcNow;

        if (metadata == null)
        {
            return new RadarScreenshotResponse
            {
                ImagePath = imagePath,
                LastUpdated = lastWriteTime,
                ObservationTime = DateTime.UtcNow,
                ForecastTime = DateTime.UtcNow
            };
        }

        return new RadarScreenshotResponse
        {
            ImagePath = imagePath,
            LastUpdated = lastWriteTime,
            ObservationTime = metadata.ObservationTime,
            ForecastTime = metadata.ForecastTime,
            WeatherStation = metadata.WeatherStation,
            Distance = metadata.Distance
        };
    }
}

