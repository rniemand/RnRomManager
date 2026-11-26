/** `idle` before the first run; `ended` after sign-out or leaving the page. */
export type RunStatus = 'idle' | 'running' | 'paused' | 'stopped' | 'finished' | 'ended';
