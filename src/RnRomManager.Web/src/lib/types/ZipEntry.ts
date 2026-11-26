/** One file entry of a library ZIP (Z2); sizes from the central directory. */
export interface ZipEntry {
	path: string;
	sizeBytes: number;
	compressedSizeBytes: number;
	modifiedUtc: string;
}
