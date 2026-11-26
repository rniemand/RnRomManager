import type { ReleaseFile } from './ReleaseFile.js';

export interface RomDetail {
	id: number;
	fileName: string;
	originalFileName: string;
	baseTitle: string;
	regions: string[];
	languages: string[];
	revision: string | null;
	/** An update's dotted version (`1.0.1`); null otherwise. */
	version: string | null;
	flags: string[];
	isPrimary: boolean;
	identificationMethod: 'Dat' | 'Manual' | 'TitleDb' | 'Serial';
	datName: string | null;
	datVersion: string | null;
	datGameName: string | null;
	/** Handler-specific (§8.2); null when unreadable. */
	header: Record<string, unknown> | null;
	sizeBytes: number;
	crc32: string;
	md5: string;
	sha1: string;
	dateAdded: string;
	dateModified: string;
	downloadUrl: string;
	/** B3: the ZIP holds `notes/<fileName>.md`. */
	hasNote: boolean;
	/** G7: the file next to the ZIP for an external system's release; null when the ROM is in the ZIP. */
	externalFileName: string | null;
	/** B20: a disc set's other files (cue, other tracks) in order; null for a release without. */
	files: ReleaseFile[] | null;
	/** B20: `sizeBytes` plus every `files` entry. */
	totalSizeBytes: number;
	/** Multi-disc M3: on a PlayStation disc set's Disc 1, its playlist name and release ids in disc order; null otherwise. */
	discSet: { name: string; romIds: number[] } | null;
}
