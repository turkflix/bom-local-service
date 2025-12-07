using BomLocalService.Services;
using BomLocalService.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// Register core services via interfaces (order matters - dependencies must be registered first)
builder.Services.AddSingleton<IDebugService, DebugService>();
builder.Services.AddSingleton<ICacheService, CacheService>();
builder.Services.AddSingleton<ITimeParsingService, TimeParsingService>();
builder.Services.AddSingleton<IBrowserService, BrowserService>();
builder.Services.AddSingleton<IScrapingService, ScrapingService>();

// Register BOM Radar Service as singleton (orchestrator, depends on all above services)
builder.Services.AddSingleton<IBomRadarService, BomLocalService.Services.BomRadarService>();

// Register background services (order matters - management service needs radar service)
builder.Services.AddHostedService<CacheCleanupService>();
builder.Services.AddHostedService<CacheManagementService>();

// Add configuration - appsettings.json provides default values
// All values can be overridden via environment variables
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// HTTPS redirection is optional and disabled by default for Docker flexibility
// Users can enable it by setting ENABLE_HTTPS_REDIRECTION=true environment variable
// or EnableHttpsRedirection=true in appsettings.json
var enableHttpsRedirection = builder.Configuration.GetValue<bool>("EnableHttpsRedirection", false) ||
                             Environment.GetEnvironmentVariable("ENABLE_HTTPS_REDIRECTION")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
if (enableHttpsRedirection)
{
    app.UseHttpsRedirection();
}

// No authorization required - service is designed to run behind a reverse proxy if auth is needed
app.MapControllers();

// Cleanup on shutdown
app.Lifetime.ApplicationStopped.Register(() =>
{
    var service = app.Services.GetService<IBomRadarService>();
    if (service is IDisposable disposable)
    {
        disposable.Dispose();
    }
});

app.Run();
