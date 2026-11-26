import type { BundleDownloadTicket } from './BundleDownloadTicket.js';
import type { BundleTicketStatus } from './BundleTicketStatus.js';
import type { BundleIdentity } from './BundleIdentity.js';

/** What the runner needs from the outside world, so tests can fake the server, the clock and the click. */
export interface DownloadDeps {
	issue(identity: BundleIdentity): Promise<BundleDownloadTicket>;
	status(ticket: string): Promise<BundleTicketStatus>;
	/** Hands one same-origin URL to the browser's Downloads flow. */
	start(url: string): void;
	wait(ms: number): Promise<void>;
}
