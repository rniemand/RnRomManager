import type { BundleTicketState } from '#lib/enums/index.js';

/** `GET /api/bundle-downloads/{ticket}` (B7). */
export interface BundleTicketStatus {
	state: BundleTicketState;
	errorCode: string | null;
}
