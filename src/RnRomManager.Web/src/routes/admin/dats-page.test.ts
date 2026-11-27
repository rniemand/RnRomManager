import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { DatRefreshSettings } from '#lib/types/index.js';
import { formatDateTime } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

const { default: DatsPage } = await import('./dats/+page.svelte');

type Answer = { status: number; body?: unknown };
const settings = (overrides: Partial<DatRefreshSettings> = {}): DatRefreshSettings => ({
	enabled: true,
	time: '04:00',
	timeZone: 'Europe/Amsterdam',
	nextRunAt: '2026-11-18T03:00:00+00:00',
	...overrides
});

describe('DATs tab: automatic refresh (B28)', () => {
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		answers = {
			'GET /api/admin/dats': { status: 200, body: [] },
			'GET /api/admin/dats/settings': { status: 200, body: settings() },
			'GET /api/jobs/current': { status: 204 }
		};
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

	const time = () =>
		screen.findByLabelText('Time (server time, Europe/Amsterdam)') as Promise<HTMLInputElement>;

	it('loads the defaults: on at 04:00 with the next run', async () => {
		render(DatsPage);

		const input = await time();
		expect(input.value).toBe('04:00');
		expect(input.type).toBe('time');
		expect((screen.getByRole('switch', { name: 'Enabled' }) as HTMLInputElement).checked).toBe(
			true
		);
		expect(screen.getByTestId('dat-next-run').textContent!.trim()).toBe(
			`Next run: ${formatDateTime('2026-11-18T03:00:00+00:00')}`
		);
		expect(screen.getByRole('heading', { name: 'Automatic refresh' })).toBeTruthy();
	});

	it('Save sends PUT with the form, shows the new next run and a toast', async () => {
		render(DatsPage);
		await fireEvent.input(await time(), { target: { value: '05:15' } });
		await fireEvent.click(screen.getByRole('switch', { name: 'Enabled' }));
		answers['PUT /api/admin/dats/settings'] = {
			status: 200,
			body: settings({ enabled: false, time: '05:15', nextRunAt: null })
		};

		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		await vi.waitFor(() =>
			expect(screen.getByTestId('dat-next-run').textContent!.trim()).toBe('Next run: Disabled')
		);
		expect(calls.find((c) => c.key === 'PUT /api/admin/dats/settings')!.body).toEqual({
			enabled: false,
			time: '05:15'
		});
		expect(toasts.items.map((t) => t.text)).toContain('DAT refresh schedule saved.');
	});

	it('an invalid time is refused before sending, and a server time error shows under the field', async () => {
		render(DatsPage);
		const input = await time();
		await fireEvent.input(input, { target: { value: '' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		expect(screen.getByText('Use HH:mm, 00:00–23:59.')).toBeTruthy();
		expect(calls.some((c) => c.key.startsWith('PUT'))).toBe(false);

		await fireEvent.input(input, { target: { value: '04:30' } });
		answers['PUT /api/admin/dats/settings'] = {
			status: 400,
			body: {
				status: 400,
				code: 'ValidationFailed',
				title: 'Validation failed',
				errors: { time: ['Server says no.'] }
			}
		};
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));
		expect(await screen.findByText('Server says no.')).toBeTruthy();
		expect(input.classList.contains('is-invalid')).toBe(true);
	});

	it('Automatic refresh and DAT files are accordion panels, one open at a time, DAT files open (B28, A1)', async () => {
		render(DatsPage);
		await time();

		const schedule = screen.getByRole('button', { name: 'Automatic refresh' });
		const files = screen.getByRole('button', { name: 'DAT files' });
		expect([schedule, files].map((b) => b.getAttribute('aria-expanded'))).toEqual([
			'false',
			'true'
		]);
		const heading = screen.getByRole('heading', { level: 2, name: 'Automatic refresh' });
		expect(heading.id).toBe('dat-schedule');
		const form = document.querySelector('form[aria-labelledby="dat-schedule"]')!;
		expect(form.closest('#dat-schedule-panel')).not.toBeNull();
		expect(form.className).toBe('');
		expect(screen.getByRole('heading', { level: 2, name: 'DAT files' }).id).toBe('dat-files');

		await fireEvent.click(schedule);
		expect(schedule.getAttribute('aria-expanded')).toBe('true');
		expect(files.getAttribute('aria-expanded')).toBe('false');
		await fireEvent.click(schedule);
		expect([schedule, files].map((b) => b.getAttribute('aria-expanded'))).toEqual([
			'false',
			'false'
		]);
	});

	it('Refresh DATs heads the DAT files panel, before the list (A1)', async () => {
		render(DatsPage);
		const refresh = await screen.findByRole('button', { name: /Refresh DATs/ });

		const panel = refresh.closest('#dat-files-panel')!;
		expect(panel).not.toBeNull();
		const live = refresh.closest('[aria-live="polite"]')!;
		expect(live.parentElement!.firstElementChild).toBe(live);
	});

	it('each DAT row links to its download, name encoded (B29)', async () => {
		answers['GET /api/admin/dats'] = {
			status: 200,
			body: [{ datName: 'Nintendo - Game Boy', version: '1', downloadedAt: '2026-11-06T10:00:00Z' }]
		};
		render(DatsPage);

		const link = (await screen.findByRole('link', {
			name: 'Download Nintendo - Game Boy'
		})) as HTMLAnchorElement;
		expect(link.getAttribute('href')).toBe('/api/admin/dats/Nintendo%20-%20Game%20Boy/download');
		expect(link.hasAttribute('download')).toBe(true);
		expect(screen.getByRole('columnheader', { name: 'Actions' })).toBeTruthy();
	});
});
