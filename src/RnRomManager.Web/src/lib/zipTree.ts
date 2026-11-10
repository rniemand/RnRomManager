import type { ZipEntry } from './api/types';

/** A folder of a library ZIP (Z4): `path` is `''` for the root, else `a/b` (no trailing slash). */
export interface ZipFolder {
	name: string;
	path: string;
	folders: ZipFolder[];
	files: { name: string; entry: ZipEntry }[];
}

export interface ZipTree {
	root: ZipFolder;
	/** Every folder by path, the root included. */
	folders: Map<string, ZipFolder>;
	fileCount: number;
	totalSize: number;
	totalCompressed: number;
}

const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

/** Z4: folders implied by the entry paths, folders and files each sorted by name ordinally, with whole-ZIP totals. */
export function buildTree(entries: ZipEntry[]): ZipTree {
	const root: ZipFolder = { name: '', path: '', folders: [], files: [] };
	const folders = new Map<string, ZipFolder>([['', root]]);
	let totalSize = 0;
	let totalCompressed = 0;
	for (const entry of entries) {
		const parts = entry.path.split('/');
		let folder = root;
		for (const name of parts.slice(0, -1)) {
			const path = folder.path ? `${folder.path}/${name}` : name;
			let child = folders.get(path);
			if (!child) {
				child = { name, path, folders: [], files: [] };
				folders.set(path, child);
				folder.folders.push(child);
			}
			folder = child;
		}
		folder.files.push({ name: parts[parts.length - 1], entry });
		totalSize += entry.sizeBytes;
		totalCompressed += entry.compressedSizeBytes;
	}
	for (const folder of folders.values()) {
		folder.folders.sort((a, b) => ordinal(a.name, b.name));
		folder.files.sort((a, b) => ordinal(a.name, b.name));
	}
	return { root, folders, fileCount: entries.length, totalSize, totalCompressed };
}

/** `a/b/c` → `['', 'a', 'a/b', 'a/b/c']`: the folder and every ancestor, root first. */
export function ancestors(path: string): string[] {
	const result = [''];
	if (path) path.split('/').forEach((_, i, parts) => result.push(parts.slice(0, i + 1).join('/')));
	return result;
}
