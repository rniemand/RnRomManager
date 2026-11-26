import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

const hides = vi.hoisted(() => [] as true[]);
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {
			hides.push(true);
		}
		dispose() {}
	}
}));

const tooltips = vi.hoisted(() => new Map<Element, string>());
vi.mock('#lib/tooltip.js', () => ({
	tooltip: (node: Element, text: string) => {
		tooltips.set(node, text);
		return {};
	}
}));

const { default: RomStoragePage } = await import('./rom-storage/+page.svelte');

const rows = (ndsEmbed = false, pending = 0, gbaLevel = 'Med') => [
	{
		id: 'gba',
		displayName: 'Game Boy Advance',
		defaultEmbedRoms: true,
		embedRoms: true,
		configurable: false,
		pendingGames: 0,
		gameCount: 1234,
		zipCompression: gbaLevel,
		compressionConfigurable: true
	},
	{
		id: 'nds',
		displayName: 'Nintendo DS',
		defaultEmbedRoms: false,
		embedRoms: ndsEmbed,
		configurable: true,
		pendingGames: pending,
		gameCount: 0,
		zipCompression: 'Med',
		compressionConfigurable: false
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
		expect(within(gba).getAllByRole('img', { name: 'Stored in ZIP' })).toHaveLength(2);
		expect(within(gba).queryByRole('button', { name: /^Store (in|beside) ZIP$/ })).toBeNull();
		const nds = await row('Nintendo DS');
		expect(within(nds).getAllByRole('img', { name: 'Beside ZIP' })).toHaveLength(2);
		expect(nds.textContent).toContain('2 games still to move');
		expect(within(nds).getByRole('button', { name: 'Store in ZIP' })).toBeTruthy();
		const [table] = screen.getAllByRole('table');
		const headers = (i: number) =>
			[...table.querySelectorAll('thead tr')[i].querySelectorAll('th')].map((h) =>
				h.textContent?.trim()
			);
		expect(headers(0)).toEqual(['System', 'ROM storage', 'ZIP compression']);
		expect(headers(1)).toEqual([
			'System',
			'Folder',
			'Games',
			'Default',
			'Configured',
			'Change',
			'Level',
			'Repack'
		]);
	});

	it('the Games column shows each system count, right-aligned (Z1)', async () => {
		render(RomStoragePage);
		const gba = await row('Game Boy Advance');
		const nds = await row('Nintendo DS');
		const games = (row: HTMLElement) => row.querySelectorAll('td')[2];

		expect(games(gba).textContent!.trim()).toBe('1,234');
		expect(games(gba).className).toBe('text-end');
		expect(games(nds).textContent!.trim()).toBe('0');
	});

	it('the storage icons carry their tooltips; an intro line explains the page', async () => {
		render(RomStoragePage);
		const gba = await row('Game Boy Advance');
		const nds = await row('Nintendo DS');

		const tip = (el: Element) => tooltips.get(el.parentElement!);
		expect(tip(within(gba).getAllByRole('img', { name: 'Stored in ZIP' })[0])).toBe(
			"Stored in ZIP: ROMs are inside each game's ZIP"
		);
		expect(tip(within(nds).getAllByRole('img', { name: 'Beside ZIP' })[0])).toBe(
			"Beside ZIP: ROMs are separate files next to each game's ZIP"
		);
		expect(
			screen
				.getByText(/^Where each system keeps its ROMs/)
				.textContent?.replace(/\s+/g, ' ')
				.trim()
		).toBe(
			'Where each system keeps its ROMs and how its game ZIPs are compressed. Changing either rewrites every game of that system in a long-running job.'
		);
	});

	it('the Folder column shows the short code; the system badge links to the filtered Library', async () => {
		render(RomStoragePage);
		const gba = await row('Game Boy Advance');

		const code = within(gba).getByText('gba');
		expect(code.tagName).toBe('CODE');
		expect(tooltips.get(code)).toBe('Put ROMs in ingest/gba; stored in roms/gba');
		const link = within(gba).getByRole('link');
		expect(link.getAttribute('href')).toBe('/?system=gba');
		expect(tooltips.get(link)).toBe('Show Game Boy Advance in the Library');
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

	describe('ZIP compression (Z11–Z12)', () => {
		const paragraphs = [
			'Every game ZIP of Game Boy Advance is rewritten, one at a time. On a large library this takes a long time and uses a lot of disk and CPU. No other job runs and no game can be edited until it ends.',
			'If you cancel, some ZIPs keep the old level. They still play; use Repack again to finish.',
			"Each game needs free space about the size of its uncompressed ROMs while it's rewritten."
		];
		const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim();
		const dialog = (title: string) =>
			screen.getByRole('heading', { name: title, hidden: true }).closest('.modal') as HTMLElement;
		const edit = 'Edit ZIP compression of Game Boy Advance';
		const repack = 'Repack again Game Boy Advance ZIPs';

		async function chooseLevel(level: string): Promise<HTMLElement> {
			await fireEvent.click(await screen.findByRole('button', { name: edit }));
			const modal = dialog('ZIP compression: Game Boy Advance');
			await fireEvent.change(within(modal).getByLabelText('Compression level'), {
				target: { value: level }
			});
			return modal;
		}

		it('configurable rows show the level with Edit and Repack again; others show a dash', async () => {
			answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(false, 0, 'High') };
			render(RomStoragePage);

			const gba = await row('Game Boy Advance');
			const badge = within(gba).getByText('High');
			expect(badge.className).toBe('badge text-bg-success');
			const editButton = within(gba).getByRole('button', { name: edit });
			expect(editButton.textContent?.trim()).toBe('');
			expect(editButton.querySelector('i.bi-pencil')).toBeTruthy();
			expect(tooltips.get(editButton)).toBe('Edit compression level');
			const repackButton = within(gba).getByRole('button', { name: repack });
			expect(repackButton.hasAttribute('aria-label')).toBe(false);
			expect(repackButton.firstChild?.textContent?.trim()).toBe('Repack again');
			expect(repackButton.closest('td')).not.toBe(badge.closest('td'));
			const nds = await row('Nintendo DS');
			const cell = within(nds).getByText('Not applicable: ROMs are beside the ZIP').closest('td')!;
			expect(cell.textContent?.trim()).toBe('—Not applicable: ROMs are beside the ZIP');
			expect(within(nds).queryByRole('button', { name: /ZIP compression|Repack/ })).toBeNull();
		});

		it('each level shows as its coloured badge', async () => {
			for (const [value, label, cls] of [
				['Store', 'Store', 'text-bg-secondary'],
				['Med', 'Medium', 'text-bg-primary']
			]) {
				answers['GET /api/admin/rom-storage'] = { status: 200, body: rows(false, 0, value) };
				const { unmount } = render(RomStoragePage);
				const gba = await row('Game Boy Advance');
				expect(within(gba).getByText(label).className).toBe(`badge ${cls}`);
				unmount();
			}
		});

		it('Edit: the select is preselected, confirm waits for a change, PUT, then a toast links the job', async () => {
			answers['PUT /api/admin/zip-compression/gba'] = { status: 202, body: { jobId: 'j9' } };
			render(RomStoragePage);
			await fireEvent.click(await screen.findByRole('button', { name: edit }));

			const modal = dialog('ZIP compression: Game Boy Advance');
			const select = within(modal).getByLabelText('Compression level') as HTMLSelectElement;
			expect(select.value).toBe('Med');
			expect([...select.options].map((o) => [o.value, o.text])).toEqual([
				['Store', 'Store'],
				['Med', 'Medium'],
				['High', 'High']
			]);
			expect([...modal.querySelectorAll('.modal-body p')].map(text)).toEqual(paragraphs);
			const confirm = within(modal).getByRole('button', {
				name: 'Change and repack',
				hidden: true
			}) as HTMLButtonElement;
			expect(confirm.disabled).toBe(true);

			await fireEvent.change(select, { target: { value: 'Store' } });
			expect(confirm.disabled).toBe(false);
			await fireEvent.click(confirm);

			await vi.waitFor(() =>
				expect(calls).toContainEqual({
					key: 'PUT /api/admin/zip-compression/gba',
					body: { level: 'Store' }
				})
			);
			await vi.waitFor(() => expect(calls.map((c) => c.key)).toContain('GET /api/jobs/current'));
			await vi.waitFor(() =>
				expect(toasts.items.at(-1)).toMatchObject({
					kind: 'success',
					text: 'Repacking Game Boy Advance ZIPs',
					link: { href: '/jobs/j9', text: 'View job' }
				})
			);
		});

		it('confirm closes the modal and sends the request', async () => {
			render(RomStoragePage);
			const modal = await chooseLevel('High');
			hides.length = 0;
			await fireEvent.click(
				within(modal).getByRole('button', { name: 'Change and repack', hidden: true })
			);

			await vi.waitFor(() =>
				expect(calls.map((c) => c.key)).toContain('PUT /api/admin/zip-compression/gba')
			);
			expect(hides.length).toBeGreaterThan(0);
		});

		it('a 204 reloads the rows', async () => {
			render(RomStoragePage);
			const modal = await chooseLevel('High');
			await fireEvent.click(
				within(modal).getByRole('button', { name: 'Change and repack', hidden: true })
			);

			await vi.waitFor(() =>
				expect(calls.filter((c) => c.key === 'GET /api/admin/rom-storage')).toHaveLength(2)
			);
		});

		it('Repack again: its own modal (no select) POSTs; a 409 shows the job-running toast', async () => {
			answers['POST /api/admin/zip-compression/gba/repack'] = {
				status: 409,
				body: { status: 409, code: 'JobRunning', title: 'A job is running' }
			};
			render(RomStoragePage);
			await fireEvent.click(await screen.findByRole('button', { name: repack }));

			const modal = dialog('Repack Game Boy Advance ZIPs');
			expect(within(modal).queryByLabelText('Compression level')).toBeNull();
			expect([...modal.querySelectorAll('.modal-body p')].map(text)).toEqual(paragraphs);
			await fireEvent.click(
				within(modal).getByRole('button', { name: 'Repack again', hidden: true })
			);

			await vi.waitFor(() =>
				expect(calls.map((c) => c.key)).toContain('POST /api/admin/zip-compression/gba/repack')
			);
			await vi.waitFor(() =>
				expect(toasts.items.at(-1)?.text).toBe(
					'Another job is running. Try again when it has finished.'
				)
			);
		});

		it('Repack again: a 202 toasts a link to the job page', async () => {
			answers['POST /api/admin/zip-compression/gba/repack'] = {
				status: 202,
				body: { jobId: 'j7' }
			};
			render(RomStoragePage);
			await fireEvent.click(await screen.findByRole('button', { name: repack }));
			await fireEvent.click(screen.getByRole('button', { name: 'Repack again', hidden: true }));

			await vi.waitFor(() =>
				expect(toasts.items.at(-1)).toMatchObject({
					text: 'Repacking Game Boy Advance ZIPs',
					link: { href: '/jobs/j7', text: 'View job' }
				})
			);
		});

		it('other errors show the error alert', async () => {
			answers['POST /api/admin/zip-compression/gba/repack'] = {
				status: 400,
				body: {
					status: 400,
					code: 'NotConfigurable',
					title: "This system's ZIP compression can't be changed."
				}
			};
			render(RomStoragePage);
			await fireEvent.click(await screen.findByRole('button', { name: repack }));
			await fireEvent.click(screen.getByRole('button', { name: 'Repack again', hidden: true }));

			expect(
				await screen.findByText("This system's ZIP compression can't be changed.")
			).toBeTruthy();
		});

		it('Edit and Repack again are disabled while a job runs', async () => {
			answers['GET /api/jobs/current'] = { status: 200, body: running };
			render(RomStoragePage);

			await vi.waitFor(() =>
				expect((screen.getByRole('button', { name: edit }) as HTMLButtonElement).disabled).toBe(
					true
				)
			);
			expect((screen.getByRole('button', { name: repack }) as HTMLButtonElement).disabled).toBe(
				true
			);
		});

		it('the legend lists the three levels with their pinned texts', async () => {
			render(RomStoragePage);
			await row('Game Boy Advance');

			expect(screen.getByRole('heading', { name: 'ZIP compression levels' })).toBeTruthy();
			const legend = screen.getAllByRole('table')[1];
			expect(within(legend).getAllByRole('columnheader').map(text)).toEqual([
				'Level',
				'Typical size',
				'Speed',
				'Use for'
			]);
			expect(
				within(legend)
					.getAllByRole('row')
					.slice(1)
					.map((r) => [...r.children].map(text))
			).toEqual([
				[
					'Store',
					'~100% of the ROM size (no compression)',
					'Fastest to read and write, almost no CPU',
					"Large ROMs that don't compress well (DS), or when fewer files on disk is the goal"
				],
				[
					'MediumDefault',
					'About 40–90% of the ROM size, depending on the ROM',
					'Fast',
					"Most systems; today's behaviour"
				],
				[
					'High',
					'A few percent smaller than Medium',
					'Writes several times slower, reads like Medium',
					'Small cartridge ROMs where space matters most'
				]
			]);
			const levelCells = within(legend)
				.getAllByRole('row')
				.slice(1)
				.map((r) => r.children[0]);
			expect(levelCells.map((c) => c.querySelector('.badge')!.className)).toEqual([
				'badge text-bg-secondary',
				'badge text-bg-primary',
				'badge text-bg-success'
			]);
			expect(levelCells[1].querySelectorAll('.badge')[1].className).toBe(
				'badge rounded-pill text-bg-light border ms-1'
			);
			expect(
				within(legend)
					.getAllByRole('row')
					.slice(1)
					.map((r) => r.children[2].querySelector('i')!.className)
			).toEqual([
				'bi bi-lightning-charge me-1',
				'bi bi-lightning me-1',
				'bi bi-hourglass-split me-1'
			]);
			expect(screen.getByText('These numbers are typical estimates, not guarantees.')).toBeTruthy();
		});
	});
});
