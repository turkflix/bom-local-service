using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using Microsoft.Playwright;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BomLocalService.Services;

public class TimeParsingService : ITimeParsingService
{
    private readonly ILogger<TimeParsingService> _logger;
    private readonly TimeZoneInfo _timeZoneInfo;

    public TimeParsingService(ILogger<TimeParsingService> logger, IConfiguration configuration)
    {
        _logger = logger;
        var timezone = configuration.GetValue<string>("Timezone") ?? "Australia/Brisbane";
        
        try
        {
            _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            _logger.LogInformation("Using timezone: {Timezone} ({DisplayName})", timezone, _timeZoneInfo.DisplayName);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning("Timezone {Timezone} not found, defaulting to Australia/Brisbane", timezone);
            _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
        }
    }

    /// <summary>
    /// Extracts last updated information from the weather map page
    /// </summary>
    public async Task<LastUpdatedInfo> ExtractLastUpdatedInfoAsync(IPage page)
    {
        try
        {
            // Look for the weather metadata section using the correct selector
            // HTML structure: <section data-testid="weatherMetadata"> with aria-label="Last updated"
            var metadataSection = page.Locator("section[data-testid='weatherMetadata'], section[aria-label='Last updated']").First;
            
            // Extract text from each div separately to preserve structure
            // Structure: <div>Observations: X minutes ago, 9:40 pm AEST</div><div>at Gympie...</div><div>Forecast: ...</div>
            var lastUpdatedText = await page.EvaluateAsync<string>(@"() => {
                const section = document.querySelector('section[data-testid=""weatherMetadata""]') || 
                               document.querySelector('section[aria-label=""Last updated""]');
                if (!section) return null;
                
                // Get all divs and join with newlines to preserve structure
                const divs = section.querySelectorAll('div');
                return Array.from(divs).map(div => div.textContent.trim()).filter(text => text).join(' ');
            }");
            
            if (string.IsNullOrEmpty(lastUpdatedText))
            {
                // Fallback: get all text content
                lastUpdatedText = await metadataSection.TextContentAsync();
            }

            return ParseLastUpdatedText(lastUpdatedText ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract last updated information");
            return new LastUpdatedInfo
            {
                ObservationTime = DateTime.UtcNow,
                ForecastTime = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Parses last updated text to extract observation time, forecast time, weather station, and distance
    /// </summary>
    public LastUpdatedInfo ParseLastUpdatedText(string text)
    {
        var info = new LastUpdatedInfo();
        _logger.LogInformation("Parsing last updated text: {Text}", text);

        // Try to parse "Observations: 11 minutes ago, 8:20 pm AEST at Gympie weather station, 30 km from Pomona, QLD"
        // And "Forecast: 41 minutes ago, 7:50 pm AEST"
        // Prefer parsing the actual time string over "minutes ago" for accuracy

        // Parse observation time - improved regex to better capture time and timezone
        // Stop timezone capture before "at" (e.g., "AEST at Gympie" or "AESTat Gympie" should capture just "AEST")
        // Pattern: "Observations: X minutes ago, 9:40 pm AESTat Gympie..." (note: sometimes no space between AEST and "at")
        // Capture timezone as 3-4 uppercase letters (AEST, AEDT, etc.) optionally followed by "at"
        var observationMatch = Regex.Match(text, @"Observations:\s*(?:\d+\s*minutes?\s*ago)?[,\s]+([\d:]+(?:\s*[ap]m)?)\s+([A-Z]{3,4})(?:at|\s+at|,|$)", RegexOptions.IgnoreCase);
        _logger.LogDebug("Observation regex match result: Success={Success}, Groups.Count={Count}", observationMatch.Success, observationMatch.Groups.Count);
        if (observationMatch.Success && observationMatch.Groups.Count >= 3 && observationMatch.Groups[1].Success)
        {
            var timeStr = observationMatch.Groups[1].Value.Trim();
            var timezoneStr = observationMatch.Groups[2].Success ? observationMatch.Groups[2].Value.Trim() : null;
            
            // Clean up timezone string - remove "at" if it got concatenated (e.g., "AESTat" -> "AEST")
            if (!string.IsNullOrEmpty(timezoneStr) && timezoneStr.EndsWith("at", StringComparison.OrdinalIgnoreCase) && timezoneStr.Length > 2)
            {
                timezoneStr = timezoneStr.Substring(0, timezoneStr.Length - 2);
            }
            
            _logger.LogDebug("Extracted observation time string: '{TimeStr}', timezone: '{TzStr}'", timeStr, timezoneStr ?? "null");
            
            if (TryParseTimeString(timeStr, timezoneStr, out var observationTime))
            {
                info.ObservationTime = observationTime;
                _logger.LogInformation("Successfully parsed observation time: {Time} UTC (from '{TimeStr}' {TzStr})", 
                    observationTime, timeStr, timezoneStr ?? "default timezone");
            }
            else
            {
                // Fallback to "minutes ago" if time parsing fails
                _logger.LogWarning("Failed to parse observation time from '{TimeStr}', falling back to 'minutes ago'", timeStr);
                var minutesAgoMatch = Regex.Match(text, @"Observations:\s*(\d+)\s*minutes?\s*ago", RegexOptions.IgnoreCase);
                if (minutesAgoMatch.Success && int.TryParse(minutesAgoMatch.Groups[1].Value, out var minutesAgo))
                {
                    info.ObservationTime = DateTime.UtcNow.AddMinutes(-minutesAgo);
                    _logger.LogInformation("Used 'minutes ago' fallback: {MinutesAgo} minutes ago = {Time} UTC", 
                        minutesAgo, info.ObservationTime);
                }
            }
        }

        // Parse forecast time
        var forecastMatch = Regex.Match(text, @"Forecast:\s*(?:\d+\s*minutes?\s*ago)?[,\s]*([\d:]+(?:\s*[ap]m)?)\s+([A-Z]+)(?:\s+at|$)", RegexOptions.IgnoreCase);
        if (forecastMatch.Success && forecastMatch.Groups[1].Success)
        {
            var timeStr = forecastMatch.Groups[1].Value.Trim();
            var timezoneStr = forecastMatch.Groups[2].Success ? forecastMatch.Groups[2].Value.Trim() : null;
            
            if (TryParseTimeString(timeStr, timezoneStr, out var forecastTime))
            {
                info.ForecastTime = forecastTime;
            }
            else
            {
                // Fallback to "minutes ago" if time parsing fails
                var minutesAgoMatch = Regex.Match(text, @"Forecast:\s*(\d+)\s*minutes?\s*ago", RegexOptions.IgnoreCase);
                if (minutesAgoMatch.Success && int.TryParse(minutesAgoMatch.Groups[1].Value, out var minutesAgo))
                {
                    info.ForecastTime = DateTime.UtcNow.AddMinutes(-minutesAgo);
                }
            }
        }

        // Extract weather station name
        var stationMatch = Regex.Match(text, @"at\s+([^,]+)\s+weather\s+station", RegexOptions.IgnoreCase);
        if (stationMatch.Success)
        {
            info.WeatherStation = stationMatch.Groups[1].Value.Trim();
        }

        // Extract distance
        var distanceMatch = Regex.Match(text, @"(\d+)\s*km\s+from", RegexOptions.IgnoreCase);
        if (distanceMatch.Success)
        {
            info.Distance = $"{distanceMatch.Groups[1].Value} km";
        }

        return info;
    }

    /// <summary>
    /// Tries to parse a time string with optional timezone to UTC
    /// </summary>
    private bool TryParseTimeString(string timeStr, string? timezoneStr, out DateTime utcTime)
    {
        utcTime = DateTime.UtcNow;
        
        try
        {
            // Parse time formats like "8:20 pm", "8:20pm", "20:20"
            var timeFormats = new[] { "h:mm tt", "h:mmtt", "HH:mm", "H:mm tt", "H:mmtt", "H:mm" };
            DateTime localTime = default;
            
            // Try parsing with various formats
            bool parsed = false;
            foreach (var format in timeFormats)
            {
                if (DateTime.TryParseExact(timeStr, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
                {
                    localTime = parsedTime;
                    parsed = true;
                    break;
                }
            }
            
            if (!parsed)
            {
                // Fallback to general parsing
                if (!DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime))
                {
                    return false;
                }
            }
            
            // Determine the timezone - use configured timezone if not specified or if it's AEST/AEDT
            TimeZoneInfo tz = _timeZoneInfo;
            if (!string.IsNullOrEmpty(timezoneStr))
            {
                // AEST/AEDT are typically the same as Australia/Brisbane or Australia/Sydney
                // Both Brisbane and Sydney use AEST/AEDT
                if (timezoneStr.Contains("AEST", StringComparison.OrdinalIgnoreCase) || 
                    timezoneStr.Contains("AEDT", StringComparison.OrdinalIgnoreCase))
                {
                    tz = _timeZoneInfo; // Use configured timezone
                }
                else
                {
                    // Try to find the timezone, fallback to configured
                    try
                    {
                        tz = TimeZoneInfo.FindSystemTimeZoneById(timezoneStr);
                    }
                    catch
                    {
                        tz = _timeZoneInfo;
                    }
                }
            }
            
            // Get current time in the target timezone to determine the date
            var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var dateTimeInTz = nowInTz.Date.Add(localTime.TimeOfDay);
            
            // If the parsed time is in the future (e.g., it's 9pm but we parsed 8pm and it's actually yesterday),
            // or if it's more than 12 hours in the past, assume it's from yesterday
            if (dateTimeInTz > nowInTz)
            {
                // Time is in the future, so it must be from yesterday
                dateTimeInTz = dateTimeInTz.AddDays(-1);
            }
            else if ((nowInTz - dateTimeInTz).TotalHours > 12)
            {
                // More than 12 hours ago, likely from yesterday
                dateTimeInTz = dateTimeInTz.AddDays(-1);
            }
            
            // Convert to UTC
            // Note: Brisbane (Australia/Brisbane) is always UTC+10 (AEST), no daylight saving
            utcTime = TimeZoneInfo.ConvertTimeToUtc(dateTimeInTz, tz);
            
            _logger.LogInformation("Parsed time string '{TimeStr}' with timezone '{TzStr}' as {DateTimeInTz} local ({UtcTime} UTC)", 
                timeStr, timezoneStr ?? "default", dateTimeInTz, utcTime);
            
            return true;
        }
        catch
        {
            return false;
        }
    }
}

