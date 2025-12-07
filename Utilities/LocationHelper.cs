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
    /// Parses suburb and state from a cache filename (format: Suburb_State_YYYYMMDD_HHMMSS.png)
    /// Returns null if parsing fails
    /// </summary>
    public static (string suburb, string state)? ParseLocationFromFilename(string fileName)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var parts = fileNameWithoutExtension.Split('_');
        
        if (parts.Length >= 2)
        {
            return (parts[0], parts[1]);
        }
        
        return null;
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

