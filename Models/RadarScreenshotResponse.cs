namespace BomLocalService.Models;

/// <summary>
/// Response model containing the radar screenshot image path and associated metadata.
/// This is the primary response returned when requesting a radar screenshot for a location.
/// </summary>
public class RadarScreenshotResponse
{
    /// <summary>
    /// The full file system path to the cached PNG screenshot image file.
    /// This is the path on the server where the screenshot is stored.
    /// Format: "{CacheDirectory}/{Suburb}_{State}_{Timestamp}.png"
    /// Example: "/app/cache/Pomona_QLD_20251207_000906.png"
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// The UTC timestamp when the screenshot file was last written/modified on disk.
    /// This is the file system modification time, which typically matches when the screenshot was captured.
    /// Used as a fallback if metadata is not available.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// The UTC timestamp when the weather observations were taken (from BOM website).
    /// This comes from the LastUpdatedInfo metadata extracted from the BOM weather map page.
    /// BOM updates observations every 15 minutes (at :00, :15, :30, :45 past the hour).
    /// If metadata is not available, defaults to DateTime.UtcNow.
    /// </summary>
    public DateTime ObservationTime { get; set; }

    /// <summary>
    /// The UTC timestamp when the weather forecast was generated (from BOM website).
    /// This comes from the LastUpdatedInfo metadata extracted from the BOM weather map page.
    /// Forecasts are typically updated less frequently than observations.
    /// If metadata is not available, defaults to DateTime.UtcNow.
    /// </summary>
    public DateTime ForecastTime { get; set; }

    /// <summary>
    /// The name of the weather station providing the observations for this location.
    /// Extracted from the BOM website metadata (e.g., "Gympie", "Brisbane").
    /// May be null if the station name cannot be parsed or if metadata is not available.
    /// </summary>
    public string? WeatherStation { get; set; }

    /// <summary>
    /// The distance from the requested location to the weather station.
    /// Extracted from the BOM website metadata (e.g., "30 km", "15 km").
    /// Format: "{number} km".
    /// May be null if the distance cannot be parsed or if metadata is not available.
    /// </summary>
    public string? Distance { get; set; }
}

