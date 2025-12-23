// Error handling and API retry logic
import { MAX_RETRY_ATTEMPTS, RETRY_DELAY_MS } from './config.js';
import { state } from './state.js';
import { updateApiStatus, showError, clearError } from './ui-updater.js';
import { fetchRadarData } from './api-client.js';
import { updateUI } from './ui-updater.js';

/**
 * Reset API retry state when connection is restored
 */
export function resetApiRetryState() {
    if (state.isApiDown) {
        state.isApiDown = false;
        state.retryAttempts = 0;
        clearError();
        if (state.retryInterval) {
            clearInterval(state.retryInterval);
            state.retryInterval = null;
        }
        updateApiStatus('connected');
    }
}

/**
 * Handle API being down
 */
export function handleApiDown(error) {
    if (!state.isApiDown) {
        state.isApiDown = true;
        state.retryAttempts = 0;
        updateApiStatus('disconnected');
    }
    
    state.retryAttempts++;
    showError(`API connection lost. Retrying... (Attempt ${state.retryAttempts}/${MAX_RETRY_ATTEMPTS})`);
    
    // Stop auto-refresh while retrying
    if (state.refreshInterval) {
        clearInterval(state.refreshInterval);
        state.refreshInterval = null;
    }
    
    // Start retry mechanism
    if (!state.retryInterval && state.retryAttempts < MAX_RETRY_ATTEMPTS) {
        state.retryInterval = setInterval(async () => {
            if (state.retryAttempts >= MAX_RETRY_ATTEMPTS) {
                clearInterval(state.retryInterval);
                state.retryInterval = null;
                showError('API connection failed after multiple attempts. Please check if the service is running.');
                updateApiStatus('failed');
                return;
            }
            
            state.retryAttempts++;
            showError(`API connection lost. Retrying... (Attempt ${state.retryAttempts}/${MAX_RETRY_ATTEMPTS})`);
            
            // Try to fetch data
            const result = await fetchRadarData();
            if (result.data) {
                updateUI(result.data);
            }
        }, RETRY_DELAY_MS);
    } else if (state.retryAttempts >= MAX_RETRY_ATTEMPTS) {
        showError('API connection failed after multiple attempts. Please check if the service is running.');
        updateApiStatus('failed');
    }
}

