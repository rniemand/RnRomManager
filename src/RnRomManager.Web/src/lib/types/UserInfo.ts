import type { Role } from '#lib/enums/index.js';

/** `GET /api/users` (spec §23). */
export interface UserInfo {
	id: number;
	username: string;
	roles: Role[];
	isDisabled: boolean;
	lastLoginAt: string | null;
}
