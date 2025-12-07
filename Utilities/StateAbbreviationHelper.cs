namespace BomLocalService.Utilities;

public static class StateAbbreviationHelper
{
    private static readonly Dictionary<string, string[]> StateMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        { "qld", new[] { "queensland" } },
        { "nsw", new[] { "new south wales" } },
        { "vic", new[] { "victoria" } },
        { "sa", new[] { "south australia" } },
        { "wa", new[] { "western australia" } },
        { "tas", new[] { "tasmania" } },
        { "nt", new[] { "northern territory" } },
        { "act", new[] { "australian capital territory" } }
    };

    /// <summary>
    /// Checks if the text matches the state abbreviation or full state name
    /// </summary>
    public static bool MatchesState(string text, string stateAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(stateAbbreviation))
        {
            return true; // No state specified, match any
        }

        var textLower = text.ToLower();
        var stateLower = stateAbbreviation.ToLower().Trim();

        // Direct match
        if (textLower.Contains(stateLower))
        {
            return true;
        }

        // Check abbreviation mappings
        if (StateMappings.TryGetValue(stateLower, out var fullNames))
        {
            return fullNames.Any(fullName => textLower.Contains(fullName));
        }

        return false;
    }
}

