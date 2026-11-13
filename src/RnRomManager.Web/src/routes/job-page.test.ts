import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/state', () => ({
	page: { params: { id: 'j1' }, url: new URL('http://localhost/jobs/j1') }
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { default: JobPage } = await import('./jobs/[id]/+page.svelte');

describe('job page', () => {
	beforeEach(() => {
		resetClient();
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		vi.stubGlobal('fetch', () =>
			Promise.resolve(
				new Response(
					JSON.stringify({
						id: 'j1',
						type: 'ReviewResolve',
						mode: null,
						triggerSource: 'Manual',
						status: 'Completed',
						requestedBy: 1,
						startedAt: '2026-11-13T12:00:00Z',
						finishedAt: '2026-11-13T12:00:05Z',
						summary: {
							reviewId: 7,
							fileName: 'disc2.iso',
							gameId: null,
							code: 'ValidationFailed',
							errors: { title: ['Bad.'] }
						},
						error: null
					}),
					{ status: 200 }
				)
			)
		);
	});

	afterEach(() => vi.unstubAllGlobals());

	it("shows a ReviewResolve summary's errors as JSON and a null value as null (B6)", async () => {
		render(JobPage);

		expect(await screen.findByText('errors {"title":["Bad."]}')).toBeTruthy();
		expect(screen.getByText('gameId null')).toBeTruthy();
		expect(screen.getByText('fileName disc2.iso')).toBeTruthy();
		expect(screen.queryByText(/\[object Object\]/)).toBeNull();
	});
});
