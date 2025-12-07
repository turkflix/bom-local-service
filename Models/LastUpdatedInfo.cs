namespace BomLocalService.Models;

/// <summary>
/// Metadata extracted from the BOM weather map page about observation and forecast times.
/// This information is scraped from the weather metadata section on the BOM website.
/// </summary>
public class LastUpdatedInfo
{
    /// <summary>
    /// The UTC timestamp when the weather observations were taken.
    /// Extracted from the BOM website metadata section (e.g., "Observations: 11 minutes ago, 8:20 pm AEST").
    /// BOM updates observations every 15 minutes (at :00, :15, :30, :45 past the hour).
    /// </summary>
    public DateTime ObservationTime { get; set; }

    /// <summary>
    /// The UTC timestamp when the weather forecast was generated.
    /// Extracted from the BOM website metadata section (e.g., "Forecast: 41 minutes ago, 7:50 pm AEST").
    /// Forecasts are typically updated less frequently than observations.
    /// </summary>
    public DateTime ForecastTime { get; set; }

    /// <summary>
    /// The name of the weather station providing the observations for this location.
    /// Extracted from text like "at Gympie weather station" in the metadata section.
    /// May be null if the station name cannot be parsed from the page.
    /// Example: "Gympie", "Brisbane", "Sydney"
    /// </summary>
    public string? WeatherStation { get; set; }

    /// <summary>
    /// The distance from the requested location to the weather station.
    /// Extracted from text like "30 km from Pomona, QLD" in the metadata section.
    /// Format: "{number} km" (e.g., "30 km", "15 km").
    /// May be null if the distance cannot be parsed from the page.
    /// </summary>
    public string? Distance { get; set; }
}

