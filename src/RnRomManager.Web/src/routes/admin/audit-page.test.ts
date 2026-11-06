import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { api } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

const goto = vi.fn((url: string) => navigateTo(url));
vi.mock('$app/navigation', () => ({ goto: (...args: [string]) => goto(...args) }));
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
const { default: AuditPage } = await import('./audit/+page.svelte');
const { default: AdminLayout } = await import('./+layout.svelte');

describe('Auditing', () => {
	let calls: string[];
	let items: object[];
	let total: number;
	let fail: boolean;
	const game = '00000000-0000-0000-0000-000000000123';
	const latest = () =>
		new URL(calls.filter((url) => url.startsWith('/api/admin/audit?')).at(-1)!, 'http://localhost')
			.searchParams;
	beforeEach(() => {
		navigateTo('/admin/audit');
		goto.mockClear();
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		calls = [];
		total = 1;
		fail = false;
		items = [
			{
				id: 1,
				occurredAt: '2026-11-06T12:00:00Z',
				action: 'Ingested',
				system: 'gb',
				gameId: game,
				user: null,
				path: 'ingest/a.gb',
				targetPath: 'roms/a.zip',
				details: 'release a.gb'
			}
		];
		vi.spyOn(api, 'get').mockImplementation(async (url) => {
			calls.push(url);
			if (url === '/api/systems') return [{ id: 'gb', displayName: 'Game Boy' }];
			if (url === '/api/admin/audit/actions') return ['Ingested', 'GameUpdated'];
			if (fail) throw new Error('offline');
			return {
				items,
				page: Number(new URL(url, 'http://localhost').searchParams.get('page')),
				pageSize: 100,
				total
			};
		});
	});
	afterEach(() => {
		vi.restoreAllMocks();
		auth.set(null);
	});

	it('places the Auditing tab between Logs and About', () => {
		render(AdminLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		const links = screen.getAllByRole('link');
		expect(links.map((link) => link.textContent?.trim())).toEqual([
			'Users',
			'DATs',
			'Settings',
			'Logs',
			'Auditing',
			'About'
		]);
		expect(screen.getByRole('link', { name: 'Auditing' }).getAttribute('href')).toBe(
			'/admin/audit'
		);
		expect(screen.getByRole('link', { name: 'Auditing' }).getAttribute('aria-current')).toBe(
			'page'
		);
	});

	it('loads 100 unfiltered rows, shows System for a null user, paths and the game link', async () => {
		render(AuditPage);
		const link = await screen.findByRole('link', { name: game });
		expect(link.getAttribute('href')).toBe(`/games/${game}`);
		expect(latest().toString()).toBe('page=1&pageSize=100');
		expect(screen.getByText('System', { selector: 'em' })).toBeTruthy();
		expect(screen.getByText('ingest/a.gb').getAttribute('title')).toBe('ingest/a.gb');
		expect(screen.getByText('Game Boy', { selector: '.badge' })).toBeTruthy();
	});

	it.each([
		['Action', 'action', 'GameUpdated'],
		['System', 'system', 'gb'],
		['User', 'user', 'boss'],
		['Search', 'q', 'roms/a']
	])('maps %s to the API and URL, resetting the page', async (label, key, value) => {
		navigateTo('/admin/audit?page=2');
		render(AuditPage);
		await screen.findByRole('link', { name: game });
		const input = screen.getByLabelText(label, { exact: true });
		if (label === 'Action' || label === 'System')
			await fireEvent.change(input, { target: { value } });
		else await fireEvent.input(input, { target: { value } });
		await vi.waitFor(() => expect(latest().get(key)).toBe(value));
		expect(testUrl.current.searchParams.get(key)).toBe(value);
		expect(latest().get('page')).toBe('1');
		expect(goto).toHaveBeenLastCalledWith(expect.any(String), { replace: true, reset: false });
	});

	it('sends local midnights, with To exclusive on the next local day', async () => {
		render(AuditPage);
		await screen.findByRole('link', { name: game });
		await fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-11-01' } });
		await vi.waitFor(() => expect(latest().get('from')).toBe(new Date(2026, 10, 1).toISOString()));
		await fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-11-01' } });
		await vi.waitFor(() => expect(latest().get('to')).toBe(new Date(2026, 10, 2).toISOString()));
		expect(testUrl.current.searchParams.get('from')).toBe('2026-11-01');
		expect(testUrl.current.searchParams.get('to')).toBe('2026-11-01');
	});

	it('scheduled only clears and disables User, including a pending debounce', async () => {
		navigateTo('/admin/audit?user=boss');
		render(AuditPage);
		await screen.findByRole('link', { name: game });
		await fireEvent.input(screen.getByLabelText('User', { exact: true }), {
			target: { value: 'other' }
		});
		await fireEvent.click(screen.getByLabelText('Scheduled only'));
		await vi.waitFor(() => expect(latest().get('scheduled')).toBe('true'));
		expect((screen.getByLabelText('User', { exact: true }) as HTMLInputElement).disabled).toBe(
			true
		);
		expect(latest().has('user')).toBe(false);
		expect(testUrl.current.searchParams.get('scheduled')).toBe('true');
		await new Promise((done) => setTimeout(done, 350));
		expect(latest().has('user')).toBe(false);
	});

	it('restores URL filters, pages and clears', async () => {
		total = 201;
		navigateTo('/admin/audit?action=Ingested&system=gb&q=zip&from=2026-11-01&to=2026-11-02&page=2');
		render(AuditPage);
		await screen.findByRole('link', { name: game });
		expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('zip');
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
		await vi.waitFor(() => expect(latest().get('page')).toBe('3'));
		expect(testUrl.current.searchParams.get('page')).toBe('3');
		await fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
		await vi.waitFor(() => expect(latest().toString()).toBe('page=1&pageSize=100'));
		expect(testUrl.current.search).toBe('');
		expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('');
	});

	it('shows an empty state', async () => {
		items = [];
		total = 0;
		render(AuditPage);
		expect(await screen.findByText('No audit events found.')).toBeTruthy();
	});
	it('retries failed requests', async () => {
		fail = true;
		render(AuditPage);
		await screen.findByRole('alert');
		fail = false;
		await fireEvent.click(screen.getByRole('button', { name: /Retry/ }));
		expect(await screen.findByRole('link', { name: game })).toBeTruthy();
	});
	it('requires admin and makes no requests for a plain user', async () => {
		auth.set({ id: 2, username: 'plain', roles: [], mustChangePassword: false });
		render(AuditPage);
		expect(await screen.findByText('Not allowed')).toBeTruthy();
		expect(calls).toEqual([]);
	});
});
