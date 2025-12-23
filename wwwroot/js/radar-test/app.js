// Main application entry point
import { loadSettings, saveSettings, updateSettingsUI, applySettings } from './settings.js';
import { fetchRadarData } from './api-client.js';
import { updateUI, updateRelativeTimes } from './ui-updater.js';
import { showError, showNoFramesMessage, clearError } from './ui-updater.js';
import { handleApiDown, resetApiRetryState } from './error-handler.js';
import { buildFrameControls, showFrame, findFrameToShowAfterRefresh } from './frame-navigation.js';
import { play, pause, togglePlay, previousFrame, nextFrame } from './slideshow.js';
import { setupKeyboardNavigation } from './keyboard.js';
import { state } from './state.js';
import { isNetworkError } from './utils.js';

/**
 * Settings modal management
 */
function showSettings() {
    const modal = document.getElementById('settings-modal');
    if (modal) modal.style.display = 'flex';
}

function hideSettings() {
    const modal = document.getElementById('settings-modal');
    if (modal) modal.style.display = 'none';
}

/**
 * Auto-refresh data
 */
async function refreshData() {
    // Don't clear error if API is down (keep retry message visible)
    if (!state.isApiDown) {
        clearError();
    }
    
    const result = await fetchRadarData();
    
    if (result.error) {
        if (result.error === 'network') {
            handleApiDown(result.originalError || new Error('Network error'));
            return;
        }
        
        // Handle other errors
        if (result.error === 'cache_generating') {
            updateUI(result.cacheStatus);
            showNoFramesMessage(result.message, result.refreshEndpoint);
        } else if (result.error === 'location_missing' || result.error === 'no_cache_data' || result.error === 'no_frames') {
            if (result.cacheStatus) {
                updateUI(result.cacheStatus);
            }
            showNoFramesMessage(result.message, result.refreshEndpoint);
        } else {
            showError(result.message || 'An error occurred');
        }
        return;
    }
    
    if (result.data) {
        state.lastRefreshTime = new Date();
        resetApiRetryState();
        updateUI(result.data);
        
        // Build frame controls after UI update
        buildFrameControls();
        
        // Find and show appropriate frame
        const frameToShow = findFrameToShowAfterRefresh();
        showFrame(frameToShow);
        
        // Restart auto-refresh if it was stopped
        if (!state.refreshInterval && !state.isApiDown) {
            state.refreshInterval = setInterval(refreshData, state.settings.refreshInterval * 1000);
        }
        
        // Restart play if it was playing before refresh
        const wasPlaying = state.isPlaying;
        if (wasPlaying && result.data.frames && result.data.frames.length > 0) {
            setTimeout(() => {
                if (!state.isPlaying) {
                    play();
                }
            }, 100);
        } else if (state.settings.autoPlay && !state.isPlaying && result.data.frames && result.data.frames.length > 0) {
            // Auto-play on initial load only
            setTimeout(() => play(), 500);
        }
    }
}

/**
 * Initialize application
 */
async function init() {
    // Load settings from cookies
    loadSettings();
    
    // Set up event listeners
    const playBtn = document.getElementById('play-btn');
    const prevBtn = document.getElementById('prev-btn');
    const nextBtn = document.getElementById('next-btn');
    
    if (playBtn) playBtn.addEventListener('click', togglePlay);
    if (prevBtn) prevBtn.addEventListener('click', previousFrame);
    if (nextBtn) nextBtn.addEventListener('click', nextFrame);
    
    // Settings button
    const settingsBtn = document.getElementById('settings-btn-header');
    if (settingsBtn) {
        settingsBtn.addEventListener('click', showSettings);
    }
    
    // Settings modal handlers
    const saveSettingsBtn = document.getElementById('save-settings-btn');
    if (saveSettingsBtn) {
        saveSettingsBtn.addEventListener('click', () => {
            const frameIntervalValue = parseFloat(document.getElementById('frame-interval-input').value);
            state.settings.frameInterval = (frameIntervalValue > 0) ? frameIntervalValue : 2.0;
            
            const refreshIntervalValue = parseInt(document.getElementById('refresh-interval-input').value);
            state.settings.refreshInterval = (refreshIntervalValue >= 5) ? refreshIntervalValue : 30;
            state.settings.autoPlay = document.getElementById('auto-play-input').checked;
            state.settings.timespan = document.getElementById('timespan-select').value;
            
            if (state.settings.timespan === 'custom') {
                const startInput = document.getElementById('start-time-input');
                const endInput = document.getElementById('end-time-input');
                if (startInput && startInput.value) {
                    state.settings.customStartTime = startInput.value;
                }
                if (endInput && endInput.value) {
                    state.settings.customEndTime = endInput.value;
                }
            }
            
            saveSettings();
            hideSettings();
            
            // Apply settings and restart refresh interval
            applySettings();
            if (state.refreshInterval) {
                clearInterval(state.refreshInterval);
                state.refreshInterval = null;
            }
            
            // Reload data with new timespan
            refreshData();
        });
    }
    
    // Timespan select change handler
    const timespanSelect = document.getElementById('timespan-select');
    if (timespanSelect) {
        timespanSelect.addEventListener('change', (e) => {
            state.settings.timespan = e.target.value;
            const customSection = document.getElementById('custom-range-section');
            if (customSection) {
                customSection.style.display = e.target.value === 'custom' ? 'block' : 'none';
            }
        });
    }
    
    // Cancel settings button
    const cancelSettingsBtn = document.getElementById('cancel-settings-btn');
    if (cancelSettingsBtn) {
        cancelSettingsBtn.addEventListener('click', () => {
            updateSettingsUI(); // Reset to current settings
            hideSettings();
        });
    }
    
    // Close button handler
    const closeSettingsBtn = document.getElementById('close-settings-btn');
    if (closeSettingsBtn) {
        closeSettingsBtn.addEventListener('click', hideSettings);
    }
    
    // Close modal on background click
    const settingsModal = document.getElementById('settings-modal');
    if (settingsModal) {
        settingsModal.addEventListener('click', (e) => {
            if (e.target.id === 'settings-modal') {
                hideSettings();
            }
        });
    }
    
    // Setup keyboard navigation
    setupKeyboardNavigation();
    
    // Initial load
    await refreshData();
    
    // Auto-refresh based on settings (only if API is up)
    if (!state.isApiDown) {
        state.refreshInterval = setInterval(refreshData, state.settings.refreshInterval * 1000);
    }
    
    // Update relative times every second
    setInterval(updateRelativeTimes, 1000);
}

// Start when page loads
init();
