import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: RomStoragePage } = await import('./rom-storage/+page.svelte');

const rows = (ndsEmbed = false, pending = 0) => [
	{
		id: 'gba',
		displayName: 'Game Boy Advance',
		defaultEmbedRoms: true,
		embedRoms: true,
		configurable: false,
		pendingGames: 0
	},
	{
		id: 'nds',
		displayName: 'Nintendo DS',
		defaultEmbedRoms: false,
		embedRoms: ndsEmbed,
		configurable: true,
		pendingGames: pending
	}
];
const running = {
	id: 'j1',
	type: 'StorageMigration',
	mode: null,
	status: 'Running',
	current: 0,
	total: 1,
	currentItem: null,
	startedAt: '2026-11-18T10:00:00Z'
};

type Answer = { status: number; body?: unknown };

describe('ROM Zip Config page (ROM Zip Config B15–B16)', () => {
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		jobs.current = null;
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		answers = { 'GET /api/admin/rom-storage': { status: 200, body: rows() } };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body ? JSON.parse(init.body as string) : undefined });
			const answer = answers[key] ?? { status: 204 };
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

	const row = async (name: string) => (await screen.findByText(name)).closest('tr')!;

	it('shows each system with its default and configured behaviour; only configurable rows have a button', async () => {
		answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(false, 2) };
		render(RomStoragePage);

		const gba = await row('Game Boy Advance');
		expect(gba.textContent).toContain('Stored in ZIP');
		expect(within(gba).queryByRole('button')).toBeNull();
		const nds = await row('Nintendo DS');
		expect(nds.textContent).toContain('Beside ZIP');
		expect(nds.textContent).toContain('2 games still to move');
		expect(within(nds).getByRole('button', { name: 'Store in ZIP' })).toBeTruthy();
		expect(screen.getAllByRole('columnheader').map((h) => h.textContent?.trim())).toEqual([
			'System',
			'Default Behaviour',
			'Configured Behaviour',
			'Change'
		]);
	});

	it('the button offers the opposite of the configured value', async () => {
		answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(true) };
		render(RomStoragePage);

		expect(
			within(await row('Nintendo DS')).getByRole('button', { name: 'Store beside ZIP' })
		).toBeTruthy();
	});

	it('into ZIP: the modal warns about slower saves; confirm PUTs, then a 202 refreshes jobs and toasts a link', async () => {
		answers['PUT /api/admin/rom-storage/nds'] = { status: 202, body: { jobId: 'j1' } };
		render(RomStoragePage);
		await fireEvent.click(await screen.findByRole('button', { name: 'Store in ZIP' }));

		expect(screen.getByText(/saves get slower/, { exact: false })).toBeTruthy();
		expect(screen.queryByText(/separate file next to its ZIP/)).toBeNull();
		expect(screen.getByText(/hours for a/)).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Start moving', hidden: true }));

		await vi.waitFor(() =>
			expect(calls).toContainEqual({
				key: 'PUT /api/admin/rom-storage/nds',
				body: { embedRoms: true }
			})
		);
		await vi.waitFor(() => expect(calls.map((c) => c.key)).toContain('GET /api/jobs/current'));
		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({ kind: 'success', link: { href: '/jobs' } })
		);
	});

	it('beside ZIP: the modal says each ROM becomes a separate file', async () => {
		answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(true) };
		render(RomStoragePage);
		await fireEvent.click(await screen.findByRole('button', { name: 'Store beside ZIP' }));

		expect(screen.getByText(/separate file next to its ZIP/)).toBeTruthy();
		expect(screen.queryByText(/saves get slower/)).toBeNull();
	});

	it('a 409 JobRunning shows the job-running toast', async () => {
		answers['PUT /api/admin/rom-storage/nds'] = {
			status: 409,
			body: { status: 409, code: 'JobRunning', title: 'A job is running' }
		};
		render(RomStoragePage);
		await fireEvent.click(await screen.findByRole('button', { name: 'Store in ZIP' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Start moving', hidden: true }));

		await vi.waitFor(() =>
			expect(toasts.items.at(-1)?.text).toBe(
				'Another job is running. Try again when it has finished.'
			)
		);
	});

	it('disables the buttons while a job runs', async () => {
		answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(false, 1) };
		answers['GET /api/jobs/current'] = { status: 200, body: running };
		render(RomStoragePage);

		await vi.waitFor(() =>
			expect(
				(screen.getByRole('button', { name: 'Store in ZIP' }) as HTMLButtonElement).disabled
			).toBe(true)
		);
		expect(
			(screen.getByRole('button', { name: 'Resume moves' }) as HTMLButtonElement).disabled
		).toBe(true);
	});

	it('Resume moves shows while games wait and POSTs the storage job', async () => {
		answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(false, 1) };
		answers['POST /api/jobs/storage-migration'] = { status: 202, body: { jobId: 'j1' } };
		render(RomStoragePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Resume moves' }));

		await vi.waitFor(() =>
			expect(calls.map((c) => c.key)).toContain('POST /api/jobs/storage-migration')
		);
	});

	it('no Resume button when nothing waits', async () => {
		render(RomStoragePage);
		await row('Nintendo DS');

		expect(screen.queryByRole('button', { name: 'Resume moves' })).toBeNull();
	});
});
