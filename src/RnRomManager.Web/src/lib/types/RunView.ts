import type { RunStatus, PauseReason } from '#lib/enums/index.js';
import type { RunItem } from './RunItem.js';

export interface RunView {
	status: RunStatus;
	pause: PauseReason | null;
	stopRequested: boolean;
	/** See {@link BundleDownloadRun.canRetry}. */
	canRetry: boolean;
	items: RunItem[];
}
