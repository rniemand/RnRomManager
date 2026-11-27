import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { api, resetClient } from '#lib/api/client.js';
import type { BackupStatus } from '#lib/types/index.js';
import { formatDateTime } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { navCounts } from '#lib/stores/navCounts.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));
const goto = vi.fn(() => Promise.resolve());
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...(args as [])) }));

const { default: BackupsPage } = await import('./backups/+page.svelte');

const backup = (fileName: string, createdAt: string, sizeBytes = 2048) => ({
	fileName,
	createdAt,
	sizeBytes
});

const status = (overrides: Partial<BackupStatus> = {}): BackupStatus => ({
	enabled: true,
	time: '03:30',
	retention: 30,
	timeZone: 'Europe/Amsterdam',
	nextRunAt: '2026-11-07T02:30:00+00:00',
	running: false,
	lastResult: null,
	backups: [
		backup('rnrommanager-20261106-023000.zip', '2026-11-06T02:30:00+00:00', 4096),
		backup('rnrommanager-20261105-023000.zip', '2026-11-05T02:30:00+00:00')
	],
	...overrides
});

type Answer = { status: number; body?: unknown };

describe('admin backups page (B18)', () => {
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Answer[]>;
	let pending: ((r: Response) => void) | null;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		toasts.items = [];
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		pending = null;
		answers = { 'GET /api/admin/backups': [{ status: 200, body: status() }] };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body ? JSON.parse(init.body as string) : undefined });
			if (key === 'POST /api/admin/backups' && !answers[key]?.length)
				return new Promise<Response>((r) => (pending = r));
			const list = answers[key];
			const answer = (list && list.length > 1 ? list.shift() : list?.[0]) ?? { status: 204 };
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

	it('shows the settings, next run, last result and the backups newest first', async () => {
		answers['GET /api/admin/backups'] = [
			{
				status: 200,
				body: status({
					lastResult: {
						at: '2026-11-06T02:31:00+00:00',
						ok: true,
						fileName: 'rnrommanager-20261106-023000.zip',
						error: null
					}
				})
			}
		];
		render(BackupsPage);

		const time = (await screen.findByLabelText(
			'Daily at (server time, Europe/Amsterdam)'
		)) as HTMLInputElement;
		expect(time.value).toBe('03:30');
		expect(time.type).toBe('time');
		expect((screen.getByLabelText('Backups to keep') as HTMLInputElement).value).toBe('30');
		expect(
			(screen.getByRole('switch', { name: 'Daily backups enabled' }) as HTMLInputElement).checked
		).toBe(true);
		expect(screen.getByTestId('next-run').textContent!.trim()).toBe(
			formatDateTime('2026-11-07T02:30:00+00:00')
		);
		expect(screen.getByTestId('last-result').textContent).toContain(
			'rnrommanager-20261106-023000.zip'
		);

		const rows = [...document.querySelectorAll('tbody tr')].map(
			(r) => r.querySelector('td')!.textContent
		);
		expect(rows).toEqual(['rnrommanager-20261106-023000.zip', 'rnrommanager-20261105-023000.zip']);
		expect(document.querySelector('tbody .file-size')!.textContent).toBe('4.0 KiB');
		expect(document.querySelector('tbody .file-size')!.parentElement!.className).toBe(
			'badge text-bg-light border'
		);
		const table = document.querySelector('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.querySelector('thead tr')!.className).toBe('');
		// A2: Download (a link) then Restore, as icon buttons.
		const download = screen.getByRole('link', {
			name: 'Download rnrommanager-20261106-023000.zip'
		});
		expect(download.getAttribute('href')).toBe(
			'/api/admin/backups/rnrommanager-20261106-023000.zip/download'
		);
		expect(download.hasAttribute('download')).toBe(true);
		const actions = download.parentElement!;
		expect(actions.className).toBe('d-flex gap-2 justify-content-end');
		expect(actions.lastElementChild!.getAttribute('aria-label')).toBe(
			'Restore rnrommanager-20261106-023000.zip'
		);
		expect(
			screen.getByRole('link', { name: 'How to restore a backup' }).getAttribute('href')
		).toContain('docs/deployment.md#backup-and-restore');
	});

	it('is three accordion items, Schedule, Status and Backups (n), only Backups open (release editing B34)', async () => {
		answers['GET /api/admin/backups'] = [{ status: 200, body: status() }];
		render(BackupsPage);
		await screen.findByLabelText('Backups to keep');

		const items = [...document.querySelectorAll('.accordion-item')];
		expect(
			items.map((item) => [
				item.querySelector('.accordion-header')!.id,
				item.querySelector('.accordion-button')!.textContent!.trim(),
				item.querySelector('.accordion-collapse')!.id,
				item.querySelector('.accordion-collapse')!.classList.contains('show')
			])
		).toEqual([
			['backup-schedule', 'Schedule', 'backups-schedule-body', false],
			['backups-status', 'Status', 'backups-status-body', false],
			['backups-list', 'Backups (2)', 'backups-list-body', true]
		]);
		expect(document.querySelector('form')!.getAttribute('aria-labelledby')).toBe('backup-schedule');
		expect(document.querySelectorAll('h2.h5')).toHaveLength(0);
		expect(items[2].querySelector('.accordion-body')!.classList).toContain('p-0');
		expect(document.querySelector('table')!.closest('.card')).toBeNull();

		await fireEvent.click(screen.getByRole('button', { name: 'Schedule' }));
		expect(items[0].querySelector('.accordion-collapse')!.classList).toContain('show');
	});

	it('shows Disabled and an empty state when there is nothing', async () => {
		answers['GET /api/admin/backups'] = [
			{ status: 200, body: status({ enabled: false, nextRunAt: null, backups: [] }) }
		];
		render(BackupsPage);

		const empty = await screen.findByText('No backups yet.');
		expect(empty.closest('#backups-list-body')).not.toBeNull();
		expect(screen.getByTestId('next-run').textContent!.trim()).toBe('Disabled');
		expect(document.querySelector('table')).toBeNull();
	});

	it('shows a failed last result with its error', async () => {
		answers['GET /api/admin/backups'] = [
			{
				status: 200,
				body: status({
					lastResult: {
						at: '2026-11-06T02:31:00+00:00',
						ok: false,
						fileName: null,
						error: 'disk full'
					}
				})
			}
		];
		render(BackupsPage);

		expect((await screen.findByTestId('last-result')).textContent).toContain('disk full');
	});

	it('Save sends PUT with the form and shows a toast', async () => {
		answers['PUT /api/admin/backups/settings'] = [
			{
				status: 200,
				body: status({ enabled: false, time: '22:15', retention: 7, nextRunAt: null })
			}
		];
		render(BackupsPage);
		const time = await screen.findByLabelText(/Daily at/);

		await fireEvent.click(screen.getByRole('switch', { name: 'Daily backups enabled' }));
		await fireEvent.input(time, { target: { value: '22:15' } });
		await fireEvent.input(screen.getByLabelText('Backups to keep'), { target: { value: '7' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		await vi.waitFor(() =>
			expect(calls).toContainEqual({
				key: 'PUT /api/admin/backups/settings',
				body: { enabled: false, time: '22:15', retention: 7 }
			})
		);
		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain('Backup settings saved.')
		);
		expect(screen.getByTestId('next-run').textContent!.trim()).toBe('Disabled');
	});

	it('validates before sending', async () => {
		render(BackupsPage);
		await screen.findByLabelText(/Daily at/);

		await fireEvent.input(screen.getByLabelText('Backups to keep'), { target: { value: '0' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		expect(screen.getByText('Keep 1–365 backups.')).toBeTruthy();
		expect(calls.some((c) => c.key.startsWith('PUT'))).toBe(false);
	});

	it('shows the server field errors', async () => {
		answers['PUT /api/admin/backups/settings'] = [
			{
				status: 400,
				body: {
					code: 'ValidationFailed',
					title: 'Invalid',
					errors: { time: ['Use HH:mm, 00:00–23:59.'] }
				}
			}
		];
		render(BackupsPage);
		await screen.findByLabelText(/Daily at/);

		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		expect(await screen.findByText('Use HH:mm, 00:00–23:59.')).toBeTruthy();
		expect(screen.getByLabelText(/Daily at/).classList.contains('is-invalid')).toBe(true);
	});

	it('Back up now posts, shows a spinner, then adds the new backup', async () => {
		const fresh = backup('rnrommanager-20261107-101500.zip', '2026-11-07T10:15:00+00:00');
		render(BackupsPage);
		await screen.findByLabelText(/Daily at/);
		answers['GET /api/admin/backups'] = [
			{ status: 200, body: status({ backups: [fresh, ...status().backups] }) }
		];

		await fireEvent.click(screen.getByRole('button', { name: /Back up now/ }));

		const busy = screen.getByRole('button', { name: /Backing up/ }) as HTMLButtonElement;
		expect(busy.disabled).toBe(true);
		expect(busy.querySelector('.spinner-border')).toBeTruthy();
		pending!(new Response(JSON.stringify(fresh), { status: 201 }));

		expect(await screen.findByRole('cell', { name: fresh.fileName })).toBeTruthy();
		expect(toasts.items.map((t) => t.text)).toContain(`Backup ${fresh.fileName} written.`);
		expect(screen.getByRole('button', { name: /Back up now/ })).toBeTruthy();
	});

	it('409 BackupRunning shows a warning toast', async () => {
		answers['POST /api/admin/backups'] = [
			{ status: 409, body: { code: 'BackupRunning', title: 'Busy' } }
		];
		render(BackupsPage);
		await screen.findByLabelText(/Daily at/);

		await fireEvent.click(screen.getByRole('button', { name: /Back up now/ }));

		await vi.waitFor(() =>
			expect(toasts.items).toContainEqual(
				expect.objectContaining({ text: 'A backup is already running.', kind: 'warning' })
			)
		);
	});

	it('a failed backup shows an error toast', async () => {
		answers['POST /api/admin/backups'] = [
			{ status: 500, body: { code: 'BackupFailed', title: 'The backup failed' } }
		];
		render(BackupsPage);
		await screen.findByLabelText(/Daily at/);

		await fireEvent.click(screen.getByRole('button', { name: /Back up now/ }));

		await vi.waitFor(() =>
			expect(toasts.items).toContainEqual(
				expect.objectContaining({ text: 'The backup failed', kind: 'danger' })
			)
		);
	});

	it('a backup already running disables the button with a spinner', async () => {
		answers['GET /api/admin/backups'] = [{ status: 200, body: status({ running: true }) }];
		render(BackupsPage);

		const button = (await screen.findByRole('button', { name: /Backing up/ })) as HTMLButtonElement;
		expect(button.disabled).toBe(true);
	});

	describe('restore (B26, B27.8)', () => {
		const name = 'rnrommanager-20261117-040000 (2).zip';
		const encoded = 'rnrommanager-20261117-040000%20(2).zip';
		const restoreKey = `POST /api/admin/backups/${encoded}/restore`;
		const modal = () => document.querySelector<HTMLElement>('.modal')!;
		const confirm = () =>
			screen.getByRole('button', { name: 'Restore', hidden: true }) as HTMLButtonElement;

		async function open(): Promise<void> {
			answers['GET /api/admin/backups'] = [
				{ status: 200, body: status({ backups: [backup(name, '2026-11-17T04:00:00+00:00')] }) }
			];
			navCounts.review = 4;
			render(BackupsPage);
			await fireEvent.click(await screen.findByRole('button', { name: `Restore ${name}` }));
		}

		it('every backup row has a Restore button that opens the danger confirm with the warnings', async () => {
			await open();

			const text = modal().textContent!;
			expect(text).toContain(name);
			expect(text).toContain(formatDateTime('2026-11-17T04:00:00+00:00'));
			expect(text).toContain('Every table in the database is replaced');
			expect(text).toContain('safety backup of the current database is taken first');
			expect(text).toContain('Everyone, including you, is signed out');
			expect(text).toContain('Integrity → Run quick');
			expect(confirm().className).toContain('btn-danger');
			expect(calls.some((c) => c.key.includes('/restore'))).toBe(false);
		});

		it('confirm posts the encoded name, forgets the session and goes to /login', async () => {
			answers[restoreKey] = [
				{ status: 200, body: { restored: name, safetyBackup: 'rnrommanager-20261117-050000.zip' } }
			];
			await open();

			await fireEvent.click(confirm());

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/login', { replaceState: true }));
			expect(calls.map((c) => c.key)).toContain(restoreKey);
			expect(auth.me).toBeNull();
			expect(navCounts.review).toBeNull();
			expect(toasts.items.at(-1)!.kind).toBe('success');
			expect(toasts.items.at(-1)!.text).toContain('rnrommanager-20261117-050000.zip');
			// The cached antiforgery call is forgotten: the next POST fetches a fresh token.
			calls = [];
			await api.post('/api/auth/logout');
			expect(calls.map((c) => c.key)).toEqual([
				'GET /api/auth/antiforgery',
				'POST /api/auth/logout'
			]);
		});

		it.each([
			['JobRunning', 409, 'Another job is running', undefined],
			[
				'BackupTooNew',
				409,
				'The backup is from a newer app version',
				'Backup x is from a newer app version (format version 2).'
			],
			['Busy', 409, "Other requests didn't finish in time; try again", undefined],
			['RestoreFailed', 500, 'The restore failed', 'Restore the safety backup y.zip.']
		])('%s is shown in the modal and the page stays', async (code, status, title, detail) => {
			answers[restoreKey] = [{ status, body: { status, code, title, detail } }];
			await open();

			await fireEvent.click(confirm());

			const alert = await vi.waitFor(() => within(modal()).getByRole('alert', { hidden: true }));
			expect(alert.textContent).toContain(title);
			if (detail) expect(alert.textContent).toContain(detail);
			expect(goto).not.toHaveBeenCalled();
			expect(auth.me).not.toBeNull();
		});

		it.each([502, 504])(
			'a response lost behind a proxy (%i) warns that it may still be running',
			async (code) => {
				answers[restoreKey] = [{ status: code }];
				await open();

				await fireEvent.click(confirm());

				await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/login', { replaceState: true }));
				expect(auth.me).toBeNull();
				expect(toasts.items.at(-1)).toMatchObject({
					kind: 'warning',
					text: 'The restore may still be running. Sign in again in a minute, then check Backups.'
				});
			}
		);

		it('a network error does the same', async () => {
			await open();
			const fetcher = globalThis.fetch;
			vi.stubGlobal('fetch', (url: string, init?: RequestInit) =>
				`${init?.method ?? 'GET'} ${url}` === restoreKey
					? Promise.reject(new TypeError('Failed to fetch'))
					: fetcher(url, init)
			);

			await fireEvent.click(confirm());

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/login', { replaceState: true }));
			expect(toasts.items.at(-1)!.kind).toBe('warning');
		});
	});
});
