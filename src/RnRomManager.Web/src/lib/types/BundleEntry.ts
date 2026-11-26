import type { BundleMember } from './BundleMember.js';

/** Bundle Creator B3/B4: one cart or queue entry, a stable identity plus its last-known labels. */
export interface BundleEntry {
	gameId: string;
	releaseSha1: string;
	member: BundleMember | null;
	gameTitle: string;
	fileName: string;
}
