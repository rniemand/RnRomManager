import type { Me } from './api/types';

/** Routes reachable without a session. */
export const publicRoutes = ['/login'];

/**
 * Routing guards (spec §11): signed out → `/login?returnUrl=…`; `mustChangePassword` → `/change-password`.
 * Admin-only pages render "Not allowed" themselves (`AdminOnly`).
 * @returns where to redirect, or null to stay.
 */
export function guard(pathname: string, search: string, me: Me | null): string | null {
	if (publicRoutes.includes(pathname)) return null;
	if (!me) return `/login?returnUrl=${encodeURIComponent(pathname + search)}`;
	if (me.mustChangePassword && pathname !== '/change-password') return '/change-password';
	return null;
}

/**
 * A `returnUrl` that's safe to follow after login: same-origin app paths only (no `//host`, schemes or backslashes).
 */
export function safeReturnUrl(value: string | null): string {
	if (!value || !value.startsWith('/') || value.startsWith('//') || value.includes('\\'))
		return '/';
	return value;
}
