/** One file of `GET /api/admin/bios`. */
export interface BiosFileStatus {
	name: string;
	minBytes: number;
	maxBytes: number;
	status: 'Valid' | 'Missing' | 'WrongSize' | 'Unsafe' | 'Unreadable';
	size: number | null;
	md5: string | null;
}
