export interface RequestOptions {
	/** JSON-serialised as the body. */
	body?: unknown;
	/** Don't redirect on 401 (the login form and the boot-time `me` check handle it themselves). */
	noRedirect?: boolean;
	signal?: AbortSignal;
	/** Return the response body as text instead of parsing it (GP4: an info.json may not be valid JSON). */
	raw?: boolean;
}
