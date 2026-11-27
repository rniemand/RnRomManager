import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from '#lib/api/client.js';
import type { Me } from '#lib/types/index.js';
import { auth } from './auth.svelte';
import { navCounts, refreshCounts } from './navCounts.svelte';

const admin: Me = { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false };
const reviewUrl = '/api/review?status=Pending&pageSize=1';

describe('navCounts (D6)', () => {
	let answers: Record<string, { status: number; body?: unknown }>;
	let calls: string[];

	beforeEach(() => {
		resetClient();
		navCounts.reset();
		calls = [];
		answers = {
			[reviewUrl]: { status: 200, body: { items: [], page: 1, pageSize: 1, total: 7 } },
			'/api/ingest/removed': {
				status: 200,
				body: [
					{ system: 'gb', files: [{ name: 'a.gb' }, { name: 'b.gb' }] },
					{ system: 'nes', files: [{ name: 'c.nes' }] }
				]
			},
			'/api/ingest/duplicates': {
				status: 200,
				body: [{ system: 'gb', files: [{ name: 'a.gb' }, { name: 'b.gb' }] }]
			}
		};
		vi.stubGlobal('fetch', (url: string) => {
			calls.push(url);
			const answer = answers[url] ?? { status: 404 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('starts with every count null', () => {
		expect([navCounts.review, navCounts.deleted, navCounts.duplicates]).toEqual([null, null, null]);
	});

	it('refresh loads the review total and the sum of removed and duplicate files', async () => {
		auth.set(admin);

		await refreshCounts();

		expect(calls.sort()).toEqual(['/api/ingest/duplicates', '/api/ingest/removed', reviewUrl]);
		expect([navCounts.review, navCounts.deleted, navCounts.duplicates]).toEqual([7, 3, 2]);
	});

	it.each([
		['a non-admin', { ...admin, roles: [] }],
		['an admin who must change the password', { ...admin, mustChangePassword: true }]
	])('%s loads nothing', async (_who, me) => {
		auth.set(me);

		await refreshCounts();

		expect(calls).toEqual([]);
		expect([navCounts.review, navCounts.deleted, navCounts.duplicates]).toEqual([null, null, null]);
	});

	it('a failed call keeps the last value of that count only', async () => {
		auth.set(admin);
		await refreshCounts();
		answers['/api/ingest/removed'] = { status: 500 };
		answers[reviewUrl] = { status: 200, body: { items: [], page: 1, pageSize: 1, total: 2 } };

		await refreshCounts();

		expect([navCounts.review, navCounts.deleted, navCounts.duplicates]).toEqual([2, 3, 2]);
	});

	it('reset clears every count', async () => {
		auth.set(admin);
		await refreshCounts();

		navCounts.reset();

		expect([navCounts.review, navCounts.deleted, navCounts.duplicates]).toEqual([null, null, null]);
	});
});
