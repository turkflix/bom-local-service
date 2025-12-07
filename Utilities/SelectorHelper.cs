using Microsoft.Playwright;

namespace BomLocalService.Utilities;

public static class SelectorHelper
{
    /// <summary>
    /// Tries to find a locator by trying multiple selectors in order
    /// Returns the first visible locator found, or null if none found
    /// </summary>
    public static async Task<ILocator?> FindLocatorBySelectorsAsync(
        IPage page, 
        string[] selectors, 
        ILogger? logger = null)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                if (await locator.IsVisibleAsync())
                {
                    logger?.LogInformation("Found element with selector: {Selector}", selector);
                    return locator;
                }
            }
            catch
            {
                // Continue to next selector
                continue;
            }
        }

        return null;
    }
}

