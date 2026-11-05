import { goto } from '$app/navigation';

/**
 * Client-side redirect to an app path that may carry a query (guards, 401 → login, returnUrl). The app has no base
 * path, so these paths need no `resolve()`; they're built at runtime, which the lint rule can't check.
 */
export function navigate(path: string): Promise<void> {
	// eslint-disable-next-line svelte/no-navigation-without-resolve -- runtime-built app path, no base path configured
	return goto(path, { replaceState: true });
}
