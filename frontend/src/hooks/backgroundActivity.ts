/**
 * Module-level counter of active background jobs.
 *
 * Components that run long-running backend work (e.g. cash flow capture,
 * OCR processing, validation) should call registerBackgroundJob() when the
 * job starts and unregisterBackgroundJob() when it finishes (or on unmount).
 *
 * useAcumaticaSession reads hasBackgroundActivity() to decide whether to
 * suppress the inactivity-timeout logout and continue sending keepalives,
 * even when the user has not moved the mouse or typed anything.
 */

let _count = 0;

export const registerBackgroundJob = (): void => {
  _count++;
};

export const unregisterBackgroundJob = (): void => {
  _count = Math.max(0, _count - 1);
};

export const hasBackgroundActivity = (): boolean => _count > 0;
