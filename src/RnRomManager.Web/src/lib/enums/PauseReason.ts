/**
 * Why a run waits for the user:
 * - `blocked`: a ticket expired before the browser fetched it (automatic downloads are likely blocked);
 * - `network`: a status or issue request got no answer; retry resumes on the same ticket;
 * - `signIn`: the session ended (401);
 * - `busy`: another download of this account is still running (another tab);
 * - `full`: the server has too many downloads in flight;
 * - `unknown`: the server lost the current ticket (restart); the user retries or skips that file.
 */
export type PauseReason = 'blocked' | 'network' | 'signIn' | 'busy' | 'full' | 'unknown';
