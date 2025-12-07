using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using Microsoft.Playwright;

namespace BomLocalService.Services;

    public class DebugService : IDebugService
    {
        private readonly ILogger<DebugService> _logger;
        private readonly bool _enabled;
        private readonly string _debugDirectory;
        private readonly int _waitMs;

        public DebugService(ILogger<DebugService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _waitMs = configuration.GetValue<int>("Debug:WaitMs", 2000);
        
        // Check all possible ways the config might be set
        var debugEnabledEnv = Environment.GetEnvironmentVariable("DEBUG__ENABLED");
        var debugEnabledConfig = configuration.GetValue<bool>("Debug:Enabled", false);
        var debugEnabledFromConfig = configuration["Debug:Enabled"];
        
        _logger.LogInformation("Debug configuration check - Env var DEBUG__ENABLED: {EnvVar}, Config Debug:Enabled: {ConfigValue}, Config string: {ConfigString}", 
            debugEnabledEnv ?? "null", debugEnabledConfig, debugEnabledFromConfig ?? "null");
        
        // Try environment variable first, then config
        _enabled = !string.IsNullOrEmpty(debugEnabledEnv) 
            ? bool.TryParse(debugEnabledEnv, out var envBool) && envBool
            : debugEnabledConfig;
        
        var cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        _debugDirectory = Path.Combine(cacheDirectory, "debug");
        
        if (_enabled)
        {
            Directory.CreateDirectory(_debugDirectory);
            _logger.LogInformation("Debug mode is ENABLED. Debug files will be saved to: {DebugDirectory}", _debugDirectory);
        }
        else
        {
            _logger.LogInformation("Debug mode is DISABLED");
        }
    }

    public bool IsEnabled => _enabled;

    public string CreateRequestFolder(string requestId)
    {
        if (!_enabled) return string.Empty;

        var requestFolder = Path.Combine(_debugDirectory, requestId);
        Directory.CreateDirectory(requestFolder);
        _logger.LogDebug("Created debug folder for request: {RequestId} at {Path}", requestId, requestFolder);
        return requestFolder;
    }

    public async Task SaveStepDebugAsync(string requestFolder, int stepNumber, string stepName, IPage page, List<(string type, string text, DateTime timestamp)>? consoleMessages = null, List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null, CancellationToken cancellationToken = default)
    {
        if (!_enabled || string.IsNullOrEmpty(requestFolder)) return;

        try
        {
            // Wait for page to be fully loaded before taking screenshot
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 });
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 5000 });
            }
            catch
            {
                // If network idle times out, at least wait for DOM
            }
            await page.WaitForTimeoutAsync(_waitMs); // Additional wait for dynamic content and animations

            var stepFolder = Path.Combine(requestFolder, $"step_{stepNumber:D2}_{stepName}");
            Directory.CreateDirectory(stepFolder);

            // Save screenshot
            var screenshotPath = Path.Combine(stepFolder, "screenshot.png");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true
            });

            // Save HTML
            var htmlContent = await page.ContentAsync();
            var htmlPath = Path.Combine(stepFolder, "page.html");
            await File.WriteAllTextAsync(htmlPath, htmlContent, cancellationToken);

            // Save console messages if provided
            if (consoleMessages != null && consoleMessages.Count > 0)
            {
                var consolePath = Path.Combine(stepFolder, "console.log");
                var consoleText = string.Join("\n", consoleMessages.Select(msg => $"[{msg.timestamp:HH:mm:ss.fff}] [{msg.type}] {msg.text}"));
                await File.WriteAllTextAsync(consolePath, consoleText, cancellationToken);
            }

            // Save network requests summary if provided
            if (networkRequests != null && networkRequests.Count > 0)
            {
                var networkPath = Path.Combine(stepFolder, "network.log");
                var networkText = string.Join("\n", networkRequests.Select(req => 
                    $"[{req.timestamp:HH:mm:ss.fff}] {req.method} {req.url} -> {req.status?.ToString() ?? "pending"} ({req.resourceType})"
                ));
                await File.WriteAllTextAsync(networkPath, networkText, cancellationToken);
            }

            _logger.LogDebug("Saved debug files for step {StepNumber} ({StepName}) to {Path}", stepNumber, stepName, stepFolder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debug files for step {StepNumber} ({StepName})", stepNumber, stepName);
        }
    }

    public async Task SaveErrorDebugAsync(string requestFolder, string errorMessage, IPage? page = null, 
        List<(string type, string text, DateTime timestamp)>? consoleMessages = null, 
        List<(string method, string url, int? status, string resourceType, DateTime timestamp)>? networkRequests = null, 
        CancellationToken cancellationToken = default)
    {
        if (!_enabled || string.IsNullOrEmpty(requestFolder)) return;

        try
        {
            var errorFolder = Path.Combine(requestFolder, "error");
            Directory.CreateDirectory(errorFolder);

            // Save error message
            var errorPath = Path.Combine(errorFolder, "error.txt");
            await File.WriteAllTextAsync(errorPath, errorMessage, cancellationToken);

            // Save screenshot if page is available
            if (page != null)
            {
                var screenshotPath = Path.Combine(errorFolder, "screenshot.png");
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = true
                });

                // Save HTML
                var htmlContent = await page.ContentAsync();
                var htmlPath = Path.Combine(errorFolder, "page.html");
                await File.WriteAllTextAsync(htmlPath, htmlContent, cancellationToken);
            }

            // Save console messages if provided
            if (consoleMessages != null && consoleMessages.Count > 0)
            {
                var consolePath = Path.Combine(errorFolder, "console.log");
                var consoleText = string.Join("\n", consoleMessages.Select(msg => $"[{msg.timestamp:HH:mm:ss.fff}] [{msg.type}] {msg.text}"));
                await File.WriteAllTextAsync(consolePath, consoleText, cancellationToken);
            }

            // Save network requests summary if provided
            if (networkRequests != null && networkRequests.Count > 0)
            {
                var networkPath = Path.Combine(errorFolder, "network.log");
                var networkText = string.Join("\n", networkRequests.Select(req => 
                    $"[{req.timestamp:HH:mm:ss.fff}] {req.method} {req.url} -> {req.status?.ToString() ?? "pending"} ({req.resourceType})"
                ));
                await File.WriteAllTextAsync(networkPath, networkText, cancellationToken);
            }

            _logger.LogDebug("Saved error debug files to {Path}", errorFolder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save error debug files");
        }
    }
}

