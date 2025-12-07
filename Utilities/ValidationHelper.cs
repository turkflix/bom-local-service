namespace BomLocalService.Utilities;

public static class ValidationHelper
{
    /// <summary>
    /// Validates suburb and state parameters
    /// Returns error message if validation fails, null if valid
    /// </summary>
    public static string? ValidateLocation(string suburb, string state)
    {
        if (string.IsNullOrWhiteSpace(suburb))
        {
            return "Suburb is required";
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            return "State is required";
        }

        return null;
    }
}

