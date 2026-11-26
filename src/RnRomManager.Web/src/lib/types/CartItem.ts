import type { BundleEntry } from './BundleEntry.js';

/** A cart row: the entry, its availability after the last refresh and the names to show. */
export interface CartItem {
	key: string;
	entry: BundleEntry;
	/** `unknown` until a refresh answers for this entry. */
	status: 'unknown' | 'available' | 'unavailable';
	/** The current names when available, else the last-known labels. */
	gameTitle: string;
	fileName: string;
	downloadUrl: string | null;
}
