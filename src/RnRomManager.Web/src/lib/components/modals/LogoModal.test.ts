import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { DiscoveryGroup } from '#lib/types/index.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: LogoModal } = await import('./LogoModal.svelte');
const { default: DiscoveryGroups } =
	await import('#lib/components/discovery/DiscoveryGroups.svelte');

const nintendo = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
const hudson = '1'.repeat(64);
const logo = `/api/discovery/publishers/logo?key=${nintendo}`;
const publisher = (extra: Partial<DiscoveryGroup> = {}): DiscoveryGroup => ({
	key: nintendo,
	name: 'Nintendo',
	gameCount: 3,
	logoUrl: null,
	...extra
});

type Reply = { status: number; body?: unknown };
let calls: { key: string; body: unknown }[];
let answers: Record<string, Reply[]>;

beforeEach(() => {
	resetClient();
	toasts.items.length = 0;
	calls = [];
	answers = {};
	const store = new Map<string, string>();
	vi.stubGlobal('localStorage', {
		getItem: (k: string) => store.get(k) ?? null,
		setItem: (k: string, value: string) => void store.set(k, value)
	});
	vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
		const key = `${init?.method ?? 'GET'} ${url}`;
		calls.push({ key, body: init?.body });
		const answer = answers[key]?.shift() ?? { status: 204 };
		const payload = answer.body === undefined ? null : JSON.stringify(answer.body);
		return Promise.resolve(new Response(payload, { status: answer.status }));
	});
});
afterEach(() => {
	vi.unstubAllGlobals();
	auth.set(null);
});

const modal = () => within(document.querySelector<HTMLElement>('.modal')!);
const button = (name: string | RegExp) =>
	modal().getByRole('button', { name, hidden: true }) as HTMLButtonElement;
const fileInput = () => modal().getByLabelText('Logo image file') as HTMLInputElement;
const writes = () => calls.map((c) => c.key).filter((k) => !k.startsWith('GET'));
const alertText = () => modal().getByRole('alert', { hidden: true }).textContent;
const png = (size = 10) => new File([new Uint8Array(size)], 'logo.png', { type: 'image/png' });

/** Switches the modal from its default URL source to File. */
async function useFile(): Promise<void> {
	await fireEvent.click(modal().getByLabelText('File', { exact: true }));
}

async function choose(file: File): Promise<void> {
	await fireEvent.change(fileInput(), { target: { files: [file] } });
}

