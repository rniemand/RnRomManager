import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/state', () => ({ page: { params: {}, url: new URL('http://localhost/deleted') } }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
// Toggles `show` so a test can tell an open modal from a closed one.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		constructor(private element: Element) {}
		show() {
			this.element.classList.add('show');
		}
		hide() {
			this.element.classList.remove('show');
		}
		dispose() {}
	}
}));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: DeletedPage } = await import('./+page.svelte');

type Answer = { status: number; body?: unknown };

const file = (name: string) => ({ name, sizeBytes: 1024, modifiedAt: '2026-11-13T09:00:00Z' });
const running = {
	id: 'j1',
	type: 'Ingest',
	mode: null,
	status: 'Running',
	current: 1,
	total: 4,
	currentItem: 'x',
	startedAt: '2026-11-13T10:00:00Z'
};

describe('Deleted ROMs page (D3, D4)', () => {
	let calls: string[];
	let bodies: Record<string, unknown[]>;
	let answers: Record<string, Answer[]>;
	let fixed: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		jobs.current = null;
		toasts.items = [];
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		bodies = {};
		answers = {};
		fixed = {
			'GET /api/ingest/removed': {
				status: 200,
				body: [
					{ system: 'gb', files: [file('Tetris (World).gb'), file('Tetris (World) (2).gb')] },
					{ system: 'nes', files: [file('Zelda.nes')] }
				]
			},
			'GET /api/systems': {
				status: 200,
				body: [
					{ id: 'gb', displayName: 'Game Boy', shortName: 'GB' },
					{ id: 'nes', displayName: 'Nintendo Entertainment System', shortName: 'NES' }
				]
			},
			'POST /api/ingest/delete': { status: 200, body: { deleted: 1, failed: [] } }
		};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			(bodies[key] ??= []).push(
				typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body
			);
			const answer = answers[key]?.shift() ?? fixed[key] ?? { status: 204 };
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
		jobs.current = null;
	});

	const modal = () => screen.getByText("This can't be undone.").closest('.modal') as HTMLElement;
	const deleteSelected = () =>
		screen.getByRole('button', { name: /^Delete selected/ }) as HTMLButtonElement;

	it('lists one card per system with the display name and file count', async () => {
		render(DeletedPage);

		expect(await screen.findByRole('heading', { level: 1, name: 'Deleted ROMs' })).toBeTruthy();
		expect(document.title).toBe('Deleted ROMs · RnRomManager');
		await screen.findByText('Zelda.nes');
		const headers = [...document.querySelectorAll('section h2')].map((h) =>
			h.textContent!.replace(/\s+/g, ' ').trim()
		);
		expect(headers).toEqual(['Game Boy 2 files', 'Nintendo Entertainment System 1 file']);
		const gb = screen.getByRole('region', { name: /Game Boy/ });
		expect(
			within(gb)
				.getAllByRole('row')
				.slice(1)
				.map((r) => r.children[1].textContent)
		).toEqual(['Tetris (World).gb', 'Tetris (World) (2).gb']);
		expect(within(gb).getByRole('columnheader', { name: 'Modified' })).toBeTruthy();
	});

	it('a row delete confirms and posts folder removed; Delete reloads and refreshes the counts', async () => {
		render(DeletedPage);
		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Zelda.nes' }));

		expect(modal().classList.contains('show')).toBe(true);
		expect(modal().querySelector('.modal-title')!.textContent).toBe('Permanently delete 1 file?');
		expect(calls).not.toContain('POST /api/ingest/delete');
		await fireEvent.click(within(modal()).getByRole('button', { name: 'Delete', hidden: true }));

		await vi.waitFor(() =>
			expect(bodies['POST /api/ingest/delete']).toEqual([
				{ files: [{ system: 'nes', folder: 'removed', name: 'Zelda.nes' }] }
			])
		);
		await vi.waitFor(() =>
			expect(calls.filter((c) => c === 'GET /api/ingest/removed').length).toBeGreaterThanOrEqual(3)
		);
		expect(toasts.items.map((t) => t.text)).toContain('Deleted Zelda.nes.');
		// refreshCounts() also asks for the review count (D6).
		expect(calls).toContain('GET /api/review?status=Pending&pageSize=1');
	});

	it('Shift+click on a row deletes at once', async () => {
		render(DeletedPage);
		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Zelda.nes' }), {
			shiftKey: true
		});

		await vi.waitFor(() => expect(calls).toContain('POST /api/ingest/delete'));
	});

	it('Select all spans every system; Delete selected confirms, and Shift+click skips the modal (no archive exception)', async () => {
		fixed['GET /api/ingest/removed'] = {
			status: 200,
			body: [
				{ system: 'gb', files: [file('pack.zip')] },
				{ system: 'nes', files: [file('Zelda.nes')] }
			]
		};
		render(DeletedPage);
		await screen.findByText('pack.zip');
		expect(deleteSelected().disabled).toBe(true);

		await fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
		expect(screen.getByText('2 selected')).toBeTruthy();
		await fireEvent.click(deleteSelected());
		expect(modal().classList.contains('show')).toBe(true);
		expect([...modal().querySelectorAll('li')].map((li) => li.textContent)).toEqual([
			'gb/pack.zip',
			'nes/Zelda.nes'
		]);
		await fireEvent.click(within(modal()).getByRole('button', { name: 'Cancel', hidden: true }));

		await fireEvent.click(deleteSelected(), { shiftKey: true });

		await vi.waitFor(() =>
			expect(bodies['POST /api/ingest/delete']).toEqual([
				{
					files: [
						{ system: 'gb', folder: 'removed', name: 'pack.zip' },
						{ system: 'nes', folder: 'removed', name: 'Zelda.nes' }
					]
				}
			])
		);
	});

	it('Clear selection empties it', async () => {
		render(DeletedPage);
		await screen.findByText('Zelda.nes');
		await fireEvent.click(screen.getByRole('button', { name: 'Select all' }));

		await fireEvent.click(screen.getByRole('button', { name: 'Clear selection' }));

		expect(screen.getByText('0 selected')).toBeTruthy();
	});

	it('the modal lists at most 10 entries and "and n more"', async () => {
		fixed['GET /api/ingest/removed'] = {
			status: 200,
			body: [{ system: 'gb', files: Array.from({ length: 12 }, (_, i) => file(`r${i}.gb`)) }]
		};
		render(DeletedPage);
		await screen.findByText('r0.gb');
		await fireEvent.click(screen.getByRole('button', { name: 'Select all' }));

		await fireEvent.click(deleteSelected());

		const items = [...modal().querySelectorAll('li')].map((li) => li.textContent);
		expect(items).toHaveLength(11);
		expect(items.at(-1)).toBe('and 2 more');
		expect(modal().querySelector('.modal-title')!.textContent).toBe('Permanently delete 12 files?');
	});

	it('every delete button is disabled while a job runs', async () => {
		fixed['GET /api/jobs/current'] = { status: 200, body: running };
		jobs.current = running as never;
		render(DeletedPage);
		await screen.findByText('Zelda.nes');

		const deletes = screen
			.getAllByRole('button')
			.filter((b) => /^Delete/.test(b.getAttribute('aria-label') ?? b.textContent!.trim()));
		expect(deletes.length).toBe(4);
		expect(deletes.every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
	});

	it('JobRunning from a delete shows the job-running toast', async () => {
		answers['POST /api/ingest/delete'] = [
			{ status: 409, body: { code: 'JobRunning', title: 'x' } }
		];
		render(DeletedPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Zelda.nes' }), {
			shiftKey: true
		});

		await vi.waitFor(() => expect(toasts.items.map((t) => t.kind)).toEqual(['warning']));
	});

	describe('restore (D3, D5)', () => {
		it('a row Restore posts that file without confirmation, toasts with the job link and reloads', async () => {
			answers['POST /api/ingest/restore'] = [
				{ status: 200, body: { restored: 1, failed: [], jobId: 'job-9' } }
			];
			render(DeletedPage);
			const restore = await screen.findByRole('button', { name: 'Restore Zelda.nes' });
			expect(restore.className).toBe('btn btn-sm btn-outline-primary');
			expect(restore.querySelector('.bi-arrow-counterclockwise')).toBeTruthy();
			const del = screen.getByRole('button', { name: 'Delete Zelda.nes' });
			expect(restore.compareDocumentPosition(del) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

			await fireEvent.click(restore);

			await vi.waitFor(() =>
				expect(bodies['POST /api/ingest/restore']).toEqual([
					{ files: [{ system: 'nes', name: 'Zelda.nes' }] }
				])
			);
			await vi.waitFor(() =>
				expect(toasts.items).toContainEqual(
					expect.objectContaining({
						text: 'Restored 1 file; importing.',
						kind: 'success',
						link: { href: '/jobs/job-9', text: 'View job' }
					})
				)
			);
			expect(document.querySelector('.modal.show')).toBeNull();
			await vi.waitFor(() =>
				expect(calls.filter((c) => c === 'GET /api/ingest/removed').length).toBeGreaterThanOrEqual(
					3
				)
			);
			expect(calls).toContain('GET /api/review?status=Pending&pageSize=1');
		});

		it('Restore selected posts every selected file; without a job it points to Ingestion', async () => {
			answers['POST /api/ingest/restore'] = [
				{ status: 200, body: { restored: 3, failed: [], jobId: null } }
			];
			render(DeletedPage);
			await screen.findByText('Zelda.nes');
			const restoreSelected = screen.getByRole('button', {
				name: /^Restore selected/
			}) as HTMLButtonElement;
			expect(restoreSelected.disabled).toBe(true);

			await fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
			expect(restoreSelected.textContent!.trim()).toBe('Restore selected (3)');
			await fireEvent.click(restoreSelected);

			await vi.waitFor(() =>
				expect(bodies['POST /api/ingest/restore']).toEqual([
					{
						files: [
							{ system: 'gb', name: 'Tetris (World).gb' },
							{ system: 'gb', name: 'Tetris (World) (2).gb' },
							{ system: 'nes', name: 'Zelda.nes' }
						]
					}
				])
			);
			await vi.waitFor(() =>
				expect(toasts.items.map((t) => [t.text, t.link])).toContainEqual([
					'Restored 3 files to Ingestion; import them there.',
					undefined
				])
			);
		});

		it('failed files and JobRunning are toasted', async () => {
			answers['POST /api/ingest/restore'] = [
				{
					status: 200,
					body: { restored: 0, failed: [{ system: 'nes', name: 'Zelda.nes' }], jobId: null }
				},
				{ status: 409, body: { code: 'JobRunning', title: 'x' } }
			];
			render(DeletedPage);
			await fireEvent.click(await screen.findByRole('button', { name: 'Restore Zelda.nes' }));
			await vi.waitFor(() =>
				expect(toasts.items.map((t) => t.text)).toEqual(["Couldn't restore: nes/Zelda.nes"])
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Restore Zelda.nes' }));
			await vi.waitFor(() => expect(toasts.items.at(-1)?.kind).toBe('warning'));
		});

		it('every restore button is disabled while a job runs', async () => {
			fixed['GET /api/jobs/current'] = { status: 200, body: running };
			jobs.current = running as never;
			render(DeletedPage);
			await screen.findByText('Zelda.nes');

			const restores = screen
				.getAllByRole('button')
				.filter((b) => /^Restore/.test(b.getAttribute('aria-label') ?? b.textContent!.trim()));
			expect(restores.length).toBe(4);
			expect(restores.every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
		});
	});

	it('shows the empty state without deleted ROMs', async () => {
		fixed['GET /api/ingest/removed'] = { status: 200, body: [] };
		render(DeletedPage);

		expect(await screen.findByText('No deleted ROMs')).toBeTruthy();
	});

	it('a User sees Not allowed and nothing is fetched', async () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		render(DeletedPage);

		expect(await screen.findByText(/Not allowed/i)).toBeTruthy();
		expect(calls).toEqual([]);
	});
});
