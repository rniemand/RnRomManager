import type { BiosGroup } from './BiosGroup.js';

/** `GET /api/admin/bios`: the BIOS info tab. */
export interface BiosInfo {
	folder: string;
	setting: string;
	env: string;
	folderExists: boolean;
	groups: BiosGroup[];
	otherFiles: { name: string; size: number }[];
}
