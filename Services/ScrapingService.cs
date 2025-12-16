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
    private readonly IConfiguration _configuration;
    private readonly int _dynamicContentWaitMs;
    private readonly int _tileRenderWaitMs;
    private readonly ScreenshotCropConfig _cropConfig;
    private readonly double _cacheExpirationMinutes;
    private readonly int _cacheManagementCheckIntervalMinutes;

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
        _configuration = configuration;
        _dynamicContentWaitMs = configuration.GetValue<int>("Screenshot:DynamicContentWaitMs", 2000);
        _tileRenderWaitMs = configuration.GetValue<int>("Screenshot:TileRenderWaitMs", 5000);
        
        // Load crop configuration
        var cropSection = configuration.GetSection("Screenshot:Crop");
        _cropConfig = new ScreenshotCropConfig
        {
            X = cropSection.GetValue<int>("X", 0),
            Y = cropSection.GetValue<int>("Y", 0),
            RightOffset = cropSection.GetValue<int>("RightOffset", 0),
            Height = cropSection.GetValue<int?>("Height")
        };
        
        _logger.LogInformation("Screenshot crop config: X={X}, Y={Y}, RightOffset={RightOffset}, Height={Height}",
            _cropConfig.X, _cropConfig.Y, _cropConfig.RightOffset, _cropConfig.Height);
        
        _cacheExpirationMinutes = configuration.GetValue<double>("CacheExpirationMinutes", 12.5);
        _cacheManagementCheckIntervalMinutes = configuration.GetValue<int>("CacheManagement:CheckIntervalMinutes", 5);
        
        if (_cacheExpirationMinutes <= 0)
        {
            throw new ArgumentException("CacheExpirationMinutes must be greater than 0", nameof(configuration));
        }
        if (_cacheManagementCheckIntervalMinutes <= 0 || _cacheManagementCheckIntervalMinutes > 60)
        {
            throw new ArgumentException("CacheManagement:CheckIntervalMinutes must be between 1 and 60", nameof(configuration));
        }
    }

    /// <summary>
    /// Scrapes the BOM website to get a radar screenshot for a location
    /// </summary>
    public async Task<RadarResponse> ScrapeRadarScreenshotAsync(
        string suburb,
        string state,
        string cacheFolderPath,
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
            int bestMatchScore = -1;

            for (int i = 0; i < results.Count; i++)
            {
                var (name, desc, fullText) = results[i];
                var nameLower = name.ToLower().Trim();
                var descLower = desc.ToLower().Trim();
                var fullTextLower = fullText.ToLower();
                
                _logger.LogInformation("Checking result {Index}: Name='{Name}', Desc='{Desc}', FullText='{FullText}'", 
                    i, name, desc, fullText.Length > 100 ? fullText.Substring(0, 100) + "..." : fullText);
                
                // Check if suburb name matches with priority scoring
                var matchesSuburb = false;
                var matchScore = 0;
                
                if (!string.IsNullOrEmpty(name))
                {
                    if (nameLower == suburbLower)
                    {
                        // Exact match - highest priority
                        matchesSuburb = true;
                        matchScore = 100;
                    }
                    else if (nameLower.StartsWith(suburbLower + " ") || nameLower.StartsWith(suburbLower + "("))
                    {
                        // Starts with suburb name (e.g., "Sydney" or "Sydney (Inner)")
                        matchesSuburb = true;
                        matchScore = 80;
                    }
                    else if (nameLower.Contains("(" + suburbLower + ")") || nameLower.Contains("(" + suburbLower + " "))
                    {
                        // Suburb name in parentheses (e.g., "Darlington (Sydney)") - lower priority
                        matchesSuburb = true;
                        matchScore = 60;
                    }
                    else if (nameLower.Contains(suburbLower))
                    {
                        // Contains suburb name anywhere - lowest priority
                        matchesSuburb = true;
                        matchScore = 40;
                    }
                }
                else
                {
                    // Fallback to fullText if name extraction failed
                    if (fullTextLower.Contains(suburbLower))
                    {
                        matchesSuburb = true;
                        matchScore = 20;
                    }
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
                
                _logger.LogInformation("Result {Index}: matchesSuburb={MatchesSuburb} (score={Score}, suburb='{Suburb}' vs name='{Name}'), matchesState={MatchesState} (state='{State}' vs desc='{Desc}')", 
                    i, matchesSuburb, matchScore, suburbLower, nameLower, matchesState, stateLower, descLower);
                
                // Track the best matching result (highest score)
                if (matchesSuburb && matchesState && matchScore > bestMatchScore)
                {
                    matchingIndex = i;
                    bestMatchScore = matchScore;
                    _logger.LogInformation("New best match found: {Name} - {Desc} (score: {Score})", name, desc, matchScore);
                }
            }
            
            // Use the best match found (or first if no match)
            if (matchingIndex.HasValue)
            {
                _logger.LogInformation("Using best matching result at index {Index} with score {Score}", matchingIndex.Value, bestMatchScore);
            }
            else
            {
                _logger.LogInformation("No exact match found, using first result");
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

            // Step 8: Ensure radar is paused before capturing frames
            _logger.LogInformation("Checking if radar loop is paused");
            var playPauseButton = page.Locator("button[data-testid='bom-time-scrub-play-pause']").First;
            await playPauseButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

            // Check if button shows "Play" (paused) or "Pause" (playing)
            var buttonLabel = await playPauseButton.Locator(".bom-scrub-action__label").TextContentAsync();
            if (buttonLabel?.Trim().Equals("Pause", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Radar is playing, pausing it");
                await playPauseButton.ClickAsync();
                // Wait for pause to take effect
                await page.WaitForTimeoutAsync(500);
                
                // Verify it's now paused
                buttonLabel = await playPauseButton.Locator(".bom-scrub-action__label").TextContentAsync();
                if (buttonLabel?.Trim().Equals("Play", StringComparison.OrdinalIgnoreCase) != true)
                {
                    _logger.LogWarning("Radar may not be paused after click, continuing anyway");
                }
            }
            else
            {
                _logger.LogInformation("Radar is already paused");
            }

            await _debugService.SaveStepDebugAsync(debugFolder, 8, "radar_paused", page, consoleMessages, networkRequests, cancellationToken);

            // Step 9: Click on first frame segment to ensure we start at frame 0
            _logger.LogInformation("Resetting to first frame (frame 0)");
            try
            {
                var firstFrameSegment = page.Locator("[data-testid='bom-scrub-segment'][data-id='0']").First;
                await firstFrameSegment.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await firstFrameSegment.ClickAsync();
                // Wait for frame to update
                await page.WaitForTimeoutAsync(1000);
                _logger.LogInformation("Successfully clicked frame 0 segment");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to click first frame segment, continuing anyway");
            }

            await _debugService.SaveStepDebugAsync(debugFolder, 9, "frame_0_selected", page, consoleMessages, networkRequests, cancellationToken);

            // Step 10: Verify scrubber is at position 0 before capturing first frame
            _logger.LogInformation("Verifying scrubber is at position 0");
            try
            {
                // Check if the active segment has data-id="0" (simpler and more reliable than checking thumb position)
                var activeSegment = await page.EvaluateAsync<bool>(@"() => {
                    const segments = Array.from(document.querySelectorAll('[data-testid=""bom-scrub-segment""]'));
                    const activeSegment = segments.find(s => {
                        const style = window.getComputedStyle(s);
                        return style.backgroundColor !== 'rgb(148, 148, 148)' && style.backgroundColor !== 'rgb(148, 148, 148)';
                    });
                    return activeSegment && activeSegment.getAttribute('data-id') === '0';
                }");
                
                if (activeSegment)
                {
                    _logger.LogInformation("Scrubber confirmed at position 0 (active segment is frame 0)");
                }
                else
                {
                    _logger.LogDebug("Could not confirm scrubber position via active segment, but continuing (frame 0 was clicked)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Scrubber position verification failed, continuing anyway (non-critical check)");
            }

            await _debugService.SaveStepDebugAsync(debugFolder, 10, "scrubber_at_position_0", page, consoleMessages, networkRequests, cancellationToken);

            // Step 11: Wait for frame 0 tiles to fully load before calculating bounding box
            // This ensures the map viewport is stable and prevents jiggle between frames
            _logger.LogInformation("Waiting for frame 0 tiles to fully render");
            await page.WaitForTimeoutAsync(_tileRenderWaitMs);

            // Step 12: Extract metadata and frame information
            _logger.LogInformation("Extracting metadata and frame information");
            var lastUpdatedInfo = await _timeParsingService.ExtractLastUpdatedInfoAsync(page);
            var frameInfo = await ExtractFrameInfoAsync(page);

            // Step 13: Get map container and calculate bounding box once
            // Calculate after frame 0 tiles are loaded to ensure consistent viewport
            _logger.LogInformation("Preparing map container for screenshot");
            var mapContainer = page.Locator(".esri-view-surface").First;
            await mapContainer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // Ensure the map container is visible and has dimensions
            await page.WaitForFunctionAsync(@"() => {
                const container = document.querySelector('.esri-view-surface');
                return container && container.offsetWidth > 0 && container.offsetHeight > 0;
            }", new PageWaitForFunctionOptions { Timeout = 10000 });

            var boundingBox = await mapContainer.BoundingBoxAsync();
            if (boundingBox == null || boundingBox.Width <= 0 || boundingBox.Height <= 0)
            {
                _logger.LogError("Map container has invalid bounds: {BoundingBox}", boundingBox);
                throw new Exception($"Map container has invalid bounds: {boundingBox?.Width ?? 0}x{boundingBox?.Height ?? 0}");
            }
            
            // Convert BoundingBox to Clip for crop calculation
            var containerClip = new Clip
            {
                X = boundingBox.X,
                Y = boundingBox.Y,
                Width = boundingBox.Width,
                Height = boundingBox.Height
            };

            // Step 14: Use provided cache folder (already created by BomRadarService)
            Directory.CreateDirectory(cacheFolderPath);
            _logger.LogInformation("Using cache folder: {Path}", cacheFolderPath);

            // Step 15-21: Capture all frames
            var frameCount = CacheHelper.GetFrameCountForDataType(_configuration, CachedDataType.Radar);
            
            // Track progress: map is ready, starting frame capture
            _cacheService.RecordUpdateProgressByFolder(cacheFolderPath, CacheUpdatePhase.CapturingFrames, 0, frameCount);
            
            var frames = new List<RadarFrame>();
            var stepForwardButton = page.Locator("button[data-testid='bom-scrub-utils__right__step-forward']").First;
            int? previousMinutesAgo = null;

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                _logger.LogInformation("Capturing frame {FrameIndex} of {FrameCount}", frameIndex, frameCount);
                
                // Wait for map to stabilize (tiles to load for current frame)
                await page.WaitForTimeoutAsync(_tileRenderWaitMs);
                
                // Extract actual minutes ago from the display label (e.g., "17 minutes ago" -> 17)
                var minutesAgo = await ExtractMinutesAgoFromDisplayAsync(page);
                if (minutesAgo == null)
                {
                    // Fallback to calculated value if extraction fails
                    var (_, defaultMinutesAgo) = frameInfo[frameIndex];
                    minutesAgo = defaultMinutesAgo;
                    _logger.LogWarning("Failed to extract minutes from display label for frame {FrameIndex}, using default: {MinutesAgo}", frameIndex, minutesAgo);
                }
                
                // Validate that minutesAgo is different from previous frame (unless it's frame 0)
                if (frameIndex > 0 && previousMinutesAgo.HasValue && minutesAgo == previousMinutesAgo.Value)
                {
                    _logger.LogWarning("Frame {FrameIndex} has same minutesAgo ({MinutesAgo}) as previous frame. Waiting for display to update...", frameIndex, minutesAgo);
                    // Wait for display label to change
                    await WaitForDisplayLabelToChangeAsync(page, previousMinutesAgo.Value);
                    // Re-extract after waiting
                    minutesAgo = await ExtractMinutesAgoFromDisplayAsync(page);
                    if (minutesAgo == null || minutesAgo == previousMinutesAgo.Value)
                    {
                        // Still same or failed - use calculated fallback
                        var (_, defaultMinutesAgo) = frameInfo[frameIndex];
                        minutesAgo = defaultMinutesAgo;
                        _logger.LogWarning("Display label did not update for frame {FrameIndex}, using calculated default: {MinutesAgo}", frameIndex, minutesAgo);
                    }
                }
                
                // Take screenshot with crop configuration
                // Ensure radar subfolder exists
                var radarFolder = FilePathHelper.GetDataTypeFolderPath(cacheFolderPath, CachedDataType.Radar);
                if (!Directory.Exists(radarFolder))
                {
                    Directory.CreateDirectory(radarFolder);
                }
                
                var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, CachedDataType.Radar, frameIndex);
                await CaptureMapScreenshotAsync(page, mapContainer, framePath, containerClip);
                
                frames.Add(new RadarFrame
                {
                    FrameIndex = frameIndex,
                    ImagePath = framePath,
                    MinutesAgo = minutesAgo.Value
                });
                
                previousMinutesAgo = minutesAgo;
                
                _logger.LogInformation("Frame {FrameIndex} saved: {Path} ({MinutesAgo} minutes ago)", 
                    frameIndex, framePath, minutesAgo.Value);
                
                // Track progress: frame captured
                _cacheService.RecordUpdateProgressByFolder(cacheFolderPath, CacheUpdatePhase.CapturingFrames, frameIndex + 1, frameCount);
                
                // Save debug screenshot BEFORE clicking step forward
                await _debugService.SaveStepDebugAsync(debugFolder, 15 + frameIndex, $"frame_{frameIndex}_captured", page, consoleMessages, networkRequests, cancellationToken);
                
                // If not the last frame, click step forward to prepare for next frame
                if (frameIndex < frameCount - 1)
                {
                    // Dismiss any modal overlays (BOM, reCAPTCHA, feedback forms) before clicking
                    await DismissModalOverlaysAsync(page);
                    
                    // Get current minutesAgo before clicking (to detect change)
                    var currentMinutesAgo = await ExtractMinutesAgoFromDisplayAsync(page);
                    
                    // Use force click to bypass any remaining overlays
                    await stepForwardButton.ClickAsync(new LocatorClickOptions { Force = true });
                    
                    // Wait for display label to actually change (not just a fixed timeout)
                    if (currentMinutesAgo.HasValue)
                    {
                        await WaitForDisplayLabelToChangeAsync(page, currentMinutesAgo.Value);
                    }
                    else
                    {
                        // Fallback to fixed wait if we can't detect change
                        await page.WaitForTimeoutAsync(1000);
                    }
                }
            }

            _logger.LogInformation("All {FrameCount} frames captured successfully", frameCount);

            // Step 22: Save metadata and frame information
            // Track progress: switching to saving phase
            _cacheService.RecordUpdateProgressByFolder(cacheFolderPath, CacheUpdatePhase.Saving);
            
            await _cacheService.SaveMetadataAsync(cacheFolderPath, lastUpdatedInfo, cancellationToken);
            await _cacheService.SaveFramesMetadataAsync(cacheFolderPath, CachedDataType.Radar, frames, cancellationToken);

            // Step 23: Return response with all frames
            var cacheExpiresAt = lastUpdatedInfo.ObservationTime.AddMinutes(_cacheExpirationMinutes);
            return ResponseBuilder.CreateRadarResponse(cacheFolderPath, frames, _cacheManagementCheckIntervalMinutes, lastUpdatedInfo, suburb, state, cacheIsValid: true, cacheExpiresAt: cacheExpiresAt, isUpdating: false);
        }
        catch (Exception ex)
        {
            // Save error debug info if debug is enabled
            await _debugService.SaveErrorDebugAsync(debugFolder, $"Exception: {ex.Message}\n\nStackTrace:\n{ex.StackTrace}", page, consoleMessages, networkRequests, cancellationToken);
            _logger.LogError(ex, "Error during radar screenshot capture");
            throw;
        }
    }

    /// <summary>
    /// Calculates the crop area for screenshot based on configuration
    /// </summary>
    private Clip CalculateCropArea(Clip containerClip)
    {
        // Start with container's position plus offset
        var x = containerClip.X + _cropConfig.X;
        var y = containerClip.Y + _cropConfig.Y;
        
        // Calculate width: container width minus left offset (X) minus right offset
        var width = Math.Max(0, containerClip.Width - _cropConfig.X - _cropConfig.RightOffset);
        
        // Calculate height (use configured or remaining height)
        var height = _cropConfig.Height ?? Math.Max(0, containerClip.Height - _cropConfig.Y);
        
        // Validate bounds
        if (x < containerClip.X || y < containerClip.Y)
        {
            _logger.LogWarning("Crop offset is outside container bounds, using container bounds");
            x = containerClip.X;
            y = containerClip.Y;
        }
        
        var maxWidth = containerClip.Width - (x - containerClip.X);
        var maxHeight = containerClip.Height - (y - containerClip.Y);
        
        if (width > maxWidth)
        {
            _logger.LogWarning("Crop width exceeds container bounds, adjusting from {Requested} to {Max}", width, maxWidth);
            width = maxWidth;
        }
        
        if (height > maxHeight)
        {
            _logger.LogWarning("Crop height exceeds container bounds, adjusting from {Requested} to {Max}", height, maxHeight);
            height = maxHeight;
        }
        
        if (width <= 0 || height <= 0)
        {
            throw new Exception($"Invalid crop dimensions: {width}x{height}");
        }
        
        _logger.LogDebug("Crop area calculated: X={X}, Y={Y}, Width={Width}, Height={Height} (container: {ContainerX}, {ContainerY}, {ContainerWidth}x{ContainerHeight})",
            x, y, width, height, containerClip.X, containerClip.Y, containerClip.Width, containerClip.Height);
        
        return new Clip
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
    }

    /// <summary>
    /// Extracts frame information from timeline segments
    /// </summary>
    private async Task<List<(int index, int minutesAgo)>> ExtractFrameInfoAsync(IPage page)
    {
        try
        {
            var frameInfo = await page.EvaluateAsync<object[]>(@"() => {
                const segments = Array.from(document.querySelectorAll('[data-testid=""bom-scrub-segment""]'));
                return segments.map((seg, index) => {
                    const ariaLabel = seg.getAttribute('aria-label') || '';
                    // Extract minutes from '40 minutes ago', '35 minutes ago', etc.
                    const minutesMatch = ariaLabel.match(/(\d+)\s+minutes?\s+ago/);
                    const minutes = minutesMatch ? parseInt(minutesMatch[1]) : null;
                    return { index: index, minutesAgo: minutes };
                });
            }");
            
            var result = new List<(int index, int minutesAgo)>();
            for (int i = 0; i < 7; i++)
            {
                // Default values if extraction fails
                var minutesAgo = 40 - (i * 5);
                
                // Try to use extracted values if available
                if (frameInfo != null && i < frameInfo.Length)
                {
                    // The EvaluateAsync returns object[], we'd need to deserialize properly
                    // For now, use defaults
                }
                
                result.Add((i, minutesAgo));
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract frame info, using defaults");
            // Return default frame info
            return Enumerable.Range(0, 7)
                .Select(i => (i, 40 - (i * 5)))
                .ToList();
        }
    }

    /// <summary>
    /// Extracts the actual minutes ago value from the display label (e.g., "17 minutes ago" -> 17)
    /// </summary>
    private async Task<int?> ExtractMinutesAgoFromDisplayAsync(IPage page)
    {
        try
        {
            var timeLabel = await page.Locator(".bom-scrub-display-label").First.TextContentAsync();
            if (string.IsNullOrEmpty(timeLabel))
            {
                return null;
            }
            
            // Parse "17 minutes ago" or "41 minutes ago" etc.
            var match = System.Text.RegularExpressions.Regex.Match(
                timeLabel.Trim(), 
                @"(\d+)\s+minutes?\s+ago", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            
            if (match.Success && match.Groups.Count >= 2)
            {
                if (int.TryParse(match.Groups[1].Value, out var minutes))
                {
                    return minutes;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract minutes from display label");
            return null;
        }
    }

    /// <summary>
    /// Waits for the display label to change from the current minutesAgo value
    /// </summary>
    private async Task WaitForDisplayLabelToChangeAsync(IPage page, int currentMinutesAgo, int maxWaitMs = 5000)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < maxWaitMs)
            {
                var newMinutesAgo = await ExtractMinutesAgoFromDisplayAsync(page);
                if (newMinutesAgo.HasValue && newMinutesAgo.Value != currentMinutesAgo)
                {
                    // Display has changed
                    return;
                }
                await page.WaitForTimeoutAsync(200); // Check every 200ms
            }
            _logger.LogDebug("Display label did not change from {CurrentMinutesAgo} within {MaxWaitMs}ms", currentMinutesAgo, maxWaitMs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error waiting for display label to change");
        }
    }

    /// <summary>
    /// Dismisses any modal overlays (BOM modals, reCAPTCHA, feedback forms) that might block the radar
    /// Uses a single efficient check and minimal delays
    /// </summary>
    private async Task DismissModalOverlaysAsync(IPage page)
    {
        try
        {
            // Single check for all modal types (BOM, reCAPTCHA, feedback forms)
            var hasModal = await page.EvaluateAsync<bool>(@"() => {
                // Check BOM modal overlay
                const bomOverlay = document.querySelector('.bom-modal-overlay--after-open');
                if (bomOverlay && bomOverlay.style.display !== 'none') {
                    return true;
                }
                
                // Check reCAPTCHA modals (large visible elements)
                const recaptchaSelectors = [
                    '.g-recaptcha',
                    '#g-recaptcha',
                    '.rc-anchor-container'
                ];
                for (const selector of recaptchaSelectors) {
                    const el = document.querySelector(selector);
                    if (el) {
                        const style = window.getComputedStyle(el);
                        const rect = el.getBoundingClientRect();
                        if (style.display !== 'none' && style.visibility !== 'hidden' && 
                            rect.width > 200 && rect.height > 200) {
                            return true;
                        }
                    }
                }
                
                // Check feedback forms with reCAPTCHA
                const allForms = document.querySelectorAll('form');
                for (const form of allForms) {
                    const action = form.getAttribute('action') || '';
                    const id = form.getAttribute('id') || '';
                    if (action.includes('feedback') || id.includes('feedback')) {
                        const style = window.getComputedStyle(form);
                        const rect = form.getBoundingClientRect();
                        if (style.display !== 'none' && rect.width > 200 && rect.height > 200) {
                            const text = form.textContent || '';
                            if (text.includes('reCAPTCHA') || text.includes('recaptcha') || text.includes('Tell us why')) {
                                return true;
                            }
                        }
                    }
                }
                
                return false;
            }");
            
            if (!hasModal)
            {
                return; // No modal, exit quickly
            }
            
            _logger.LogDebug("Modal overlay detected, dismissing");
            
            // Single Escape key press (works for most modals)
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(300); // Minimal delay
            
            // If still visible, try clicking outside (on map)
            var stillVisible = await page.EvaluateAsync<bool>(@"() => {
                const bomOverlay = document.querySelector('.bom-modal-overlay--after-open');
                if (bomOverlay && bomOverlay.style.display !== 'none') return true;
                
                // Check feedback forms
                const allForms = document.querySelectorAll('form');
                for (const form of allForms) {
                    const action = form.getAttribute('action') || '';
                    const id = form.getAttribute('id') || '';
                    if (action.includes('feedback') || id.includes('feedback')) {
                        const style = window.getComputedStyle(form);
                        const rect = form.getBoundingClientRect();
                        if (style.display !== 'none' && rect.width > 200 && rect.height > 200) {
                            return true;
                        }
                    }
                }
                return false;
            }");
            
            if (stillVisible)
            {
                // Click on map to dismiss (only if Escape didn't work)
                try
                {
                    var mapContainer = page.Locator(".esri-view-surface").First;
                    await mapContainer.ClickAsync(new LocatorClickOptions { Force = true });
                    await page.WaitForTimeoutAsync(200); // Minimal delay
                }
                catch
                {
                    // Ignore if click fails
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error dismissing modal overlay, continuing");
        }
    }

    /// <summary>
    /// Captures map screenshot with crop configuration
    /// </summary>
    private async Task CaptureMapScreenshotAsync(IPage page, ILocator mapContainer, string outputPath, Clip containerClip)
    {
        // First, validate container clip itself
        if (containerClip == null || containerClip.Width <= 0 || containerClip.Height <= 0)
        {
            _logger.LogError("Invalid container bounds: X={X}, Y={Y}, Width={Width}, Height={Height}", 
                containerClip?.X ?? 0, containerClip?.Y ?? 0, containerClip?.Width ?? 0, containerClip?.Height ?? 0);
            throw new Exception($"Invalid container bounds: {containerClip?.Width ?? 0}x{containerClip?.Height ?? 0}");
        }
        
        Clip cropArea;
        try
        {
            cropArea = CalculateCropArea(containerClip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate crop area, using full container bounds. Container: X={X}, Y={Y}, Width={Width}, Height={Height}", 
                containerClip.X, containerClip.Y, containerClip.Width, containerClip.Height);
            // Fallback to full container if crop calculation fails
            cropArea = containerClip;
        }
        
        // Get viewport size - if null, try to get it from page evaluation as fallback
        var viewportSize = page.ViewportSize;
        int? viewportWidth = viewportSize?.Width;
        int? viewportHeight = viewportSize?.Height;
        
        if (viewportWidth == null || viewportHeight == null)
        {
            try
            {
                var viewportJson = await page.EvaluateAsync<string>("() => JSON.stringify({ width: window.innerWidth, height: window.innerHeight })");
                if (!string.IsNullOrEmpty(viewportJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(viewportJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("width", out var widthProp) && root.TryGetProperty("height", out var heightProp))
                    {
                        if (widthProp.TryGetInt32(out var width) && heightProp.TryGetInt32(out var height))
                        {
                            viewportWidth = width;
                            viewportHeight = height;
                            _logger.LogDebug("Retrieved viewport size from page evaluation: {Width}x{Height}", width, height);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get viewport size from page evaluation");
            }
            
            // If still can't get it, use container bounds as fallback for validation
            if (viewportWidth == null || viewportHeight == null)
            {
                _logger.LogWarning("Cannot determine viewport size, using container bounds for validation");
                viewportWidth = (int)containerClip.Width;
                viewportHeight = (int)containerClip.Height;
            }
        }
        
        // Validate crop area is within page bounds and adjust if necessary
        if (viewportWidth.HasValue && viewportHeight.HasValue)
        {
            // Ensure crop area coordinates are non-negative
            if (cropArea.X < 0)
            {
                _logger.LogWarning("Crop X is negative ({X}), adjusting to 0", cropArea.X);
                cropArea = new Clip { X = 0, Y = cropArea.Y, Width = cropArea.Width + cropArea.X, Height = cropArea.Height };
            }
            if (cropArea.Y < 0)
            {
                _logger.LogWarning("Crop Y is negative ({Y}), adjusting to 0", cropArea.Y);
                cropArea = new Clip { X = cropArea.X, Y = 0, Width = cropArea.Width, Height = cropArea.Height + cropArea.Y };
            }
            
            // Ensure crop area doesn't exceed viewport bounds
            if (cropArea.X + cropArea.Width > viewportWidth.Value)
            {
                var newWidth = viewportWidth.Value - cropArea.X;
                _logger.LogWarning("Crop width exceeds viewport ({Requested} > {Max}), adjusting to {NewWidth}", 
                    cropArea.Width, viewportWidth.Value, newWidth);
                cropArea = new Clip { X = cropArea.X, Y = cropArea.Y, Width = newWidth, Height = cropArea.Height };
            }
            if (cropArea.Y + cropArea.Height > viewportHeight.Value)
            {
                var newHeight = viewportHeight.Value - cropArea.Y;
                _logger.LogWarning("Crop height exceeds viewport ({Requested} > {Max}), adjusting to {NewHeight}", 
                    cropArea.Height, viewportHeight.Value, newHeight);
                cropArea = new Clip { X = cropArea.X, Y = cropArea.Y, Width = cropArea.Width, Height = newHeight };
            }
        }
        
        // Final validation - ensure dimensions are positive
        if (cropArea.Width <= 0 || cropArea.Height <= 0)
        {
            _logger.LogError("Invalid crop dimensions after validation: {Width}x{Height}, using full container", cropArea.Width, cropArea.Height);
            cropArea = containerClip;
        }
        
        // Double-check container clip is still valid as final fallback
        if (cropArea.Width <= 0 || cropArea.Height <= 0)
        {
            _logger.LogError("Cannot create valid crop area. Container: {ContainerWidth}x{ContainerHeight}, Viewport: {ViewportWidth}x{ViewportHeight}", 
                containerClip.Width, containerClip.Height, viewportWidth ?? 0, viewportHeight ?? 0);
            throw new Exception($"Cannot create valid crop area. Container: {containerClip.Width}x{containerClip.Height}, Viewport: {viewportWidth ?? 0}x{viewportHeight ?? 0}");
        }
        
        // Wait for fonts to be loaded to prevent text rendering artifacts
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
        }
        catch
        {
            // Continue if network idle timeout - fonts may already be loaded
        }
        
        // Dismiss any modal overlays (BOM, reCAPTCHA, feedback forms) before taking screenshot
        await DismissModalOverlaysAsync(page);
        
        // Take high-quality screenshot with explicit PNG format and disabled animations
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            Clip = cropArea,
            Type = ScreenshotType.Png, // Explicit PNG for lossless quality
            Animations = ScreenshotAnimations.Disabled // Disable animations to prevent artifacts
        });
        
        _logger.LogDebug("Screenshot saved: {Path} (crop: {X},{Y} {Width}x{Height})", 
            outputPath, cropArea.X, cropArea.Y, cropArea.Width, cropArea.Height);
    }
}

