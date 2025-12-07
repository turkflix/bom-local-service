using BomLocalService.Utilities;
using Microsoft.Extensions.Hosting;

namespace BomLocalService.Services;

public class CacheCleanupService : BackgroundService
{
    private readonly ILogger<CacheCleanupService> _logger;
    private readonly string _cacheDirectory;
    private readonly int _retentionHours;
    private readonly TimeSpan _cleanupInterval;

    public CacheCleanupService(ILogger<CacheCleanupService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _cacheDirectory = FilePathHelper.GetCacheDirectory(configuration);
        _retentionHours = configuration.GetValue<int>("CacheRetentionHours", 24);
        var cleanupIntervalHours = configuration.GetValue<int>("CacheCleanup:IntervalHours", 1);
        _cleanupInterval = TimeSpan.FromHours(cleanupIntervalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cache cleanup service started. Retention: {RetentionHours} hours, Interval: {Interval}", 
            _retentionHours, _cleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldCacheFilesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private Task CleanupOldCacheFilesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return Task.CompletedTask;
        }

        var cutoffTime = DateTime.UtcNow.AddHours(-_retentionHours);
        var deletedCount = 0;
        var totalSize = 0L;

        try
        {
            // Get all PNG files in cache directory
            var pngFiles = Directory.GetFiles(_cacheDirectory, "*.png", SearchOption.TopDirectoryOnly);
            
            foreach (var file in pngFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileInfo = new FileInfo(file);
                    var fileTime = fileInfo.LastWriteTimeUtc;

                    // Delete if file is older than retention period
                    if (fileTime < cutoffTime)
                    {
                        var fileSize = fileInfo.Length;
                        File.Delete(file);
                        deletedCount++;
                        totalSize += fileSize;

                        // Also delete associated metadata file if it exists
                        var metadataFile = FilePathHelper.GetMetadataFilePath(file);
                        if (File.Exists(metadataFile))
                        {
                            File.Delete(metadataFile);
                        }

                        _logger.LogDebug("Deleted old cache file: {File} (age: {Age})", 
                            Path.GetFileName(file), DateTime.UtcNow - fileTime);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cache file: {File}", file);
                }
            }

            if (deletedCount > 0)
            {
                var sizeInMB = totalSize / (1024.0 * 1024.0);
                _logger.LogInformation("Cache cleanup completed. Deleted {Count} files ({Size:F2} MB)", 
                    deletedCount, sizeInMB);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache file enumeration");
        }

        // Also clean up old debug directories
        try
        {
            var debugDir = Path.Combine(_cacheDirectory, "debug");
            if (Directory.Exists(debugDir))
            {
                var debugDirs = Directory.GetDirectories(debugDir);
                foreach (var dir in debugDirs)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (dirInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            Directory.Delete(dir, recursive: true);
                            _logger.LogDebug("Deleted old debug directory: {Dir}", dir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete debug directory: {Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up debug directories: {Error}", ex.Message);
        }

        return Task.CompletedTask;
    }
}

