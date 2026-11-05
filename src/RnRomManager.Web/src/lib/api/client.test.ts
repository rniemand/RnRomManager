import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, configureClient, resetClient } from './client';
import { ApiError } from './errors';

type Call = { url: string; init?: RequestInit };

function json(status: number, body?: unknown): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { 'Content-Type': 'application/json' }
	});
}

describe('api client', () => {
	let calls: Call[];
	let responses: Response[];
	let navigate: ReturnType<typeof vi.fn<(url: string) => void>>;

	beforeEach(() => {
		calls = [];
		responses = [];
		navigate = vi.fn<(url: string) => void>();
		resetClient();
		configureClient({ navigate });
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			calls.push({ url, init });
			if (url === '/api/auth/antiforgery') {
				document.cookie = 'XSRF-TOKEN=tok%2Ben; path=/';
				return Promise.resolve(new Response(null, { status: 204 }));
			}
			return Promise.resolve(responses.shift() ?? json(200, {}));
		});
		window.history.replaceState(null, '', '/games/abc?tab=roms');
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
	});

	const headers = (call: Call) => call.init?.headers as Record<string, string>;

	it('GET sends no XSRF header and never calls antiforgery', async () => {
		responses.push(json(200, { id: 1 }));

		expect(await api.get('/api/auth/me')).toEqual({ id: 1 });
		expect(calls.map((c) => c.url)).toEqual(['/api/auth/me']);
		expect(headers(calls[0])['X-XSRF-TOKEN']).toBeUndefined();
	});

	it('mutations call antiforgery once, then send the cookie value as X-XSRF-TOKEN', async () => {
		await api.post('/api/jobs/ingest');
		await api.patch('/api/games/1', { title: 'x' });
		await api.delete('/api/games/1');

		expect(calls.map((c) => c.url)).toEqual([
			'/api/auth/antiforgery',
			'/api/jobs/ingest',
			'/api/games/1',
			'/api/games/1'
		]);
		for (const call of calls.slice(1)) expect(headers(call)['X-XSRF-TOKEN']).toBe('tok+en');
		expect(calls[2].init?.body).toBe('{"title":"x"}');
		expect(headers(calls[2])['Content-Type']).toBe('application/json');
	});

	it('re-reads the cookie on each mutation (login re-issues it)', async () => {
		await api.post('/api/auth/login', { username: 'a', password: 'b' });
		document.cookie = 'XSRF-TOKEN=second; path=/';
		await api.post('/api/auth/logout');

		expect(headers(calls[2])['X-XSRF-TOKEN']).toBe('second');
	});

	it('401 redirects to login with the current location as returnUrl', async () => {
		responses.push(json(401, { status: 401, code: 'Unauthorized', title: 'Not signed in' }));

		await expect(api.get('/api/games')).rejects.toMatchObject({
			status: 401,
			code: 'Unauthorized'
		});
		expect(navigate).toHaveBeenCalledWith('/login?returnUrl=%2Fgames%2Fabc%3Ftab%3Droms');
	});

	it('401 with noRedirect just throws', async () => {
		responses.push(
			json(401, { code: 'InvalidCredentials', title: 'Invalid username or password' })
		);

		await expect(api.post('/api/auth/login', {}, { noRedirect: true })).rejects.toMatchObject({
			code: 'InvalidCredentials'
		});
		expect(navigate).not.toHaveBeenCalled();
	});

	it('403 PasswordChangeRequired redirects to change-password; other 403s do not', async () => {
		responses.push(json(403, { code: 'PasswordChangeRequired', title: 'Change your password' }));
		await expect(api.get('/api/games')).rejects.toBeInstanceOf(ApiError);
		expect(navigate).toHaveBeenCalledWith('/change-password');

		navigate.mockClear();
		responses.push(json(403, { code: 'Forbidden', title: 'Not allowed' }));
		await expect(api.get('/api/users')).rejects.toMatchObject({ code: 'Forbidden' });
		expect(navigate).not.toHaveBeenCalled();
	});

	it('ProblemDetails become a typed ApiError', async () => {
		responses.push(
			json(400, {
				status: 400,
				title: 'One or more validation errors occurred.',
				code: 'ValidationFailed',
				traceId: '00-abc',
				errors: { title: ['Required'] }
			})
		);

		const error = (await api.patch('/api/games/1', {}).catch((e) => e)) as ApiError;

		expect(error).toBeInstanceOf(ApiError);
		expect(error).toMatchObject({
			status: 400,
			code: 'ValidationFailed',
			title: 'One or more validation errors occurred.',
			traceId: '00-abc',
			errors: { title: ['Required'] }
		});
	});

	it('a non-JSON error body still gives an ApiError', async () => {
		responses.push(new Response('boom', { status: 502 }));

		await expect(api.get('/api/systems')).rejects.toMatchObject({ status: 502, code: 'HttpError' });
	});

	it('204 resolves undefined; a network failure is NetworkError', async () => {
		responses.push(new Response(null, { status: 204 }));
		expect(await api.get('/api/jobs/current')).toBeUndefined();

		vi.stubGlobal('fetch', () => Promise.reject(new TypeError('offline')));
		await expect(api.get('/api/systems')).rejects.toMatchObject({
			status: 0,
			code: 'NetworkError'
		});
	});
});
