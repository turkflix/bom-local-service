using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Scraping;
using Microsoft.Playwright;

namespace BomLocalService.Services.Scraping.Steps.Search;

public class FillSearchInputStep : BaseScrapingStep
{
    public override string Name => "FillSearchInput";
    public override string[] Prerequisites => new[] { "ClickSearchButton" };
    
    public FillSearchInputStep(
        ILogger<FillSearchInputStep> logger,
        ISelectorService selectorService,
        IDebugService debugService,
        IConfiguration configuration)
        : base(logger, selectorService, debugService, configuration)
    {
    }
    
    public override bool CanExecute(ScrapingContext context)
    {
        return context.IsSearchModalOpen;
    }
    
    public override async Task<ScrapingStepResult> ExecuteAsync(ScrapingContext context, CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Step {Step}: Searching for suburb: {Suburb}", Name, context.Suburb);
            
            var searchInput = SelectorService.GetLocator(context.Page, Selectors.SearchInput);
            await searchInput.FillAsync(context.Suburb);
            
            context.SearchInput = searchInput;
            await SaveDebugAsync(context, 3, "search_input_filled", cancellationToken);
            
            return ScrapingStepResult.Successful();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Step {Step} failed", Name);
            await SaveErrorDebugAsync(context, $"Failed to fill search input: {ex.Message}", cancellationToken);
            return ScrapingStepResult.Failed($"Failed to fill search input: {ex.Message}");
        }
    }
}

