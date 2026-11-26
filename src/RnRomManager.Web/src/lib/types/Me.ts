import type { Role } from '#lib/enums/index.js';

/** `GET /api/auth/me` (spec §23). */
export interface Me {
	id: number;
	username: string;
	roles: Role[];
	mustChangePassword: boolean;
}
