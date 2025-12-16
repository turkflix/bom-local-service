namespace BomLocalService.Models;

/// <summary>
/// Standardized error response model for API endpoints.
/// Provides consistent error structure with error codes, types, and detailed information.
/// </summary>
public class ApiErrorResponse
{
    /// <summary>
    /// Machine-readable error code for programmatic handling.
    /// Examples: "CACHE_NOT_FOUND", "VALIDATION_ERROR", "INTERNAL_ERROR", "CACHE_UPDATE_FAILED"
    /// </summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable error message describing what went wrong.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Error type/category for grouping similar errors.
    /// Examples: "CacheError", "ValidationError", "ServiceError", "NotFoundError"
    /// </summary>
    public string ErrorType { get; set; } = string.Empty;

    /// <summary>
    /// Additional context about the error (e.g., field name for validation errors).
    /// </summary>
    public Dictionary<string, object>? Details { get; set; }

    /// <summary>
    /// Suggested action for the client (e.g., "retry_after_seconds", "refresh_cache").
    /// </summary>
    public Dictionary<string, object>? Suggestions { get; set; }

    /// <summary>
    /// Timestamp when the error occurred (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Helper class for creating standardized error responses.
/// </summary>
public static class ApiErrorResponseBuilder
{
    /// <summary>
    /// Creates an error response for when cache is not found (fresh start scenario).
    /// </summary>
    public static ApiErrorResponse CacheNotFound(
        string suburb, 
        string state, 
        CacheUpdateStatus? cacheStatus = null,
        bool updateTriggered = false)
    {
        var response = new ApiErrorResponse
        {
            ErrorCode = "CACHE_NOT_FOUND",
            ErrorType = "CacheError",
            Message = "No cached data found for this location. Cache update has been triggered in background.",
            Details = new Dictionary<string, object>
            {
                { "location", new { suburb, state } },
                { "cacheExists", cacheStatus?.CacheExists ?? false },
                { "cacheIsValid", cacheStatus?.CacheIsValid ?? false },
                { "updateTriggered", updateTriggered || (cacheStatus?.UpdateTriggered ?? false) }
            },
            Suggestions = new Dictionary<string, object>
            {
                { "action", "retry_after_seconds" },
                { "refreshEndpoint", $"/api/cache/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/refresh" },
                { "statusEndpoint", $"/api/cache/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/range" }
            }
        };

        // Determine meaningful retryAfter based on cache status
        int retryAfter;
        bool isUpdateInProgress = cacheStatus?.Message?.Contains("in progress") ?? false;
        
        if (cacheStatus != null)
        {
            if (cacheStatus.CacheExpiresAt.HasValue)
            {
                response.Details!["cacheExpiresAt"] = cacheStatus.CacheExpiresAt.Value;
            }
            if (cacheStatus.NextUpdateTime.HasValue)
            {
                response.Details!["nextUpdateTime"] = cacheStatus.NextUpdateTime.Value;
                
                // Calculate retryAfter from NextUpdateTime if available
                var secondsUntilUpdate = (int)(cacheStatus.NextUpdateTime.Value - DateTime.UtcNow).TotalSeconds;
                
                if (isUpdateInProgress)
                {
                    // Update is in progress - retry when it completes (typically ~2 minutes)
                    // Use NextUpdateTime if it's reasonable (1-3 minutes), otherwise default to 2 minutes
                    retryAfter = secondsUntilUpdate > 0 && secondsUntilUpdate <= 180 
                        ? secondsUntilUpdate 
                        : 120; // Default to 2 minutes for in-progress updates
                }
                else if (updateTriggered || cacheStatus.UpdateTriggered)
                {
                    // Update was just triggered - allow time for it to complete (60-90 seconds)
                    // Use NextUpdateTime if reasonable, otherwise default to 90 seconds
                    retryAfter = secondsUntilUpdate > 0 && secondsUntilUpdate <= 120 
                        ? secondsUntilUpdate 
                        : 90; // Default to 90 seconds for newly triggered updates
                }
                else
                {
                    // No update triggered yet - suggest waiting for background service check
                    // Cap at reasonable maximum (5 minutes)
                    retryAfter = secondsUntilUpdate > 0 && secondsUntilUpdate <= 300 
                        ? secondsUntilUpdate 
                        : 60; // Default to 60 seconds if NextUpdateTime is too far or in past
                }
            }
            else
            {
                // No NextUpdateTime available - use defaults based on scenario
                if (isUpdateInProgress)
                {
                    retryAfter = 120; // 2 minutes for in-progress updates
                }
                else if (updateTriggered || cacheStatus.UpdateTriggered)
                {
                    retryAfter = 90; // 90 seconds for newly triggered updates
                }
                else
                {
                    retryAfter = 60; // 60 seconds default
                }
            }
            
            response.Details!["statusMessage"] = cacheStatus.Message ?? "Unknown status";
            
            // Add update failure information if available
            if (cacheStatus.UpdateFailed)
            {
                response.Details!["previousUpdateFailed"] = true;
                if (cacheStatus.Error != null)
                {
                    response.Details!["previousError"] = cacheStatus.Error;
                }
                if (cacheStatus.ErrorCode != null)
                {
                    response.Details!["previousErrorCode"] = cacheStatus.ErrorCode;
                }
                // Suggest manual refresh if previous update failed
                response.Suggestions!["action"] = "manual_refresh_recommended";
            }
        }
        else
        {
            // No cache status - default to 90 seconds for fresh start
            retryAfter = 90;
        }
        
        // Ensure retryAfter is within reasonable bounds (30 seconds to 5 minutes)
        retryAfter = Math.Max(30, Math.Min(retryAfter, 300));
        response.Suggestions!["retryAfter"] = retryAfter;

        return response;
    }

