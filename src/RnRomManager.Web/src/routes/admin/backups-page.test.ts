import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { BackupStatus } from '#lib/api/types.js';
import { formatDateTime } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

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
		const table = document.querySelector('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(
			screen.getByRole('link', { name: 'How to restore a backup' }).getAttribute('href')
		).toContain('docs/deployment.md#backup-and-restore');
	});

	it('shows Disabled and an empty state when there is nothing', async () => {
		answers['GET /api/admin/backups'] = [
			{ status: 200, body: status({ enabled: false, nextRunAt: null, backups: [] }) }
		];
		render(BackupsPage);

		expect(await screen.findByText('No backups yet.')).toBeTruthy();
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
});
