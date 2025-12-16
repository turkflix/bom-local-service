namespace BomLocalService.Utilities;

public static class LocationHelper
{
    /// <summary>
    /// Creates a location key from suburb and state (e.g., "Pomona_QLD")
    /// </summary>
    public static string GetLocationKey(string suburb, string state)
    {
        return $"{suburb}_{state}";
    }

    /// <summary>
    /// Sanitizes a filename by removing invalid characters and replacing spaces/commas
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries))
            .Replace(" ", "_")
            .Replace(",", "");
    }

    /// <summary>
    /// Parses suburb and state from a cache filename (format: Suburb_State_YYYYMMDD_HHMMSS)
    /// Handles multi-word suburbs like "Gold Coast" which becomes "Gold_Coast_QLD_20251216_130831"
    /// Returns null if parsing fails
    /// </summary>
    public static (string suburb, string state)? ParseLocationFromFilename(string fileName)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var parts = fileNameWithoutExtension.Split('_');
        
        // Need at least: suburb, state, date, time (4 parts minimum)
        // Format: [Suburb_Parts...]_State_YYYYMMDD_HHMMSS
        if (parts.Length < 4)
        {
            return null;
        }
        
        // Last two parts are always timestamp (YYYYMMDD, HHMMSS)
        // Second-to-last part is the state
        // Everything before that is the suburb (may contain underscores from multi-word suburbs)
        var state = parts[^3]; // Third from end
        var suburbParts = parts.Take(parts.Length - 3).ToArray();
        var suburb = string.Join(" ", suburbParts); // Join with spaces (original format had spaces converted to underscores)
        
        return (suburb, state);
    }

    /// <summary>
    /// Parses timestamp from a cache filename (format: Suburb_State_YYYYMMDD_HHMMSS.png)
    /// Returns null if parsing fails
    /// </summary>
    public static DateTime? ParseTimestampFromFilename(string fileName)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var parts = fileNameWithoutExtension.Split('_');
        
        if (parts.Length >= 3)
        {
            // Last two parts should be date and time
            var dateTimeStr = $"{parts[^2]}_{parts[^1]}";
            if (DateTime.TryParseExact(dateTimeStr, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var fileTime))
            {
                return fileTime;
            }
        }
        
        return null;
    }
}

