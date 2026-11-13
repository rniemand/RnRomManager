import type { RomDetail } from '#lib/api/types.js';

type UpdateFields = Pick<RomDetail, 'version' | 'revision' | 'dateAdded' | 'fileName'>;

/** B9: an update is a release flagged `Update`. */
export const isUpdate = (rom: Pick<RomDetail, 'flags'>): boolean => rom.flags.includes('Update');

/** B10: the flag a disc number becomes (`Disc 2`); the case and leading zeros don't matter. */
const discFlag = /^disc (\d+)$/i;

/** B18: the release's disc number from its first `Disc <n>` flag; null without one. */
export function discNumber(rom: Pick<RomDetail, 'flags'>): number | null {
	for (const flag of rom.flags) {
		const match = discFlag.exec(flag);
		if (match) return Number(match[1]);
	}
	return null;
}

/**
 * B18: with two or more disc releases, releases without a disc come first in their given order, then by disc
 * number, ties in their given order; otherwise the given order is kept.
 */
export function orderByDisc<T extends Pick<RomDetail, 'flags'>>(roms: T[]): T[] {
	if (roms.filter((r) => discNumber(r) !== null).length < 2) return roms;
	// Array.prototype.sort is stable; no disc sorts as 0, before disc 1.
	return [...roms].sort((a, b) => (discNumber(a) ?? 0) - (discNumber(b) ?? 0));
}

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