    /// <summary>
    /// Creates an error response for validation errors.
    /// </summary>
    public static ApiErrorResponse ValidationError(string message, string? field = null)
    {
        var response = new ApiErrorResponse
        {
            ErrorCode = "VALIDATION_ERROR",
            ErrorType = "ValidationError",
            Message = message
        };

        if (!string.IsNullOrEmpty(field))
        {
            response.Details = new Dictionary<string, object> { { "field", field } };
        }

        return response;
    }

    /// <summary>
    /// Creates an error response for when cache update fails.
    /// </summary>
    public static ApiErrorResponse CacheUpdateFailed(string suburb, string state, string reason, Exception? exception = null)
    {
        var response = new ApiErrorResponse
        {
            ErrorCode = "CACHE_UPDATE_FAILED",
            ErrorType = "ServiceError",
            Message = $"Failed to update cache for {suburb}, {state}: {reason}",
            Details = new Dictionary<string, object>
            {
                { "location", new { suburb, state } },
                { "reason", reason }
            },
            Suggestions = new Dictionary<string, object>
            {
                { "action", "retry_after_seconds" },
                { "retryAfter", 60 }, // Wait 1 minute before retrying failed update
                { "refreshEndpoint", $"/api/cache/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/refresh" },
                { "statusEndpoint", $"/api/cache/{Uri.EscapeDataString(suburb)}/{Uri.EscapeDataString(state)}/range" }
            }
        };

        if (exception != null)
        {
            response.Details["exceptionType"] = exception.GetType().Name;
            response.Details["exceptionMessage"] = exception.Message;
            
            // Adjust retry suggestion based on exception type
            if (exception is TimeoutException || exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                response.Suggestions!["retryAfter"] = 30; // Shorter wait for timeouts
                response.Suggestions!["action"] = "retry_soon";
            }
            else if (exception.Message.Contains("network", StringComparison.OrdinalIgnoreCase) || 
                     exception.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
            {
                response.Suggestions!["retryAfter"] = 120; // Longer wait for network issues
                response.Suggestions!["action"] = "check_network_and_retry";
            }
        }

        return response;
    }

    /// <summary>
    /// Creates an error response for internal server errors.
    /// </summary>
    public static ApiErrorResponse InternalError(string message, Exception? exception = null)
    {
        var response = new ApiErrorResponse
        {
            ErrorCode = "INTERNAL_ERROR",
            ErrorType = "ServiceError",
            Message = message
        };

        if (exception != null)
        {
            response.Details = new Dictionary<string, object>
            {
                { "exceptionType", exception.GetType().Name },
                { "exceptionMessage", exception.Message }
            };
        }

        return response;
    }

    /// <summary>
    /// Creates an error response for when a specific resource is not found.
    /// </summary>
    public static ApiErrorResponse NotFound(string resourceType, string identifier, string? suggestion = null)
    {
        var response = new ApiErrorResponse
        {
            ErrorCode = "NOT_FOUND",
            ErrorType = "NotFoundError",
            Message = $"{resourceType} not found: {identifier}",
            Details = new Dictionary<string, object>
            {
                { "resourceType", resourceType },
                { "identifier", identifier }
            }
        };

        if (!string.IsNullOrEmpty(suggestion))
        {
            response.Suggestions = new Dictionary<string, object> { { "suggestion", suggestion } };
        }

        return response;
    }

    /// <summary>
    /// Creates an error response for time range validation errors.
    /// </summary>
    public static ApiErrorResponse TimeRangeError(string message, object? availableRange = null, object? requestedRange = null)
    {
        var response = new ApiErrorResponse
        {
            ErrorCode = "TIME_RANGE_ERROR",
            ErrorType = "ValidationError",
            Message = message,
            Details = new Dictionary<string, object>(),
            Suggestions = new Dictionary<string, object>
            {
                { "action", "adjust_time_range" }
            }
        };

        if (availableRange != null)
        {
            response.Details["availableRange"] = availableRange;
            
            // Add suggestion to use available range if provided
            // Try to extract oldest/newest from the availableRange object using reflection
            try
            {
                var rangeType = availableRange.GetType();
                var oldestProp = rangeType.GetProperty("oldest");
                var newestProp = rangeType.GetProperty("newest");
                
                if (oldestProp != null && newestProp != null)
                {
                    var oldest = oldestProp.GetValue(availableRange);
                    var newest = newestProp.GetValue(availableRange);
                    
                    if (oldest != null && newest != null)
                    {
                        response.Suggestions!["suggestedRange"] = new
                        {
                            start = oldest,
                            end = newest
                        };
                        response.Suggestions!["suggestion"] = $"Try querying data between {oldest} and {newest}";
                    }
                }
            }
            catch
            {
                // Ignore if we can't extract range info
            }
        }

        if (requestedRange != null)
        {
            response.Details["requestedRange"] = requestedRange;
        }

        return response;
    }
}

