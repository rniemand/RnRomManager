import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
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
				details: 'release a.gb',
				gameTitle: 'Tetris'
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

	it('places the Auditing tab last, after Logs; there is no About tab (B12)', () => {
		render(AdminLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		const links = screen.getAllByRole('link');
		expect(links.map((link) => link.textContent?.trim())).toEqual([
			'Users',
			'DATs',
			'Settings',
			'ROM Zip Config',
			'BIOS info',
			'Actions',
			'Backups',
			'Logs',
			'Auditing'
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
		const [link] = await screen.findAllByRole('link', { name: 'Tetris' });
		expect(link.getAttribute('href')).toBe(`/games/${game}`);
		expect(latest().toString()).toBe('page=1&pageSize=100');
		expect(
			within(document.querySelector('tbody')!).getByText('System', { selector: 'em' })
		).toBeTruthy();
		const change = document.querySelector('tbody tr td:last-child')!;
		expect(
			[...change.children].map((line) => line.textContent!.replace(/\s+/g, ' ').trim())
		).toEqual(['ingest/a.gb → roms/a.zip', 'release a.gb']);
		expect(change.querySelector('[title]')).toBeNull();
		expect(
			within(document.querySelector('tbody')!).getByText('Game Boy', { selector: '.badge' })
		).toBeTruthy();
	});

	/** B25: the toolbar dropdown whose toggle starts with `field`. */
	const toggle = (field: string) =>
		screen
			.getAllByRole('button')
			.find(
				(b) => b.classList.contains('dropdown-toggle') && b.textContent!.trim().startsWith(field)
			)!;
	const menuItem = (field: string, text: string) =>
		[
			...toggle(field).closest('.dropdown')!.querySelectorAll<HTMLButtonElement>('.dropdown-item')
		].find((b) => b.textContent!.trim().endsWith(text))!;

	it.each([
		['Action', 'action', 'GameUpdated'],
		['System', 'system', 'gb'],
		['User', 'user', 'boss'],
		['Search', 'q', 'roms/a']
	])('maps %s to the API and URL, resetting the page', async (label, key, value) => {
		navigateTo('/admin/audit?page=2');
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });
		if (label === 'Action') await fireEvent.click(menuItem('Action', 'GameUpdated'));
		else if (label === 'System') await fireEvent.click(menuItem('System', 'Game Boy'));
		else
			await fireEvent.input(screen.getByLabelText(label, { exact: true }), { target: { value } });
		await vi.waitFor(() => expect(latest().get(key)).toBe(value));
		expect(testUrl.current.searchParams.get(key)).toBe(value);
		expect(latest().get('page')).toBe('1');
		expect(goto).toHaveBeenLastCalledWith(expect.any(String), { replace: true, reset: false });
	});

	it('sends local midnights, with To exclusive on the next local day', async () => {
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });
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
		await screen.findAllByRole('link', { name: 'Tetris' });
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
		await screen.findAllByRole('link', { name: 'Tetris' });
		expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('zip');
		const top = screen.getByRole('navigation', { name: 'Pages, top' });
		await fireEvent.click(within(top).getByRole('button', { name: 'Next' }));
		await vi.waitFor(() => expect(latest().get('page')).toBe('3'));
		expect(testUrl.current.searchParams.get('page')).toBe('3');
		await fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
		await vi.waitFor(() => expect(latest().toString()).toBe('page=1&pageSize=100'));
		expect(testUrl.current.search).toBe('');
		expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('');
	});

	it('the filters are one toolbar of dropdowns, each toggle showing its value (B25)', async () => {
		navigateTo('/admin/audit?action=Ingested&system=gb&user=boss&from=2026-11-01&to=2026-11-02');
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });

		const form = screen.getByRole('search');
		expect(form.classList.contains('flex-wrap')).toBe(true);
		const toggles = [...form.querySelectorAll('.dropdown-toggle')].map((b) =>
			b.textContent!.replace(/\s+/g, ' ').trim()
		);
		expect(toggles).toEqual([
			'Action: Ingested',
			'System: Game Boy',
			'User: boss',
			'Date: 2026-11-01 – 2026-11-02'
		]);
		expect(form.querySelectorAll('select')).toHaveLength(0);
		expect(screen.getByRole('button', { name: 'Clear' }).closest('form')).toBe(form);
	});

	it('an unfiltered toolbar shows the bare field names; Scheduled only names the User toggle', async () => {
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });
		expect(toggle('User').textContent!.trim()).toBe('User');
		expect(toggle('Date').textContent!.trim()).toBe('Date');
		expect(toggle('Action').classList.contains('btn-outline-secondary')).toBe(true);

		await fireEvent.click(screen.getByLabelText('Scheduled only'));
		await vi.waitFor(() =>
			expect(toggle('User').textContent!.replace(/\s+/g, ' ').trim()).toBe('User: Scheduled')
		);
		expect(toggle('User').classList.contains('btn-secondary')).toBe(true);
	});

	it('choosing All actions clears the action', async () => {
		navigateTo('/admin/audit?action=Ingested');
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });

		await fireEvent.click(menuItem('Action', 'All actions'));

		await vi.waitFor(() => expect(latest().has('action')).toBe(false));
		expect(testUrl.current.searchParams.has('action')).toBe(false);
	});

	it('wraps a long Details value instead of truncating it (B30)', async () => {
		const long = Array.from({ length: 40 }, (_, i) => `Nintendo - System ${i}.dat`).join(', ');
		items = [{ ...(items[0] as object), details: long }];
		render(AuditPage);

		const line = (await screen.findAllByText(long)).find((e) => e.closest('td'))!;
		const cell = line.closest('td')!;
		expect(cell.classList.contains('text-break')).toBe(true);
		expect(cell.classList.contains('text-truncate')).toBe(false);
		expect(line.getAttribute('title')).toBeNull();
	});

	it('below md shows one card per event with the same fields; the table is md-up (release editing B35)', async () => {
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });

		expect(document.querySelector('table')!.closest('.card')!.className).toBe(
			'card d-none d-md-block'
		);
		const list = document.querySelector('.d-md-none')!;
		const card = list.querySelector('.card.mb-2 > .card-body.py-2')!;
		const [first, second, third] = [...card.children] as HTMLElement[];
		expect(first.querySelector('strong')!.textContent).toBe('Ingested');
		expect(within(first).getByText('Game Boy', { selector: '.badge' })).toBeTruthy();
		const time = first.querySelector('.ms-auto')!;
		expect(time.className).toContain('small text-body-secondary');
		expect(time.textContent).toBe(document.querySelector('tbody td')!.textContent);
		expect(within(second).getByText('System', { selector: 'em' })).toBeTruthy();
		expect(within(second).getByRole('link', { name: 'Tetris' }).getAttribute('href')).toBe(
			`/games/${game}`
		);
		expect(third.className).toBe('small text-break');
		expect(third.textContent).toBe(document.querySelector('tbody tr td:last-child')!.textContent);
	});

	it('has five columns, the system badge under the action (B30)', async () => {
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });

		expect(screen.getAllByRole('columnheader').map((th) => th.textContent!.trim())).toEqual([
			'Time',
			'Action',
			'User',
			'Game',
			'Change'
		]);
		const action = document.querySelector('tbody tr td:nth-child(2)')!;
		expect(action.firstElementChild!.textContent).toBe('Ingested');
		expect(
			within(action as HTMLElement).getByText('Game Boy', { selector: '.badge' })
		).toBeTruthy();
	});

	it('a gone game shows its short id as plain text with the full id as title (B30)', async () => {
		items = [{ ...(items[0] as object), gameTitle: null }];
		render(AuditPage);

		const short = (await screen.findAllByText(game.slice(0, 8))).find((e) => e.closest('td'))!;
		expect(short.tagName).toBe('SPAN');
		expect(short.getAttribute('title')).toBe(game);
		expect(screen.queryByRole('link', { name: /0000/ })).toBeNull();
	});

	it('the Change cell shows → target alone, and — when path, target and details are null (B30)', async () => {
		items = [
			{ ...(items[0] as object), id: 1, path: null, targetPath: 'roms/b.zip', details: null },
			{ ...(items[0] as object), id: 2, gameId: null, path: null, targetPath: null, details: null }
		];
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });

		const cells = [...document.querySelectorAll('tbody tr')].map((row) =>
			[...row.querySelectorAll('td')]
				.slice(3)
				.map((td) => td.textContent!.replace(/\s+/g, ' ').trim())
		);
		expect(cells).toEqual([
			['Tetris', '→ roms/b.zip'],
			['—', '—']
		]);
	});

	it('paginates above and below with distinct labels, only with more than one page (B30)', async () => {
		total = 201;
		const { unmount } = render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });
		expect(screen.getAllByRole('navigation').map((n) => n.getAttribute('aria-label'))).toEqual([
			'Pages, top',
			'Pages, bottom'
		]);
		const top = screen.getByRole('navigation', { name: 'Pages, top' });
		expect(
			top.compareDocumentPosition(document.querySelector('table')!) &
				Node.DOCUMENT_POSITION_FOLLOWING
		).toBeTruthy();
		unmount();

		total = 1;
		render(AuditPage);
		await screen.findAllByRole('link', { name: 'Tetris' });
		expect(screen.queryByRole('navigation')).toBeNull();
	});

	it('no pagination when the list is empty (B30)', async () => {
		items = [];
		total = 0;
		render(AuditPage);
		await screen.findByText('No audit events found.');
		expect(screen.queryByRole('navigation')).toBeNull();
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
		expect(await screen.findAllByRole('link', { name: 'Tetris' })).toHaveLength(2);
	});
	it('requires admin and makes no requests for a plain user', async () => {
		auth.set({ id: 2, username: 'plain', roles: [], mustChangePassword: false });
		render(AuditPage);
		expect(await screen.findByText('Not allowed')).toBeTruthy();
		expect(calls).toEqual([]);
	});
});
