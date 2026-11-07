import { ApiError, type ProblemDetails } from './errors';

/**
 * The one fetch wrapper (spec §9.2, §11, §23):
 * - calls `GET /api/auth/antiforgery` once, then sends the `XSRF-TOKEN` cookie's value as `X-XSRF-TOKEN` on every
 *   mutation (the cookie is re-read each time: login, logout and change-password re-issue it);
 * - `401` → `/login?returnUrl=…`, `403 PasswordChangeRequired` → `/change-password`;
 * - any other failure → a typed {@link ApiError} built from the ProblemDetails body.
 */

export type Navigate = (url: string) => void | Promise<void>;

export interface RequestOptions {
	/** JSON-serialised as the body. */
	body?: unknown;
	/** Don't redirect on 401 (the login form and the boot-time `me` check handle it themselves). */
	noRedirect?: boolean;
	signal?: AbortSignal;
	/** Return the response body as text instead of parsing it (GP4: an info.json may not be valid JSON). */
	raw?: boolean;
}

const mutations = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

let navigate: Navigate = (url) => window.location.assign(url);
let antiforgery: Promise<void> | null = null;

/** Set by the root layout to SvelteKit's `goto`; tests pass a spy. */
export function configureClient(options: { navigate: Navigate }): void {
	navigate = options.navigate;
}

/** Forget the antiforgery call (tests). */
export function resetClient(): void {
	antiforgery = null;
}

function ensureAntiforgery(): Promise<void> {
	antiforgery ??= fetch('/api/auth/antiforgery', { credentials: 'same-origin' }).then(
		(response) => {
			if (!response.ok) throw new ApiError(response.status);
		},
		() => {
			throw new ApiError(0);
		}
	);
	// A failed call is retried on the next mutation.
	antiforgery.catch(() => (antiforgery = null));
	return antiforgery;
}

function xsrfToken(): string | null {
	const cookie = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN='));
	return cookie ? decodeURIComponent(cookie.slice('XSRF-TOKEN='.length)) : null;
}

function currentLocation(): string {
	return window.location.pathname + window.location.search;
}

async function problemOf(response: Response): Promise<ProblemDetails> {
	try {
		return (await response.json()) as ProblemDetails;
	} catch {
		return {};
	}
}

/** Sends one request; resolves with the parsed JSON (or `undefined` for 204 / empty bodies). */
export async function request<T>(
	method: string,
	url: string,
	options: RequestOptions = {}
): Promise<T> {
	const headers: Record<string, string> = { Accept: 'application/json' };
	// FormData (image upload) is sent as-is: the browser sets the multipart Content-Type with its boundary.
	const isForm = options.body instanceof FormData;
	if (options.body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
	if (mutations.has(method)) {
		await ensureAntiforgery();
		const token = xsrfToken();
		if (token) headers['X-XSRF-TOKEN'] = token;
	}

	let response: Response;
	try {
		response = await fetch(url, {
			method,
			headers,
			credentials: 'same-origin',
			body:
				options.body === undefined
					? undefined
					: isForm
						? (options.body as FormData)
						: JSON.stringify(options.body),
			signal: options.signal
		});
	} catch (error) {
		if (error instanceof DOMException && error.name === 'AbortError') throw error;
		throw new ApiError(0);
	}

	if (response.ok) {
		const text = response.status === 204 ? '' : await response.text();
		if (options.raw) return text as T;
		return (text ? JSON.parse(text) : undefined) as T;
	}

	const error = new ApiError(response.status, await problemOf(response));
	if (response.status === 401 && !options.noRedirect) {
		await navigate(`/login?returnUrl=${encodeURIComponent(currentLocation())}`);
	} else if (
		response.status === 403 &&
		error.code === 'PasswordChangeRequired' &&
		// Already there: navigating again would re-run the page's loads and loop.
		window.location.pathname !== '/change-password'
	) {
		await navigate('/change-password');
	}
	throw error;
}

export const api = {
	get: <T>(url: string, options?: RequestOptions) => request<T>('GET', url, options),
	getText: (url: string, options?: RequestOptions) =>
		request<string>('GET', url, { ...options, raw: true }),
	post: <T>(url: string, body?: unknown, options?: RequestOptions) =>
		request<T>('POST', url, { ...options, body }),
	put: <T>(url: string, body?: unknown, options?: RequestOptions) =>
		request<T>('PUT', url, { ...options, body }),
	patch: <T>(url: string, body?: unknown, options?: RequestOptions) =>
		request<T>('PATCH', url, { ...options, body }),
	delete: <T>(url: string, options?: RequestOptions) => request<T>('DELETE', url, options)
};
