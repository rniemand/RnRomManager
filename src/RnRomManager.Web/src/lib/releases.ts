import type { RomDetail } from '#lib/api/types.js';

type UpdateFields = Pick<RomDetail, 'version' | 'revision' | 'dateAdded' | 'fileName'>;

/** B9: an update is a release flagged `Update`. */
export const isUpdate = (rom: Pick<RomDetail, 'flags'>): boolean => rom.flags.includes('Update');

/** `1.10` → [1, 10]. */
const segments = (version: string): number[] => version.split('.').map(Number);

/** The N of a `v<N>` revision; none → 0. */
const revisionNumber = (revision: string | null): number => {
	const match = /^v(\d+)$/.exec(revision ?? '');
	return match ? Number(match[1]) : 0;
};

/**
 * B11: newest update first. First difference wins: version (numeric segments, missing = 0; a version before none),
 * then the `v<N>` revision, then dateAdded (newer first), then fileName (ordinal ascending).
 */
export function compareUpdates(a: UpdateFields, b: UpdateFields): number {
	if (a.version !== null && b.version === null) return -1;
	if (a.version === null && b.version !== null) return 1;
	if (a.version !== null && b.version !== null) {
		const [x, y] = [segments(a.version), segments(b.version)];
		for (let i = 0; i < Math.max(x.length, y.length); i++) {
			const diff = (y[i] ?? 0) - (x[i] ?? 0);
			if (diff !== 0) return diff;
		}
	}
	const revision = revisionNumber(b.revision) - revisionNumber(a.revision);
	if (revision !== 0) return revision;
	const added = Date.parse(b.dateAdded) - Date.parse(a.dateAdded);
	if (added !== 0) return added;
	return a.fileName < b.fileName ? -1 : a.fileName > b.fileName ? 1 : 0;
}
