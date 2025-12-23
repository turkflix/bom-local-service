// Keyboard navigation
import { state } from './state.js';
import { showFrame, jumpFrame, getJumpAmount } from './frame-navigation.js';
import { togglePlay } from './slideshow.js';

/**
 * Setup keyboard navigation
 */
export function setupKeyboardNavigation() {
    document.addEventListener('keydown', (e) => {
        // Don't handle keyboard shortcuts if user is typing in an input
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') {
            return;
        }
        
        // Arrow keys for frame navigation
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
            e.preventDefault();
            if (e.key === 'ArrowLeft') {
                if (e.shiftKey) {
                    // Shift+Left: jump back by proportional amount (only in extended mode)
                    const jumpAmount = getJumpAmount();
                    if (jumpAmount > 0) {
                        jumpFrame(-jumpAmount);
                    }
                } else {
                    showFrame(Math.max(0, state.currentFrameIndex - 1)); // Left: previous frame
                }
            } else {
                if (e.shiftKey) {
                    // Shift+Right: jump forward by proportional amount (only in extended mode)
                    const jumpAmount = getJumpAmount();
                    if (jumpAmount > 0) {
                        jumpFrame(jumpAmount);
                    }
                } else {
                    showFrame(Math.min(state.frames.length - 1, state.currentFrameIndex + 1)); // Right: next frame
                }
            }
        }
        
        // Home/End for first/last frame
        if (e.key === 'Home') {
            e.preventDefault();
            showFrame(0);
        }
        if (e.key === 'End') {
            e.preventDefault();
            showFrame(state.frames.length - 1);
        }
        
        // Spacebar for play/pause
        if (e.key === ' ' && state.frames.length > 0) {
            e.preventDefault();
            togglePlay();
        }
    });
}

