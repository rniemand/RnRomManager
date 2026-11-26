import type { BundleEntry } from './BundleEntry.js';
import type { RunItemState } from '#lib/enums/index.js';

export interface RunItem {
	key: string;
	entry: BundleEntry;
	state: RunItemState;
	/** The server's failure code (`Unavailable`, `OpenFailed`, `Aborted`, `Stalled`, `IoError`) for `failed`. */
	errorCode: string | null;
	/** The current ticket while one is open, so polling resumes on it after a lost status response (B8). */
	ticket: string | null;
}
