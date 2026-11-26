import type { ZipEntry } from './ZipEntry.js';

/** A folder of a library ZIP (Z4): `path` is `''` for the root, else `a/b` (no trailing slash). */
export interface ZipFolder {
	name: string;
	path: string;
	folders: ZipFolder[];
	files: { name: string; entry: ZipEntry }[];
}
