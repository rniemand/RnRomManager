import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { resetClient } from '#lib/api/client.js';
import type { ActivityAuditItem, ActivityReport } from '#lib/api/types.js';
import { formatDateTime } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

vi.mock('$app/state', () => ({
	page: {
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { default: ActivityPage } = await import('./activity/+page.svelte');
const { default: ReportsLayout } = await import('./+layout.svelte');

const game = '00000000-0000-0000-0000-000000000123';
const audit = (overrides: Partial<ActivityAuditItem>): ActivityAuditItem => ({
	id: 1,
	occurredAt: '2026-11-17T10:00:00Z',
	action: 'GamePlayed',
	system: 'gb',
	gameId: game,
	user: 'boss',
	jobId: null,
	path: 'roms/gb/T/Tetris.zip',
	targetPath: null,
	details: 'Tetris (World).gb',
	gameTitle: 'Tetris',
	...overrides
});
const empty: ActivityReport = {
	lastSignIn: null,
	addedRoms: [],
	signIns: [],
	plays: [],
	saves: [],
	edits: [],
	deletions: []
};

describe('Activity report (B20)', () => {
	let answers: Record<string, { status: number; body?: unknown }>;
	let calls: string[];

	beforeEach(() => {
		resetClient();
		navigateTo('/reports/activity');
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		calls = [];
		answers = {
			'/api/reports/activity': { status: 200, body: empty },
			'/api/systems': { status: 200, body: [{ id: 'gb', displayName: 'Game Boy' }] }
		};
		vi.stubGlobal('fetch', (url: string) => {
			calls.push(url);
			const answer = answers[url] ?? { status: 204 };
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

	const sectionOf = (name: string) =>
		screen.getByRole('heading', { level: 2, name }).closest('section') as HTMLElement;
	const cells = (name: string) =>
		[...sectionOf(name).querySelectorAll('tbody tr')].map((tr) =>
			[...tr.querySelectorAll('td')].map((td) => td.textContent!.replace(/\s+/g, ' ').trim())
		);

	it('renders sections 1–6 as cards with their headings, and the 30-day note', async () => {
		render(ActivityPage);
		await screen.findAllByText(/in the last 30 days\./);

		for (const [name, id] of [
			['Added ROMs', 'activity-added'],
			['Sign-ins', 'activity-sign-ins'],
			['Plays', 'activity-plays'],
			['Saves', 'activity-saves'],
			['Edits', 'activity-edits'],
			['Deletions', 'activity-deletions']
		]) {
			const heading = screen.getByRole('heading', { level: 2, name });
			expect(heading.id).toBe(id);
			expect(heading.className).toBe('h5 mb-0');
			expect(heading.parentElement!.classList.contains('card-header')).toBe(true);
			expect(heading.closest('section')!.classList.contains('card')).toBe(true);
		}
		expect(screen.getByText(/keeps the last 30 days/)).toBeTruthy();
	});

	it('empty lists show empty states and no last sign-in line', async () => {
		render(ActivityPage);

		expect(await screen.findByText('No ROMs added yet.')).toBeTruthy();
		for (const message of [
			'No sign-ins in the last 30 days.',
			'No plays in the last 30 days.',
			'No save changes in the last 30 days.',
			'No edits in the last 30 days.',
			'No deletions in the last 30 days.'
		])
			expect(screen.getByText(message)).toBeTruthy();
		expect(screen.queryByTestId('last-sign-in')).toBeNull();
	});

	it('lists added ROMs with a game link, release, system name and size', async () => {
		answers['/api/reports/activity'] = {
			status: 200,
			body: {
				...empty,
				addedRoms: [
					{
						romId: 7,
						gameId: game,
						gameTitle: 'Tetris',
						fileName: 'Tetris (World).gb',
						system: 'gb',
						sizeBytes: 2048,
						addedAt: '2026-11-17T09:00:00Z'
					}
				]
			}
		};
		render(ActivityPage);

		await screen.findByRole('link', { name: 'Tetris' });
		await vi.waitFor(() =>
			expect(cells('Added ROMs')).toEqual([
				[
					formatDateTime('2026-11-17T09:00:00Z'),
					'Tetris',
					'Tetris (World).gb',
					'Game Boy',
					'2.0 KiB'
				]
			])
		);
		expect(screen.getByRole('link', { name: 'Tetris' }).getAttribute('href')).toBe(
			`/games/${game}`
		);
		const table = sectionOf('Added ROMs').querySelector('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.querySelector('thead')!.className).toBe('table-dark');
	});

	it('the last sign-in summary, then sign-ins with the raw outcome and System for a null user', async () => {
		answers['/api/reports/activity'] = {
			status: 200,
			body: {
				...empty,
				lastSignIn: { username: 'boss', at: '2026-11-17T08:00:00Z' },
				signIns: [
					audit({ id: 2, action: 'SignInFailed', user: null, gameId: null, gameTitle: null }),
					audit({ id: 1, action: 'SignedIn', gameId: null, gameTitle: null })
				]
			}
		};
		render(ActivityPage);

		const summary = await screen.findByTestId('last-sign-in');
		expect(summary.textContent!.replace(/\s+/g, ' ').trim()).toBe(
			`Last signed in: boss at ${formatDateTime('2026-11-17T08:00:00Z')}`
		);
		const time = formatDateTime('2026-11-17T10:00:00Z');
		expect(cells('Sign-ins')).toEqual([
			[time, 'System', 'SignInFailed'],
			[time, 'boss', 'SignedIn']
		]);
		expect(within(sectionOf('Sign-ins')).getByText('System').tagName).toBe('EM');
	});

	it('plays, saves, edits and deletions: game link, release or raw action; a gone game falls back to its file name', async () => {
		answers['/api/reports/activity'] = {
			status: 200,
			body: {
				...empty,
				plays: [audit({})],
				saves: [audit({ action: 'SaveDeleted', details: 'x' })],
				edits: [audit({ action: 'CoverSet' })],
				deletions: [
					audit({
						action: 'GameDeleted',
						gameTitle: null,
						path: 'roms/gb/Z/Zelda (USA).zip',
						details: null
					})
				]
			}
		};
		render(ActivityPage);
		await screen.findAllByRole('link', { name: 'Tetris' });

		const time = formatDateTime('2026-11-17T10:00:00Z');
		expect(cells('Plays')).toEqual([[time, 'boss', 'Tetris', 'Tetris (World).gb']]);
		expect(cells('Saves')).toEqual([[time, 'boss', 'Tetris', 'SaveDeleted']]);
		expect(cells('Edits')).toEqual([[time, 'boss', 'Tetris', 'CoverSet']]);
		expect(cells('Deletions')).toEqual([[time, 'boss', 'Zelda (USA).zip', 'GameDeleted']]);
		expect(within(sectionOf('Deletions')).queryByRole('link')).toBeNull();
	});

	it('a failing report shows its error in its sections, and retry reloads', async () => {
		answers['/api/reports/activity'] = { status: 500, body: { title: 'Boom' } };
		render(ActivityPage);

		await vi.waitFor(() => expect(within(sectionOf('Plays')).getByRole('alert')).toBeTruthy());
		expect(within(sectionOf('Added ROMs')).getByRole('alert')).toBeTruthy();
		answers['/api/reports/activity'] = { status: 200, body: empty };
		await fireEvent.click(within(sectionOf('Plays')).getByRole('button', { name: /Retry/ }));
		expect(await screen.findByText('No plays in the last 30 days.')).toBeTruthy();
	});

	it('a failing /api/systems falls back to the system id, without an error', async () => {
		answers['/api/systems'] = { status: 500 };
		answers['/api/reports/activity'] = {
			status: 200,
			body: {
				...empty,
				addedRoms: [
					{
						romId: 7,
						gameId: game,
						gameTitle: 'Tetris',
						fileName: 't.gb',
						system: 'gb',
						sizeBytes: 1,
						addedAt: '2026-11-17T09:00:00Z'
					}
				]
			}
		};
		render(ActivityPage);

		await screen.findByRole('link', { name: 'Tetris' });
		expect(within(sectionOf('Added ROMs')).getByText('GB')).toBeTruthy();
		expect(screen.queryByRole('alert')).toBeNull();
	});

	describe('layout', () => {
		const children = createRawSnippet(() => ({ render: () => '<div>Content</div>' }));

		it('an Admin sees the Activity tab with its icon', () => {
			render(ReportsLayout, { children });
			const tab = screen.getByRole('link', { name: 'Activity' });
			expect(tab.getAttribute('href')).toBe('/reports/activity');
			expect(tab.querySelector('i')!.className.trim()).toBe('bi bi-activity');
		});

		it('a Site user has no Activity tab and sees one "Not allowed" h1 on its route', () => {
			auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
			render(ReportsLayout, { children });

			expect(screen.queryByRole('link', { name: 'Activity' })).toBeNull();
			expect(screen.getAllByRole('heading', { level: 1 }).map((h) => h.textContent)).toEqual([
				'Not allowed'
			]);
			expect(screen.queryByText('Content')).toBeNull();
		});

		it('a Site user keeps the other report tabs', () => {
			auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
			navigateTo('/reports/library-size');
			render(ReportsLayout, { children });
			expect(screen.getAllByRole('link').map((a) => a.textContent!.trim())).toEqual([
				'Library Size',
				'Missing Images',
				'Duplicates'
			]);
		});

		it('the page makes no requests for a Site user', () => {
			auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
			render(ActivityPage);
			expect(calls).toEqual([]);
		});
	});
});
