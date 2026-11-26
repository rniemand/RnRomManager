import type { BiosFileStatus } from './BiosFileStatus.js';

/** One system's BIOS needs in `GET /api/admin/bios`. */
export interface BiosGroup {
	system: string;
	need: 'All' | 'OneOf' | 'Optional';
	ok: boolean;
	note: string;
	files: BiosFileStatus[];
	coreSetting: string | null;
	coreEnv: string | null;
	coreConfigured: boolean | null;
}
