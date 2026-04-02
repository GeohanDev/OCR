import { useEffect, useRef, useCallback } from 'react';
import { apiClient } from '../api/client';
import { hasBackgroundActivity } from './backgroundActivity';

// How often to call keepalive when the user is active (2 minutes)
const KEEPALIVE_INTERVAL_MS = 2 * 60 * 1000;
// After this much idle time, proactively end the Acumatica session (10 minutes)
const INACTIVITY_TIMEOUT_MS = 10 * 60 * 1000;
// Polling interval for the inactivity check (every 30 seconds)
const CHECK_INTERVAL_MS = 30 * 1000;

export type SessionExpiredReason = 'inactivity' | 'token_expired';

interface UseAcumaticaSessionOptions {
  onSessionExpired: (reason: SessionExpiredReason) => void;
}

/**
 * Manages the Acumatica session lifecycle while the user is logged in:
 * - Tracks user activity (mouse, keyboard, touch, scroll).
 * - Calls /auth/acumatica/keepalive every 2 minutes when active.
 * - After 10 minutes of frontend inactivity, proactively clears the Acumatica
 *   token and notifies the caller (which shows the session-expired banner).
 * - If the keepalive returns 424 (token genuinely expired), also notifies.
 *
 * Only runs when an acumatica_token is present in sessionStorage.
 */
export function useAcumaticaSession({ onSessionExpired }: UseAcumaticaSessionOptions) {
  const lastActivityRef = useRef<number>(Date.now());
  const lastKeepaliveRef = useRef<number>(0);
  const expiredRef = useRef<boolean>(false);

  const recordActivity = useCallback(() => {
    lastActivityRef.current = Date.now();
  }, []);

  const callKeepalive = useCallback(async () => {
    const token = sessionStorage.getItem('acumatica_token');
    if (!token || expiredRef.current) return;

    try {
      await apiClient.get('/auth/acumatica/keepalive', {
        headers: { 'X-Acumatica-Token': token },
      });
      lastKeepaliveRef.current = Date.now();
    } catch (err: unknown) {
      const status = (err as { response?: { status?: number } })?.response?.status;
      if (status === 424) {
        expiredRef.current = true;
        sessionStorage.removeItem('acumatica_token');
        onSessionExpired('token_expired');
      }
      // Other errors (network, 5xx) are non-fatal — session remains active.
    }
  }, [onSessionExpired]);

  useEffect(() => {
    const acuToken = sessionStorage.getItem('acumatica_token');
    if (!acuToken) return; // No Acumatica token — nothing to manage.

    // ── Activity event listeners ──────────────────────────────────────────────
    const events = ['mousemove', 'mousedown', 'keydown', 'touchstart', 'scroll'];
    events.forEach(ev => document.addEventListener(ev, recordActivity, { passive: true }));

    // ── Interval: inactivity check + keepalive ────────────────────────────────
    const timer = setInterval(async () => {
      if (expiredRef.current) return;

      const token = sessionStorage.getItem('acumatica_token');
      if (!token) return; // Token was cleared externally (e.g. logout).

      const now = Date.now();
      const idleMs = now - lastActivityRef.current;
      const backgroundRunning = hasBackgroundActivity();

      // Suppress inactivity logout while a background job is active — the user
      // may be idle in the browser but work is still happening on their behalf.
      if (idleMs >= INACTIVITY_TIMEOUT_MS && !backgroundRunning) {
        expiredRef.current = true;
        sessionStorage.removeItem('acumatica_token');
        onSessionExpired('inactivity');
        return;
      }

      // Send keepalive whenever the user is active OR a background job is running.
      const sinceKeepalive = now - lastKeepaliveRef.current;
      if (sinceKeepalive >= KEEPALIVE_INTERVAL_MS && (idleMs < INACTIVITY_TIMEOUT_MS || backgroundRunning)) {
        await callKeepalive();
      }
    }, CHECK_INTERVAL_MS);

    // Initial keepalive to start the backend session window immediately.
    callKeepalive();

    return () => {
      clearInterval(timer);
      events.forEach(ev => document.removeEventListener(ev, recordActivity));
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // Run once on mount — stable refs mean no re-runs needed.
}
