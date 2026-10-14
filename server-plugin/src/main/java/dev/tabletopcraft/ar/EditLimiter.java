package dev.tabletopcraft.ar;

/**
 * Per-session rate limit for block_edit (config edit.max-per-second): a token bucket holding one second's worth of
 * edits, refilled continuously. Checked on the websocket thread when a message arrives, so a flood never reaches the
 * main thread's queue.
 */
public final class EditLimiter {
    private final double perSecond, capacity;
    private double tokens;
    private long lastNanos = Long.MIN_VALUE;

    public EditLimiter(double perSecond) {
        this.perSecond = Math.max(0.1, perSecond);
        this.capacity = Math.max(1, this.perSecond);
        this.tokens = capacity;
    }

    /** True if one more edit is allowed at {@code nowNanos} (System.nanoTime), and takes it. */
    public synchronized boolean tryAcquire(long nowNanos) {
        if (lastNanos != Long.MIN_VALUE) tokens = Math.min(capacity, tokens + (nowNanos - lastNanos) / 1e9 * perSecond);
        lastNanos = nowNanos;
        if (tokens < 1) return false;
        tokens -= 1;
        return true;
    }
}
