using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public class ScrapingService : IScrapingService
{
    private readonly ILogger<ScrapingService> _logger;
    private readonly IBrowserService _browserService;
    private readonly ITimeParsingService _timeParsingService;
    private readonly ICacheService _cacheService;
    private readonly IDebugService _debugService;
    private readonly int _dynamicContentWaitMs;
    private readonly int _tileRenderWaitMs;

    // Selector constants
    private static readonly string[] SearchButtonSelectors = new[]
    {
        "button[data-testid='searchLabel']",
        "button[aria-label='Search for a location']",
        "button.search-location__trigger-button",
        "button.bom-button:has(span.bom-button__label:has-text('Search for a location'))",
        "button:has-text('Search for a location')",
        "[data-testid='showSearchModal'] button"
    };

    private static readonly string[] RadarLinkSelectors = new[]
    {
        "a.jump-link.bom-button.bom-button--secondary:has-text('Rain radar and weather map')",
        ".cta.button a.jump-link:has-text('Rain radar and weather map')",
        "a.jump-link:has-text('Rain radar and weather map')",
        ".cta.button a:has-text('Rain radar and weather map')",
        "a.bom-button--secondary:has-text('Rain radar and weather map')",
        "a:has-text('Rain radar and weather map')",
        "a:has-text('rain radar')",
        "a[href*='radar']"
    };

    public ScrapingService(
        ILogger<ScrapingService> logger,
        IBrowserService browserService,
        ITimeParsingService timeParsingService,
        ICacheService cacheService,
        IDebugService debugService,
        IConfiguration configuration)
    {
        _logger = logger;
        _browserService = browserService;
        _timeParsingService = timeParsingService;
        _cacheService = cacheService;
        _debugService = debugService;
        _dynamicContentWaitMs = configuration.GetValue<int>("Screenshot:DynamicContentWaitMs", 2000);
        _tileRenderWaitMs = configuration.GetValue<int>("Screenshot:TileRenderWaitMs", 5000);
    }

    /// <summary>
    /// Scrapes the BOM website to get a radar screenshot for a location
    /// </summary>
    public async Task<RadarScreenshotResponse> ScrapeRadarScreenshotAsync(
        string suburb,
        string state,
        string debugFolder,
        IPage page,
        List<(string type, string text, DateTime timestamp)> consoleMessages,
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Step 1: Navigate to BOM homepage and wait for search button
            _logger.LogInformation("Navigating to BOM homepage");
            await page.GotoAsync("https://www.bom.gov.au/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });
            
            // Wait for search button to be ready instead of NetworkIdle (faster)
            var searchButtonReady = page.Locator("button[data-testid='searchLabel'], button[aria-label='Search for a location'], button.search-location__trigger-button").First;
            await searchButtonReady.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000, State = WaitForSelectorState.Visible });
            await _debugService.SaveStepDebugAsync(debugFolder, 1, "homepage_loaded", page, consoleMessages, networkRequests, cancellationToken);

            // Step 2: Click "Search for a location" button to open search UI
            _logger.LogInformation("Clicking 'Search for a location' button");
            var searchButton = await SelectorHelper.FindLocatorBySelectorsAsync(page, SearchButtonSelectors, _logger);
            
            if (searchButton == null)
            {
                var errorMsg = "Could not find 'Search for a location' button on BOM homepage.";
                await _debugService.SaveErrorDebugAsync(debugFolder, errorMsg, page, consoleMessages, networkRequests, cancellationToken);
                throw new Exception(errorMsg);
            }

            await searchButton.ClickAsync();
            
            // Wait for search input to appear
            var searchInputReady = page.Locator("#search-enter-keyword").First;
            await searchInputReady.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000, State = WaitForSelectorState.Visible });
            await _debugService.SaveStepDebugAsync(debugFolder, 2, "search_button_clicked", page, consoleMessages, networkRequests, cancellationToken);

            // Step 3: Find and fill search box with suburb name
            _logger.LogInformation("Searching for suburb: {Suburb}", suburb);
            var searchInput = page.Locator("#search-enter-keyword").First;
            await searchInput.FillAsync(suburb);
            
            // Step 4: Wait for autocomplete suggestions to appear
            _logger.LogInformation("Waiting for autocomplete suggestions");
            await page.WaitForFunctionAsync(@"() => {
                const results = Array.from(document.querySelectorAll('li.bom-linklist__item[role=""listitem""]'));
                return results.length > 0 && results.some(r => r.offsetParent !== null);
            }", new PageWaitForFunctionOptions { Timeout = 10000 });
            await _debugService.SaveStepDebugAsync(debugFolder, 3, "search_input_filled", page, consoleMessages, networkRequests, cancellationToken);

            // Step 5: Find the matching search result based on suburb and state
            _logger.LogInformation("Looking for matching search result for {Suburb}, {State}", suburb, state);
            var suburbLower = suburb.ToLower().Trim();
            var stateLower = state.ToLower().Trim();
            
            // Get the actual count from the summary element
            var summaryText = await page.Locator("#location-results-title, [data-testid='location-results-title']").First.TextContentAsync();
            int? actualCount = null;
            if (!string.IsNullOrEmpty(summaryText))
            {
                // Parse "3 of 3 location results" or similar
                // Pattern has 2 capture groups: (\d+) of (\d+)
                // Groups[0] = full match, Groups[1] = first number, Groups[2] = second number (total)
                var countMatch = System.Text.RegularExpressions.Regex.Match(summaryText, @"(\d+)\s+of\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (countMatch.Success && countMatch.Groups.Count >= 3 && countMatch.Groups[2].Success)
                {
                    if (int.TryParse(countMatch.Groups[2].Value, out var total))
                    {
                        actualCount = total;
                    }
                }
            }
            
            // Fetch all result data in one JavaScript evaluation - extract location name and state separately
            List<(string name, string desc, string fullText)> results = new();
            try
            {
                var resultData = await page.EvaluateAsync<string[][]>(@"() => {
                    // Scope to the location results list - find the ul with aria-labelledby pointing to location-results-title
                    const resultsList = document.querySelector('ul[aria-labelledby=""location-results-title""]');
                    if (!resultsList) {
                        console.log('Location results list not found');
                        return [];
                    }
                    const results = Array.from(resultsList.querySelectorAll('li.bom-linklist__item[role=""listitem""]'));
                    console.log('Found', results.length, 'location results');
                    return results.map((r, index) => {
                        // Query from the li element - elements are nested inside <a> tag
                        const nameEl = r.querySelector('[data-testid=""location-name""]');
                        const descEl = r.querySelector('.bom-linklist-item__desc');
                        const name = nameEl ? (nameEl.textContent || nameEl.innerText || '').trim() : '';
                        const desc = descEl ? (descEl.textContent || descEl.innerText || '').trim() : '';
                        const fullText = (r.textContent || r.innerText || '').trim();
                        console.log('Result', index, ':', { hasNameEl: !!nameEl, hasDescEl: !!descEl, name: name, desc: desc });
                        return [name, desc, fullText];
                    });
                }");
                
                // Convert to structured data
                results = resultData.Select(arr => (
                    name: arr.Length > 0 ? arr[0] : "",
                    desc: arr.Length > 1 ? arr[1] : "",
                    fullText: arr.Length > 2 ? arr[2] : ""
                )).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract structured result data, falling back to text content");
                // Fallback to simple text extraction
                var resultTexts = await page.EvaluateAsync<string[]>(@"() => { 
                    const results = Array.from(document.querySelectorAll('li.bom-linklist__item[role=""listitem""]')); 
                    return results.map(r => r.textContent || ''); 
                }");
                results = resultTexts.Select(text => (name: "", desc: "", fullText: text)).ToList();
            }
            
            if (actualCount.HasValue)
            {
                _logger.LogInformation("Found {Count} location results (summary: {Summary})", actualCount.Value, summaryText?.Trim());
                // Only use the first N results that match the actual count
                if (results.Count > actualCount.Value)
                {
                    results = results.Take(actualCount.Value).ToList();
                }
            }
            else
            {
                _logger.LogInformation("Found {Count} search results", results.Count);
            }
            
            int? matchingIndex = null;
            for (int i = 0; i < results.Count; i++)
            {
                var (name, desc, fullText) = results[i];
                var nameLower = name.ToLower().Trim();
                var descLower = desc.ToLower().Trim();
                var fullTextLower = fullText.ToLower();
                
                _logger.LogInformation("Checking result {Index}: Name='{Name}', Desc='{Desc}', FullText='{FullText}'", 
                    i, name, desc, fullText.Length > 100 ? fullText.Substring(0, 100) + "..." : fullText);
                
                // Check if suburb name matches (from location-name element or fallback to fullText)
                var matchesSuburb = false;
                if (!string.IsNullOrEmpty(name))
                {
                    matchesSuburb = nameLower == suburbLower || nameLower.Contains(suburbLower);
                }
                else
                {
                    // Fallback to fullText if name extraction failed
                    matchesSuburb = fullTextLower.Contains(suburbLower);
                }
                
                // Check if state matches (from description which contains "Queensland 4300" or "New South Wales 2469")
                var matchesState = false;
                if (!string.IsNullOrEmpty(desc))
                {
                    matchesState = StateAbbreviationHelper.MatchesState(descLower, stateLower);
                }
                // Always check fullText for state as fallback
                if (!matchesState)
                {
                    matchesState = StateAbbreviationHelper.MatchesState(fullTextLower, stateLower);
                }
                
                _logger.LogInformation("Result {Index}: matchesSuburb={MatchesSuburb} (suburb='{Suburb}' vs name='{Name}'), matchesState={MatchesState} (state='{State}' vs desc='{Desc}')", 
                    i, matchesSuburb, suburbLower, nameLower, matchesState, stateLower, descLower);
                
                if (matchesSuburb && matchesState)
                {
                    matchingIndex = i;
                    _logger.LogInformation("Found matching result: {Name} - {Desc}", name, desc);
                    break;
                }
            }
            
            // Click the matching result (or first if no match)
            // Scope to the location results list
            var resultsList = page.Locator("ul[aria-labelledby='location-results-title']");
            var allResults = resultsList.Locator("li.bom-linklist__item[role='listitem']");
            var resultToClick = matchingIndex.HasValue ? allResults.Nth(matchingIndex.Value) : allResults.First;
            
            if (!matchingIndex.HasValue)
            {
                _logger.LogInformation("No exact match found, using first result");
            }
            
            await resultToClick.ClickAsync();
            await _debugService.SaveStepDebugAsync(debugFolder, 4, "search_result_selected", page, consoleMessages, networkRequests, cancellationToken);

            // Wait for forecast page to load
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 15000 });
            await page.WaitForTimeoutAsync(_dynamicContentWaitMs); // Brief wait for dynamic content
            await _debugService.SaveStepDebugAsync(debugFolder, 5, "forecast_page_loaded", page, consoleMessages, networkRequests, cancellationToken);

            // Step 6: Find and click "Rain radar and weather map" link
            _logger.LogInformation("Looking for 'Rain radar and weather map' link");
            var radarLink = await SelectorHelper.FindLocatorBySelectorsAsync(page, RadarLinkSelectors, _logger);
            
            if (radarLink == null)
            {
                var errorMsg = $"Could not find 'Rain radar and weather map' link for {suburb}, {state}";
                await _debugService.SaveErrorDebugAsync(debugFolder, errorMsg, page, consoleMessages, networkRequests, cancellationToken);
                throw new Exception(errorMsg);
            }

            await radarLink.ClickAsync();
            await _debugService.SaveStepDebugAsync(debugFolder, 6, "radar_link_clicked", page, consoleMessages, networkRequests, cancellationToken);

            // Step 7: Wait for weather map page to load and map to fully render
            _logger.LogInformation("Waiting for weather map page to load");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 15000 });
            
            // Wait for the map canvas element to appear with proper dimensions
            _logger.LogInformation("Waiting for map canvas element to render");
            var mapCanvas = page.Locator(".esri-view-surface canvas").First;
            await mapCanvas.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            
            // Wait for canvas to have valid dimensions
            await page.WaitForFunctionAsync(@"() => {
                const canvas = document.querySelector('.esri-view-surface canvas');
                return canvas && canvas.width > 0 && canvas.height > 0 && canvas.offsetWidth > 0 && canvas.offsetHeight > 0;
            }", new PageWaitForFunctionOptions { Timeout = 15000 });
            
            _logger.LogInformation("Map canvas is ready - waiting for map to render");
            
            // Wait for Esri map view to be ready
            try
            {
                await page.WaitForFunctionAsync(@"() => {
                    try {
                        const elements = document.querySelectorAll('.esri-view');
                        for (let el of elements) {
                            if (el.__view && el.__view.ready) {
                                return true;
                            }
                        }
                    } catch(e) {}
                    return false;
                }", new PageWaitForFunctionOptions { Timeout = 30000 });
                _logger.LogInformation("Esri map view is ready");
            }
            catch
            {
                _logger.LogInformation("Esri view ready check timed out, continuing with fixed wait");
            }
            
            // Additional wait for tiles to render
            await page.WaitForTimeoutAsync(_tileRenderWaitMs);
            
            await _debugService.SaveStepDebugAsync(debugFolder, 7, "weather_map_ready", page, consoleMessages, networkRequests, cancellationToken);

            // Step 8: Extract last updated information
            var lastUpdatedInfo = await _timeParsingService.ExtractLastUpdatedInfoAsync(page);

            // Step 9: Find the map container and take screenshot of just the map area
            _logger.LogInformation("Taking screenshot of map area");
            var mapContainer = page.Locator(".esri-view-surface").First;
            await mapContainer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            
            // Ensure the map container is visible and has dimensions before taking screenshot
            await page.WaitForFunctionAsync(@"() => {
                const container = document.querySelector('.esri-view-surface');
                return container && container.offsetWidth > 0 && container.offsetHeight > 0;
            }", new PageWaitForFunctionOptions { Timeout = 10000 });

            // Get bounding box of map container
            var boundingBox = await mapContainer.BoundingBoxAsync();
            if (boundingBox == null)
            {
                throw new Exception("Could not determine map container bounds");
            }

            // Generate cache filename based on suburb, state and timestamp
            var locationKey = LocationHelper.GetLocationKey(suburb, state);
            var safeLocationKey = LocationHelper.SanitizeFileName(locationKey);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var cacheFileName = $"{safeLocationKey}_{timestamp}.png";
            var cacheFilePath = Path.Combine(_cacheService.GetCacheDirectory(), cacheFileName);

            // Take screenshot of the map area only
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = cacheFilePath,
                Clip = new Clip
                {
                    X = boundingBox.X,
                    Y = boundingBox.Y,
                    Width = boundingBox.Width,
                    Height = boundingBox.Height
                }
            });

            _logger.LogInformation("Screenshot saved to: {Path}", cacheFilePath);

            // Save metadata alongside the screenshot
            await _cacheService.SaveMetadataAsync(cacheFilePath, lastUpdatedInfo, cancellationToken);

            return ResponseBuilder.CreateRadarScreenshotResponse(cacheFilePath, lastUpdatedInfo);
        }
        catch (Exception ex)
        {
            // Save error debug info if debug is enabled
            await _debugService.SaveErrorDebugAsync(debugFolder, $"Exception: {ex.Message}\n\nStackTrace:\n{ex.StackTrace}", page, consoleMessages, networkRequests, cancellationToken);
            _logger.LogError(ex, "Error during radar screenshot capture");
            throw;
        }
    }
}

