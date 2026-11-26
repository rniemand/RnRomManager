/** B20: one file of a disc set, besides the release's main file. */
export interface ReleaseFile {
	id: number;
	fileName: string;
	sizeBytes: number;
	crc32: string;
	md5: string;
	sha1: string;
	downloadUrl: string;
}
