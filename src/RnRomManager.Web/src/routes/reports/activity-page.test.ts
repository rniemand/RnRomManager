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

const job = (id: string, startedAt: string, finishedAt: string | null, status = 'Completed') => ({
	id,
	type: 'Ingest',
	mode: null,
	triggerSource: 'Manual',
	status,
	requestedBy: 1,
	startedAt,
	finishedAt,
	summary: null,
	error: null
});
const backups = (overrides: Record<string, unknown>) => ({
	enabled: true,
	time: '03:00',
	retention: 7,
	timeZone: 'UTC',
	nextRunAt: null,
	running: false,
	lastResult: null,
	backups: [],
	...overrides
});

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
			'/api/systems': { status: 200, body: [{ id: 'gb', displayName: 'Game Boy' }] },
			'/api/jobs?pageSize=10': {
				status: 200,
				body: { items: [], page: 1, pageSize: 10, total: 0 }
			},
			'/api/admin/backups': { status: 200, body: backups({}) }
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
		screen.getByRole('heading', { level: 2, name }).closest('.accordion-item') as HTMLElement;
	const cells = (name: string) =>
		[...sectionOf(name).querySelectorAll('tbody tr')].map((tr) =>
			[...tr.querySelectorAll('td')].map((td) => td.textContent!.replace(/\s+/g, ' ').trim())
		);

	const sections = [
		['Added ROMs', 'activity-added'],
		['Sign-ins', 'activity-sign-ins'],
		['Plays', 'activity-plays'],
		['Saves', 'activity-saves'],
		['Edits', 'activity-edits'],
		['Deletions', 'activity-deletions'],
		['Recent jobs', 'activity-jobs'],
		['Last backup', 'activity-backup']
	] as const;
	const expanded = () =>
		sections.map(([name]) => screen.getByRole('button', { name }).getAttribute('aria-expanded'));

	it('renders the sections as accordion panels with their headings, and the 30-day note', async () => {
		render(ActivityPage);
		await screen.findAllByText(/in the last 30 days\./);

		for (const [name, id] of sections) {
			const heading = screen.getByRole('heading', { level: 2, name });
			expect(heading.id).toBe(id);
			expect(heading.className).toBe('accordion-header');
			expect(document.getElementById(`${id}-panel`)!.getAttribute('aria-labelledby')).toBe(id);
		}
		expect(screen.getByText(/keeps the last 30 days/)).toBeTruthy();
	});

	it('one section is open at a time, Added ROMs first', async () => {
		render(ActivityPage);
		await screen.findAllByText(/in the last 30 days\./);

		expect(expanded()).toEqual(['true', ...Array(7).fill('false')]);
		await fireEvent.click(screen.getByRole('button', { name: 'Plays' }));
		expect(expanded()).toEqual(['false', 'false', 'true', ...Array(5).fill('false')]);
		await fireEvent.click(screen.getByRole('button', { name: 'Plays' }));
		expect(expanded()).toEqual(Array(8).fill('false'));
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

	it('Recent jobs: job label, status badge, started linked to the job, duration or running', async () => {
		answers['/api/jobs?pageSize=10'] = {
			status: 200,
			body: {
				items: [
					job('j2', '2026-11-17T10:00:00Z', null, 'Running'),
					{ ...job('j1', '2026-11-17T09:00:00Z', '2026-11-17T09:03:04Z'), type: 'DatRefresh' }
				],
				page: 1,
				pageSize: 10,
				total: 2
			}
		};
		render(ActivityPage);

		const link = await screen.findByRole('link', { name: formatDateTime('2026-11-17T09:00:00Z') });
		expect(link.getAttribute('href')).toBe('/jobs/j1');
		expect(cells('Recent jobs')).toEqual([
			['Ingest', 'Running', formatDateTime('2026-11-17T10:00:00Z'), 'running'],
			['DAT Refresh', 'Completed', formatDateTime('2026-11-17T09:00:00Z'), '3m 4s']
		]);
		expect(within(sectionOf('Recent jobs')).getByText('Completed').className).toContain('badge');
	});

	it('Last backup: an OK last result', async () => {
		answers['/api/admin/backups'] = {
			status: 200,
			body: backups({
				lastResult: { at: '2026-11-17T03:00:00Z', ok: true, fileName: 'b1.sql.gz', error: null }
			})
		};
		render(ActivityPage);

		await vi.waitFor(() =>
			expect(cells('Last backup')).toEqual([
				[formatDateTime('2026-11-17T03:00:00Z')],
				['OK b1.sql.gz']
			])
		);
	});

	it('Last backup: a failed last result shows its error', async () => {
		answers['/api/admin/backups'] = {
			status: 200,
			body: backups({
				lastResult: { at: '2026-11-17T03:00:00Z', ok: false, fileName: null, error: 'disk full' }
			})
		};
		render(ActivityPage);

		await vi.waitFor(() => expect(cells('Last backup')[1]).toEqual(['Failed disk full']));
	});

	it('Last backup: after a restart the newest file, labelled since restart', async () => {
		answers['/api/admin/backups'] = {
			status: 200,
			body: backups({
				backups: [
					{ fileName: 'new.sql.gz', sizeBytes: 1, createdAt: '2026-11-16T03:00:00Z' },
					{ fileName: 'old.sql.gz', sizeBytes: 1, createdAt: '2026-11-15T03:00:00Z' }
				]
			})
		};
		render(ActivityPage);

		await vi.waitFor(() =>
			expect(cells('Last backup')).toEqual([
				['new.sql.gz'],
				[`${formatDateTime('2026-11-16T03:00:00Z')} (since restart: no backup run)`]
			])
		);
	});

	it('Last backup and Recent jobs: empty states', async () => {
		render(ActivityPage);
		expect(await screen.findByText('No backups yet.')).toBeTruthy();
		expect(screen.getByText('No jobs have run yet.')).toBeTruthy();
	});

	it('loads the three sources in parallel; one failing shows its error while the others render', async () => {
		answers['/api/jobs?pageSize=10'] = { status: 500, body: { title: 'Boom' } };
		render(ActivityPage);

		expect(calls.filter((u) => u !== '/api/systems')).toEqual([
			'/api/reports/activity',
			'/api/jobs?pageSize=10',
			'/api/admin/backups'
		]);
		await vi.waitFor(() =>
			expect(within(sectionOf('Recent jobs')).getByRole('alert')).toBeTruthy()
		);
		expect(within(sectionOf('Plays')).queryByRole('alert')).toBeNull();
		expect(within(sectionOf('Plays')).getByText('No plays in the last 30 days.')).toBeTruthy();
		expect(within(sectionOf('Last backup')).getByText('No backups yet.')).toBeTruthy();

		answers['/api/jobs?pageSize=10'] = {
			status: 200,
			body: { items: [], page: 1, pageSize: 10, total: 0 }
		};
		await fireEvent.click(within(sectionOf('Recent jobs')).getByRole('button', { name: /Retry/ }));
		expect(await screen.findByText('No jobs have run yet.')).toBeTruthy();
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
				'Newly Added',
				'Library Size',
				'Missing Images',
				'Duplicates',
				'Multiple Releases',
				'Games With Hacks',
				'Image Resolutions'
			]);
		});

		it('the page makes no requests for a Site user', () => {
			auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
			render(ActivityPage);
			expect(calls).toEqual([]);
		});
	});
});