describe('LogoModal (B19)', () => {
	it.each([
		[publisher({ logoUrl: `${logo}&v=2` }), 'Shared logo, set by a user.', `${logo}&v=2`],
		[publisher(), 'Starter art bundled with RnRomManager.', '/publishers/nintendo.svg'],
		[publisher({ key: hudson, name: 'Hudson' }), 'No logo: the name shows as text.', null]
	])('says where the current logo comes from (%#)', (group, text, src) => {
		render(LogoModal, { open: true, kind: 'publishers', item: group, onchanged: vi.fn() });

		expect(modal().getByText(text)).toBeTruthy();
		const image = modal().queryByRole('img', { name: /Current logo/, hidden: true });
		expect(image?.getAttribute('src') ?? null).toBe(src);
	});

	it.each([
		[null, 'Choose an image file.'],
		[
			new File(['<svg/>'], 'logo.svg', { type: 'image/svg+xml' }),
			'The logo must be a PNG or JPEG image.'
		],
		[png(512 * 1024 + 1), 'The logo must be at most 512 KiB.']
	])('checks the file before uploading (%#)', async (file, message) => {
		render(LogoModal, { open: true, kind: 'publishers', item: publisher(), onchanged: vi.fn() });
		await useFile();
		if (file) await choose(file);

		await fireEvent.click(button('Save logo'));

		expect(alertText()).toContain(message);
		expect(fileInput().classList.contains('is-invalid')).toBe(true);
		expect(writes()).toEqual([]);
	});

	it('uploads the file, toasts and asks for a refresh', async () => {
		const onchanged = vi.fn();
		render(LogoModal, { open: true, kind: 'publishers', item: publisher(), onchanged });
		await useFile();
		const file = png(512 * 1024);
		await choose(file);

		await fireEvent.click(button('Save logo'));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledOnce());
		const put = calls.find((c) => c.key === `PUT ${logo}`)!;
		expect((put.body as FormData).get('file')).toBe(file);
		expect(toasts.items.map((t) => [t.kind, t.text])).toContainEqual([
			'success',
			"Nintendo's logo was saved."
		]);
	});

	it.each([
		[
			{
				status: 400,
				body: {
					status: 400,
					code: 'ValidationFailed',
					title: 'Validation failed',
					errors: { file: ['The logo must be at most 2048 pixels wide and high.'] }
				}
			},
			'The logo must be at most 2048 pixels wide and high.'
		],
		[
			{ status: 413, body: { status: 413, code: 'PayloadTooLarge', title: 'Too large' } },
			'The logo must be at most 512 KiB.'
		],
		[
			{ status: 404, body: { status: 404, code: 'NotFound', title: 'Not found' } },
			'This publisher is no longer in the library.'
		]
	])('shows the server refusal on the file field (%#)', async (reply, message) => {
		const onchanged = vi.fn();
		answers[`PUT ${logo}`] = [reply];
		render(LogoModal, { open: true, kind: 'publishers', item: publisher(), onchanged });
		await useFile();
		await choose(png());

		await fireEvent.click(button('Save logo'));

		await vi.waitFor(() => expect(alertText()).toContain(message));
		expect(onchanged).not.toHaveBeenCalled();
	});

	it('removes a shared logo; without one there is nothing to remove', async () => {
		const onchanged = vi.fn();
		const view = render(LogoModal, {
			open: true,
			kind: 'publishers',
			item: publisher({ logoUrl: `${logo}&v=2` }),
			onchanged
		});

		await fireEvent.click(button(/Remove shared logo/));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledOnce());
		expect(writes()).toEqual([`DELETE ${logo}`]);
		expect(toasts.items.map((t) => t.text)).toContain("Nintendo's shared logo was removed.");

		await view.rerender({ open: true, kind: 'publishers', item: publisher(), onchanged });
		expect(modal().queryByRole('button', { name: /Remove shared logo/, hidden: true })).toBeNull();
	});

	it('from the publisher list: saving refetches the page, so the new logo shows at once', async () => {
		const page = (logoUrl: string | null) => ({
			status: 200,
			body: { items: [publisher({ logoUrl })], page: 1, pageSize: 20, total: 1 }
		});
		answers['GET /api/discovery/publishers?pageSize=20'] = [
			page(null),
			page(`${logo}&v=7`),
			page(null)
		];
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		render(DiscoveryGroups, {
			view: { kind: 'publishers', key: null, groupPage: 1, gamePage: 1 },
			onpage: vi.fn()
		});
		const art = async () =>
			(await screen.findByRole('link', { name: /Nintendo/ }))
				.querySelector('img')!
				.getAttribute('src');
		expect(await art()).toBe('/publishers/nintendo.svg');

		await fireEvent.click(screen.getByRole('button', { name: 'Change logo for Nintendo' }));
		await useFile();
		await choose(png());
		await fireEvent.click(button('Save logo'));
		await vi.waitFor(async () => expect(await art()).toBe(`${logo}&v=7`));

		await fireEvent.click(screen.getByRole('button', { name: 'Change logo for Nintendo' }));
		await fireEvent.click(button(/Remove shared logo/));
		await vi.waitFor(async () => expect(await art()).toBe('/publishers/nintendo.svg'));
		expect(calls.map((c) => c.key).filter((k) => k !== 'GET /api/auth/antiforgery')).toEqual([
			'GET /api/discovery/publishers?pageSize=20',
			`PUT ${logo}`,
			'GET /api/discovery/publishers?pageSize=20',
			`DELETE ${logo}`,
			'GET /api/discovery/publishers?pageSize=20'
		]);
	});

	it('opens on URL each time, and File can be chosen', async () => {
		const view = render(LogoModal, {
			open: true,
			kind: 'publishers',
			item: publisher(),
			onchanged: vi.fn()
		});
		const url = () => modal().getByLabelText('URL', { exact: true }) as HTMLInputElement;

		expect(url().checked).toBe(true);
		expect(modal().getByLabelText('Logo image URL')).toBeTruthy();
		expect(modal().queryByLabelText('Logo image file')).toBeNull();

		await useFile();
		expect(fileInput()).toBeTruthy();
		expect(modal().queryByLabelText('Logo image URL')).toBeNull();

		await view.rerender({
			open: false,
			kind: 'publishers',
			item: publisher(),
			onchanged: vi.fn()
		});
		await view.rerender({
			open: true,
			kind: 'publishers',
			item: publisher(),
			onchanged: vi.fn()
		});
		expect(url().checked).toBe(true);
	});

	it.each(['ftp://example.com/a.png', 'logo.png', ''])(
		'checks the URL before sending (%j)',
		async (value) => {
			render(LogoModal, {
				open: true,
				kind: 'publishers',
				item: publisher(),
				onchanged: vi.fn()
			});
			await fireEvent.input(modal().getByLabelText('Logo image URL'), { target: { value } });

			await fireEvent.click(button('Save logo'));

			expect(alertText()).toContain('Enter an http or https image URL.');
			expect(writes()).toEqual([]);
		}
	);

	it.each([
		['publishers', nintendo],
		['genres', hudson],
		['systems', 'gb']
	] as const)('saves a %s logo from a URL to its own route', async (kind, key) => {
		const onchanged = vi.fn();
		render(LogoModal, {
			open: true,
			kind,
			item: { key, name: 'Thing', logoUrl: null },
			onchanged
		});
		await fireEvent.input(modal().getByLabelText('Logo image URL'), {
			target: { value: ' https://example.com/logo.png ' }
		});

		await fireEvent.click(button('Save logo'));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledOnce());
		const put = calls.find((c) => c.key === `PUT /api/discovery/${kind}/logo/from-url?key=${key}`)!;
		expect(JSON.parse(put.body as string)).toEqual({ url: 'https://example.com/logo.png' });
		expect(toasts.items.map((t) => t.text)).toContain("Thing's logo was saved.");
	});

	it.each([
		['genres', hudson],
		['developers', hudson],
		['systems', 'gb']
	] as const)('uploads and removes a %s logo on its own route', async (kind, key) => {
		const onchanged = vi.fn();
		const shared = `/api/discovery/${kind}/logo?key=${key}`;
		render(LogoModal, {
			open: true,
			kind,
			item: { key, name: 'Thing', logoUrl: `${shared}&v=1` },
			onchanged
		});
		await useFile();
		await choose(png());
		await fireEvent.click(button('Save logo'));
		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledOnce());

		await fireEvent.click(button(/Remove shared logo/));
		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledTimes(2));
		expect(writes()).toEqual([`PUT ${shared}`, `DELETE ${shared}`]);
	});

	it.each([
		[
			{
				status: 400,
				body: {
					status: 400,
					code: 'ValidationFailed',
					title: 'Validation failed',
					errors: { url: ['The logo must be a PNG or JPEG image.'] }
				}
			},
			'The logo must be a PNG or JPEG image.'
		],
		[
			{
				status: 400,
				body: {
					status: 400,
					code: 'ImageUrlBlocked',
					title: 'The URL points to a private or local network address.'
				}
			},
			'The URL points to a private or local network address.'
		],
		[
			{
				status: 502,
				body: {
					status: 502,
					code: 'ImageDownloadFailed',
					title: "Couldn't download the image: HTTP 404."
				}
			},
			"Couldn't download the image: HTTP 404."
		],
		[
			{ status: 404, body: { status: 404, code: 'NotFound', title: 'Not found' } },
			'This genre is no longer in the library.'
		]
	])('shows the server refusal of a URL under the field (%#)', async (reply, message) => {
		const onchanged = vi.fn();
		answers[`PUT /api/discovery/genres/logo/from-url?key=${hudson}`] = [reply];
		render(LogoModal, {
			open: true,
			kind: 'genres',
			item: { key: hudson, name: 'Puzzle' },
			onchanged
		});
		await fireEvent.input(modal().getByLabelText('Logo image URL'), {
			target: { value: 'https://example.com/a.png' }
		});

		await fireEvent.click(button('Save logo'));

		await vi.waitFor(() => expect(alertText()).toContain(message));
		expect(onchanged).not.toHaveBeenCalled();
	});

	it('a system without a shared logo says the name shows as text', () => {
		render(LogoModal, {
			open: true,
			kind: 'systems',
			item: { key: 'gb', name: 'Game Boy', logoUrl: null },
			onchanged: vi.fn()
		});

		expect(modal().getByText('No logo: the name shows as text.')).toBeTruthy();
		expect(modal().queryByRole('img', { name: /Current logo/, hidden: true })).toBeNull();
	});
});
