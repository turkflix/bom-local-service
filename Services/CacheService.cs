using BomLocalService.Models;
using BomLocalService.Services.Interfaces;
using BomLocalService.Utilities;
using System.Text.Json;

namespace BomLocalService.Services;

public class CacheService : ICacheService
{
    private readonly ILogger<CacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly double _cacheExpirationMinutes;

    public CacheService(ILogger<CacheService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        _cacheExpirationMinutes = configuration.GetValue<double>("CacheExpirationMinutes", 15.5);
        
        // Ensure cache directory exists
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Gets the cached screenshot folder path and metadata for a location
    /// </summary>
    public async Task<(string? cacheFolderPath, LastUpdatedInfo? metadata)> GetCachedScreenshotWithMetadataAsync(
        string suburb, 
        string state, 
        string? excludeFolder = null,
        CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFolderPattern(suburb, state);
        _logger.LogDebug("Looking for cached folders matching pattern: {Pattern} in directory: {Directory}", pattern, _cacheDirectory);
        
        var folders = Directory.GetDirectories(_cacheDirectory, pattern)
            .OrderByDescending(f => 
            {
                // Try to extract timestamp from folder name
                var folderName = Path.GetFileName(f);
                var timestamp = LocationHelper.ParseTimestampFromFilename(folderName);
                if (timestamp.HasValue)
                {
                    return timestamp.Value;
                }
                // Fallback to folder creation time
                return Directory.GetCreationTime(f);
            })
            .ToList();

        // Find the first complete cache folder (has all 7 frames + metadata)
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;
            
            // Skip the folder if it's being excluded (currently being written to)
            if (!string.IsNullOrEmpty(excludeFolder) && Path.GetFullPath(folder).Equals(Path.GetFullPath(excludeFolder), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping excluded cache folder (being written to): {Folder}", folder);
                continue;
            }
            
            // Check if folder is complete: must have all 7 frames and metadata.json
            var hasAllFrames = true;
            for (int i = 0; i < 7; i++)
            {
                var framePath = FilePathHelper.GetFrameFilePath(folder, i);
                if (!File.Exists(framePath))
                {
                    hasAllFrames = false;
                    break;
                }
            }
            
            if (!hasAllFrames)
            {
                _logger.LogDebug("Skipping incomplete cache folder: {Folder}", folder);
                continue; // Skip incomplete folders (currently being written to)
            }
            
            // Check if metadata exists (indicates folder is complete)
            var metadataPath = FilePathHelper.GetMetadataFilePath(folder);
            if (!File.Exists(metadataPath))
            {
                _logger.LogDebug("Skipping cache folder without metadata: {Folder}", folder);
                continue; // Skip folders without metadata
            }
            
            // This folder is complete
            var metadata = await LoadMetadataAsync(folder, cancellationToken);
            return (folder, metadata);
        }

        // No complete cache folder found
        return (null, null);
    }
    
    /// <summary>
    /// Gets all cached frames for a location
    /// </summary>
    public async Task<List<RadarFrame>> GetCachedFramesAsync(
        string suburb, 
        string state, 
        CancellationToken cancellationToken = default)
    {
        var (cacheFolderPath, _) = await GetCachedScreenshotWithMetadataAsync(suburb, state, null, cancellationToken);
        
        if (string.IsNullOrEmpty(cacheFolderPath) || !Directory.Exists(cacheFolderPath))
        {
            return new List<RadarFrame>();
        }
        
        var frames = new List<RadarFrame>();
        
        // Load frame metadata from frames.json if available
        var framesMetadata = await LoadFramesMetadataAsync(cacheFolderPath, cancellationToken);
        var metadataDict = framesMetadata.ToDictionary(f => f.FrameIndex, f => f.MinutesAgo);
        
        // Load frames from folder (frame_0.png through frame_6.png)
        for (int i = 0; i < 7; i++)
        {
            var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, i);
            if (File.Exists(framePath))
            {
                // Use stored minutesAgo if available, otherwise default
                var minutesAgo = metadataDict.ContainsKey(i) 
                    ? metadataDict[i] 
                    : 40 - (i * 5);
                    
                frames.Add(new RadarFrame
                {
                    FrameIndex = i,
                    ImagePath = framePath,
                    MinutesAgo = minutesAgo
                });
            }
        }
        
        return frames;
    }
    
    /// <summary>
    /// Gets a specific cached frame for a location
    /// </summary>
    public async Task<RadarFrame?> GetCachedFrameAsync(
        string suburb, 
        string state, 
        int frameIndex,
        CancellationToken cancellationToken = default)
    {
        if (frameIndex < 0 || frameIndex > 6)
        {
            return null;
        }
        
        var (cacheFolderPath, _) = await GetCachedScreenshotWithMetadataAsync(suburb, state, null, cancellationToken);
        
        if (string.IsNullOrEmpty(cacheFolderPath))
        {
            return null;
        }
        
        var framePath = FilePathHelper.GetFrameFilePath(cacheFolderPath, frameIndex);
        if (!File.Exists(framePath))
        {
            return null;
        }
        
        // Load frame metadata to get accurate minutesAgo
        var framesMetadata = await LoadFramesMetadataAsync(cacheFolderPath, cancellationToken);
        var frameMetadata = framesMetadata.FirstOrDefault(f => f.FrameIndex == frameIndex);
        var minutesAgo = frameMetadata != null 
            ? frameMetadata.MinutesAgo 
            : 40 - (frameIndex * 5);
            
        return new RadarFrame
        {
            FrameIndex = frameIndex,
            ImagePath = framePath,
            MinutesAgo = minutesAgo
        };
    }

