using BomLocalService.Services.Interfaces;
using Microsoft.Playwright;

namespace BomLocalService.Services;

public class BrowserService : IBrowserService
{
    private readonly ILogger<BrowserService> _logger;
    private readonly string _timezone;
    private readonly IDebugService _debugService;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public BrowserService(ILogger<BrowserService> logger, IConfiguration configuration, IDebugService debugService)
    {
        _logger = logger;
        _timezone = configuration.GetValue<string>("Timezone") ?? "Australia/Brisbane";
        _debugService = debugService;
    }

    /// <summary>
    /// Gets or creates the Playwright instance
    /// </summary>
    private async Task<IPlaywright> GetPlaywrightAsync()
    {
        if (_playwright == null)
        {
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        }
        return _playwright;
    }

    /// <summary>
    /// Gets or creates the browser instance
    /// </summary>
    public async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser == null)
        {
            var playwright = await GetPlaywrightAsync();
            _browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false, // Run in visible mode with virtual display (Xvfb)
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-dev-shm-usage",
                    "--disable-infobars",
                    "--disable-extensions",
                    "--disable-background-networking",
                    "--disable-background-timer-throttling",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-breakpad",
                    "--disable-client-side-phishing-detection",
                    "--disable-component-extensions-with-background-pages",
                    "--disable-component-update",
                    "--disable-default-apps",
                    "--disable-domain-reliability",
                    "--disable-features=AudioServiceOutOfProcess,IsolateOrigins,site-per-process,TranslateUI,BlinkGenPropertyTrees",
                    "--disable-hang-monitor",
                    "--disable-ipc-flooding-protection",
                    "--disable-notifications",
                    "--disable-offer-store-unmasked-wallet-cards",
                    "--disable-popup-blocking",
                    "--disable-prompt-on-repost",
                    "--disable-renderer-backgrounding",
                    "--disable-sync",
                    "--force-color-profile=srgb",
                    "--metrics-recording-only",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--no-pings",
                    "--password-store=basic",
                    "--use-mock-keychain",
                    "--enable-automation=false",
                    "--exclude-switches=enable-automation",
                    "--disable-features=UserAgentClientHint",
                    "--enable-unsafe-swiftshader", // Required for software WebGL (Chrome 137+ deprecated automatic fallback)
                    "--use-gl=swiftshader" // Use SwiftShader for WebGL software rendering
                }
            });
        }
        return _browser;
    }

    /// <summary>
    /// Creates a new browser context with proper configuration
    /// </summary>
    public async Task<IBrowserContext> CreateContextAsync()
    {
        var browser = await GetBrowserAsync();
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            Locale = "en-AU",
            TimezoneId = _timezone,
            Permissions = new[] { "geolocation" },
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                { "Accept-Language", "en-AU,en;q=0.9" },
                { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8" },
                { "Accept-Encoding", "gzip, deflate, br" },
                { "Connection", "keep-alive" },
                { "Upgrade-Insecure-Requests", "1" },
                { "Sec-Fetch-Dest", "document" },
                { "Sec-Fetch-Mode", "navigate" },
                { "Sec-Fetch-Site", "none" },
                { "Cache-Control", "max-age=0" }
            }
        });
    }

    /// <summary>
    /// Creates a new page with anti-detection scripts and debug event handlers
    /// </summary>
    public async Task<(IPage page, List<(string type, string text, DateTime timestamp)> consoleMessages, List<(string method, string url, int? status, string resourceType, DateTime timestamp)> networkRequests)> CreatePageWithDebugAsync(
        IBrowserContext context, 
        string requestId)
    {
        var page = await context.NewPageAsync();
        
        // Set up console message and network request capture for debugging
        var consoleMessages = new List<(string type, string text, DateTime timestamp)>();
        var networkRequests = new List<(string method, string url, int? status, string resourceType, DateTime timestamp)>();
        
        if (_debugService.IsEnabled)
        {
            // Capture page console messages (console.log, console.error, etc.)
            page.Console += (sender, e) =>
            {
                consoleMessages.Add((e.Type.ToString(), e.Text, DateTime.UtcNow));
            };
            
            // Capture page errors (unhandled JavaScript errors)
            page.PageError += (sender, e) =>
            {
                consoleMessages.Add(("error", $"PageError: {e}", DateTime.UtcNow));
            };
            
            // Capture context console messages (broader scope, may include more warnings)
            context.Console += (sender, e) =>
            {
                consoleMessages.Add((e.Type.ToString(), $"[Context] {e.Text}", DateTime.UtcNow));
            };
            
            // Capture network requests
            page.Request += (sender, e) =>
            {
                networkRequests.Add((e.Method, e.Url, null, e.ResourceType, DateTime.UtcNow));
            };
            
            // Capture failed requests (these often show up as console errors in browser)
            page.RequestFailed += (sender, e) =>
            {
                networkRequests.Add((e.Method, e.Url, null, e.ResourceType, DateTime.UtcNow));
                var failureText = e.Failure?.ToString() ?? "Unknown failure";
                consoleMessages.Add(("error", $"Request failed: {e.Method} {e.Url} - {failureText}", DateTime.UtcNow));
            };
            
            page.Response += (sender, e) =>
            {
                // Update the request with response status
                var matchingRequests = networkRequests.Where(r => r.url == e.Request.Url && r.status == null).ToList();
                if (matchingRequests.Any())
                {
                    var request = matchingRequests.Last();
                    var index = networkRequests.IndexOf(request);
                    networkRequests[index] = (request.method, request.url, e.Status, request.resourceType, request.timestamp);
                }
            };
        }
        
        // Inject comprehensive anti-detection scripts (must run before page load)
        await page.AddInitScriptAsync(GetAntiDetectionScript());
        
        return (page, consoleMessages, networkRequests);
    }

    /// <summary>
    /// Gets the anti-detection JavaScript script
    /// </summary>
    private static string GetAntiDetectionScript()
    {
        return @"
            (function() {
                'use strict';

                // Remove webdriver property completely
                Object.defineProperty(navigator, 'webdriver', {
                    get: () => false,
                    configurable: true
                });

                // Delete it from prototype chain
                try {
                    delete navigator.__proto__.webdriver;
                } catch (e) {}

                // Override chrome object
                window.chrome = {
                    runtime: {},
                    loadTimes: function() {},
                    csi: function() {},
                    app: {}
                };

                // Override plugins with realistic data
                Object.defineProperty(navigator, 'plugins', {
                    get: () => {
                        const plugins = [];
                        for (let i = 0; i < 3; i++) {
                            plugins.push({
                                0: { type: 'application/x-google-chrome-pdf', suffixes: 'pdf', description: 'Portable Document Format' },
                                description: 'Portable Document Format',
                                filename: 'internal-pdf-viewer',
                                length: 1,
                                name: 'Chrome PDF Plugin'
                            });
                        }
                        return plugins;
                    },
                    configurable: true
                });

                // Override languages
                Object.defineProperty(navigator, 'languages', {
                    get: () => ['en-AU', 'en', 'en-US'],
                    configurable: true
                });

                // Override language
                Object.defineProperty(navigator, 'language', {
                    get: () => 'en-AU',
                    configurable: true
                });

                // Override vendor
                Object.defineProperty(navigator, 'vendor', {
                    get: () => 'Google Inc.',
                    configurable: true
                });

                // Override platform
                Object.defineProperty(navigator, 'platform', {
                    get: () => 'Win32',
                    configurable: true
                });

                // Override hardwareConcurrency
                Object.defineProperty(navigator, 'hardwareConcurrency', {
                    get: () => 8,
                    configurable: true
                });

                // Override deviceMemory
                if ('deviceMemory' in navigator) {
                    Object.defineProperty(navigator, 'deviceMemory', {
                        get: () => 8,
                        configurable: true
                    });
                }

                // Spoof connection - use named functions to avoid warnings
                Object.defineProperty(navigator, 'connection', {
                    get: () => ({
                        effectiveType: '4g',
                        rtt: 50,
                        downlink: 10,
                        saveData: false,
                        onchange: null,
                        addEventListener: function addEventListener() {},
                        removeEventListener: function removeEventListener() {},
                        dispatchEvent: function dispatchEvent() { return true; }
                    }),
                    configurable: true
                });

                // Override permissions API - bind context to avoid illegal invocation
                const originalQuery = window.navigator.permissions.query.bind(window.navigator.permissions);
                window.navigator.permissions.query = function query(parameters) {
                    return parameters.name === 'notifications' ?
                        Promise.resolve({ state: Notification.permission }) :
                        originalQuery(parameters);
                };

                // Override getBattery - bind context to avoid illegal invocation
                if ('getBattery' in navigator) {
                    navigator.getBattery = function getBattery() {
                        return Promise.resolve({
                            charging: true,
                            chargingTime: 0,
                            dischargingTime: Infinity,
                            level: 1,
                            onchargingchange: null,
                            onchargingtimechange: null,
                            ondischargingtimechange: null,
                            onlevelchange: null,
                            addEventListener: function addEventListener() {},
                            removeEventListener: function removeEventListener() {},
                            dispatchEvent: function dispatchEvent() { return true; }
                        });
                    };
                }

                // DON'T use Proxy on navigator - it breaks APIs like getGamepads()
                // Just hide webdriver property directly
                try {
                    delete navigator.__proto__.webdriver;
                } catch (e) {}

                // AudioContext fingerprinting
                if (window.AudioContext) {
                    const originalAudioContext = window.AudioContext;
                    window.AudioContext = function() {
                        const ctx = new originalAudioContext();
                        const originalCreateOscillator = ctx.createOscillator;
                        ctx.createOscillator = function() {
                            const oscillator = originalCreateOscillator.call(this);
                            const originalFrequency = Object.getOwnPropertyDescriptor(
                                AudioParam.prototype, 'value'
                            ).get;
                            Object.defineProperty(oscillator.frequency, 'value', {
                                get: originalFrequency,
                                set: function() {}
                            });
                            return oscillator;
                        };
                        return ctx;
                    };
                }
            })();
        ";
    }

    /// <summary>
    /// Initializes the browser (pre-warming)
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Pre-warming browser");
        try
        {
            await GetBrowserAsync();
            _logger.LogInformation("Browser initialized and ready");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-warm browser, will initialize on first use");
        }
    }

    /// <summary>
    /// Gets the semaphore for synchronizing browser operations
    /// </summary>
    public SemaphoreSlim GetSemaphore() => _semaphore;

    public void Dispose()
    {
        _browser?.DisposeAsync().AsTask().Wait();
        _playwright?.Dispose();
        _semaphore?.Dispose();
    }
}

