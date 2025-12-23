// Slideshow controls (play/pause)
import { state } from './state.js';
import { showFrame } from './frame-navigation.js';

/**
 * Play slideshow
 */
export function play() {
    if (state.frames.length === 0) return;
    
    state.isPlaying = true;
    const playBtn = document.getElementById('play-btn');
    if (playBtn) playBtn.textContent = '⏸ Pause';
    
    if (state.playInterval) {
        clearInterval(state.playInterval);
    }
    
    state.playInterval = setInterval(() => {
        state.currentFrameIndex = (state.currentFrameIndex + 1) % state.frames.length;
        showFrame(state.currentFrameIndex);
    }, state.settings.frameInterval * 1000);
}

/**
 * Pause slideshow
 */
export function pause() {
    state.isPlaying = false;
    const playBtn = document.getElementById('play-btn');
    if (playBtn) playBtn.textContent = '▶ Play';
    
    if (state.playInterval) {
        clearInterval(state.playInterval);
        state.playInterval = null;
    }
}

/**
 * Toggle play/pause
 */
export function togglePlay() {
    if (state.isPlaying) {
        pause();
    } else {
        play();
    }
}

/**
 * Go to previous frame
 */
export function previousFrame() {
    pause();
    const newIndex = state.currentFrameIndex > 0 ? state.currentFrameIndex - 1 : state.frames.length - 1;
    showFrame(newIndex);
}

/**
 * Go to next frame
 */
export function nextFrame() {
    pause();
    const newIndex = (state.currentFrameIndex + 1) % state.frames.length;
    showFrame(newIndex);
}

