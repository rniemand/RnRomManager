import type { ZipFolder } from './ZipFolder.js';

export interface ZipTree {
	root: ZipFolder;
	/** Every folder by path, the root included. */
	folders: Map<string, ZipFolder>;
	fileCount: number;
	totalSize: number;
	totalCompressed: number;
}
