import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { navCounts } from '#lib/stores/navCounts.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/state', () => ({
	page: { params: {}, url: new URL('http://localhost/duplicates') }
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

const { default: DuplicatesPage } = await import('./+page.svelte');

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

describe('Duplicates page (DU1–DU3)', () => {
	let calls: string[];
	let bodies: Record<string, unknown[]>;
	let answers: Record<string, Answer[]>;
	let fixed: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		jobs.current = null;
		toasts.items = [];
		navCounts.reset();
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		bodies = {};
		answers = {};
		fixed = {
			'GET /api/ingest/duplicates': {
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
	const button = (name: RegExp | string) =>
		screen.getByRole('button', { name }) as HTMLButtonElement;
	const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();
	const confirm = () =>
		fireEvent.click(within(modal()).getByRole('button', { name: 'Delete', hidden: true }));

	it('shows the summary and one section per system with count and size', async () => {
		render(DuplicatesPage);

		expect(await screen.findByRole('heading', { level: 1, name: 'Duplicates' })).toBeTruthy();
		expect(document.title).toBe('Duplicates · RnRomManager');
		await screen.findByText('Zelda.nes');
		expect(text(screen.getByText(/using/))).toMatch(/^3 files using 3(\.0)? KiB$/);
		const headers = [...document.querySelectorAll('section h2')].map(text);
		expect(headers[0]).toMatch(/^Game Boy 2 files · 2(\.0)? KiB$/);
		expect(headers[1]).toMatch(/^Nintendo Entertainment System 1 file · 1(\.0)? KiB$/);
		expect(screen.queryByRole('button', { name: /Restore/ })).toBeNull();
	});

	it('shows the empty state', async () => {
		fixed['GET /api/ingest/duplicates'] = { status: 200, body: [] };
		render(DuplicatesPage);

		expect(await screen.findByText('No duplicate files.')).toBeTruthy();
	});

	it('a row delete confirms, posts folder duplicates, then reloads the list and counts', async () => {
		render(DuplicatesPage);
		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Zelda.nes' }));

		expect(modal().classList.contains('show')).toBe(true);
		expect(calls).not.toContain('POST /api/ingest/delete');
		await confirm();

		await vi.waitFor(() =>
			expect(bodies['POST /api/ingest/delete']).toEqual([
				{ files: [{ system: 'nes', folder: 'duplicates', name: 'Zelda.nes' }] }
			])
		);
		await vi.waitFor(() =>
			expect(calls.filter((c) => c === 'GET /api/ingest/duplicates').length).toBeGreaterThan(2)
		);
		expect(calls).toContain('GET /api/ingest/removed');
	});

	it('Shift+click on Delete selected skips the confirmation', async () => {
		render(DuplicatesPage);
		await screen.findByText('Zelda.nes');
		await fireEvent.click(button('Select all'));
		expect(screen.getByText('3 selected')).toBeTruthy();

		await fireEvent.click(button(/^Delete selected/), { shiftKey: true });

		await vi.waitFor(() =>
			expect(bodies['POST /api/ingest/delete']).toEqual([
				{
					files: [
						{ system: 'gb', folder: 'duplicates', name: 'Tetris (World).gb' },
						{ system: 'gb', folder: 'duplicates', name: 'Tetris (World) (2).gb' },
						{ system: 'nes', folder: 'duplicates', name: 'Zelda.nes' }
					]
				}
			])
		);
	});

	it('Delete all always confirms, even with Shift, and lists every file', async () => {
		render(DuplicatesPage);
		await screen.findByText('Zelda.nes');
		const all = button(/^Delete all/);
		expect(text(all)).toMatch(/^Delete all \(3 · 3(\.0)? KiB\)$/);

		await fireEvent.click(all, { shiftKey: true });

		expect(modal().classList.contains('show')).toBe(true);
		expect(modal().querySelector('.modal-title')!.textContent).toBe('Permanently delete 3 files?');
		expect(calls).not.toContain('POST /api/ingest/delete');
		await confirm();
		await vi.waitFor(() =>
			expect((bodies['POST /api/ingest/delete'][0] as { files: unknown[] }).files).toHaveLength(3)
		);
	});

	it('disables the delete buttons while a job runs', async () => {
		render(DuplicatesPage);
		await screen.findByText('Zelda.nes');
		await fireEvent.click(button('Select all'));
		jobs.current = running as never;

		await vi.waitFor(() => expect(button(/^Delete all/).disabled).toBe(true));
		expect(button(/^Delete selected/).disabled).toBe(true);
		expect(button('Delete Zelda.nes').disabled).toBe(true);
	});
});