    /// <summary>
    /// Saves metadata in a cache folder
    /// </summary>
    public async Task SaveMetadataAsync(string cacheFolderPath, LastUpdatedInfo metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadataPath = FilePathHelper.GetMetadataFilePath(cacheFolderPath);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            // Use CancellationToken.None for file writes to avoid cancellation issues
            // The frames are already saved, metadata save is best-effort
            await File.WriteAllTextAsync(metadataPath, json, CancellationToken.None);
            _logger.LogDebug("Saved metadata to: {Path}", metadataPath);
        }
        catch (Exception ex)
        {
            // Log but don't fail - frames are already saved
            _logger.LogWarning(ex, "Failed to save metadata for folder: {Path}", cacheFolderPath);
        }
    }
    
    /// <summary>
    /// Saves frame metadata to frames.json
    /// </summary>
    public async Task SaveFramesMetadataAsync(string cacheFolderPath, List<RadarFrame> frames, CancellationToken cancellationToken = default)
    {
        try
        {
            var framesMetadata = frames.Select(f => new FrameMetadata
            {
                FrameIndex = f.FrameIndex,
                MinutesAgo = f.MinutesAgo
            }).ToList();
            
            var framesPath = Path.Combine(cacheFolderPath, "frames.json");
            var json = JsonSerializer.Serialize(framesMetadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(framesPath, json, CancellationToken.None);
            _logger.LogDebug("Saved frames metadata to: {Path}", framesPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save frames metadata for folder: {Path}", cacheFolderPath);
        }
    }
    
    /// <summary>
    /// Loads frame metadata from frames.json
    /// </summary>
    private async Task<List<FrameMetadata>> LoadFramesMetadataAsync(string cacheFolderPath, CancellationToken cancellationToken = default)
    {
        var framesPath = Path.Combine(cacheFolderPath, "frames.json");
        if (!File.Exists(framesPath))
        {
            return new List<FrameMetadata>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(framesPath, cancellationToken);
            var framesMetadata = JsonSerializer.Deserialize<List<FrameMetadata>>(json);
            return framesMetadata ?? new List<FrameMetadata>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load frames metadata from: {Path}", framesPath);
            return new List<FrameMetadata>();
        }
    }

    /// <summary>
    /// Loads metadata from a cache folder
    /// </summary>
    private async Task<LastUpdatedInfo?> LoadMetadataAsync(string cacheFolderPath, CancellationToken cancellationToken = default)
    {
        var metadataPath = FilePathHelper.GetMetadataFilePath(cacheFolderPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return JsonSerializer.Deserialize<LastUpdatedInfo>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load metadata from: {Path}", metadataPath);
            return null;
        }
    }

    /// <summary>
    /// Checks if cache is still valid based on observation time
    /// </summary>
    public bool IsCacheValid(LastUpdatedInfo? metadata)
    {
        if (metadata == null)
        {
            return false;
        }

        // BOM updates observations at :00, :15, :30, :45 (every 15 minutes by default)
        // Cache expiration includes buffer (e.g., 15.5 minutes = 15 min + 30 sec buffer)
        var nextUpdateTime = metadata.ObservationTime.AddMinutes(_cacheExpirationMinutes);
        return DateTime.UtcNow < nextUpdateTime;
    }

    /// <summary>
    /// Gets the path to the cached screenshot for a location (returns first frame path for backward compatibility)
    /// </summary>
    public async Task<string> GetCachedScreenshotPathAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var frames = await GetCachedFramesAsync(suburb, state, cancellationToken);
        return frames.FirstOrDefault()?.ImagePath ?? string.Empty;
    }

    /// <summary>
    /// Deletes all cached folders for a location
    /// </summary>
    public Task<bool> DeleteCachedLocationAsync(string suburb, string state, CancellationToken cancellationToken = default)
    {
        var pattern = FilePathHelper.GetCacheFolderPattern(suburb, state);
        var deleted = false;

        try
        {
            // Delete all folders matching the pattern
            var folders = Directory.GetDirectories(_cacheDirectory, pattern);
            foreach (var folder in folders)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _logger.LogInformation("Deleted cached folder: {Folder}", folder);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete folder: {Folder}", folder);
                }
            }

            if (deleted)
            {
                _logger.LogInformation("Deleted all cached folders for location: {Suburb}, {State}", suburb, state);
            }
            else
            {
                _logger.LogDebug("No cached folders found to delete for location: {Suburb}, {State}", suburb, state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cached location: {Suburb}, {State}", suburb, state);
            return Task.FromResult(false);
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Gets the cache directory path
    /// </summary>
    public string GetCacheDirectory() => _cacheDirectory;
}

