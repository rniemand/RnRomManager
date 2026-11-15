import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { DiscoveryGroup } from '../api/types';
import { toasts } from '../stores/toasts.svelte';

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

const { default: PublisherLogoModal } = await import('./PublisherLogoModal.svelte');
const { default: DiscoveryGroups } = await import('./DiscoveryGroups.svelte');

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
afterEach(() => vi.unstubAllGlobals());

const modal = () => within(document.querySelector<HTMLElement>('.modal')!);
const button = (name: string | RegExp) =>
	modal().getByRole('button', { name, hidden: true }) as HTMLButtonElement;
const fileInput = () => modal().getByLabelText('Logo image file') as HTMLInputElement;
const writes = () => calls.map((c) => c.key).filter((k) => !k.startsWith('GET'));
const alertText = () => modal().getByRole('alert', { hidden: true }).textContent;
const png = (size = 10) => new File([new Uint8Array(size)], 'logo.png', { type: 'image/png' });

async function choose(file: File): Promise<void> {
	await fireEvent.change(fileInput(), { target: { files: [file] } });
}

describe('PublisherLogoModal (discovery B5)', () => {
	it.each([
		[publisher({ logoUrl: `${logo}&v=2` }), 'Shared logo, set by a user.', `${logo}&v=2`],
		[publisher(), 'Starter art bundled with RnRomManager.', '/publishers/nintendo.svg'],
		[publisher({ key: hudson, name: 'Hudson' }), 'No logo: the name shows as text.', null]
	])('says where the current logo comes from (%#)', (group, text, src) => {
		render(PublisherLogoModal, { open: true, publisher: group, onchanged: vi.fn() });

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
		render(PublisherLogoModal, { open: true, publisher: publisher(), onchanged: vi.fn() });
		if (file) await choose(file);

		await fireEvent.click(button('Save logo'));

		expect(alertText()).toContain(message);
		expect(fileInput().classList.contains('is-invalid')).toBe(true);
		expect(writes()).toEqual([]);
	});

	it('uploads the file, toasts and asks for a refresh', async () => {
		const onchanged = vi.fn();
		render(PublisherLogoModal, { open: true, publisher: publisher(), onchanged });
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
		render(PublisherLogoModal, { open: true, publisher: publisher(), onchanged });
		await choose(png());

		await fireEvent.click(button('Save logo'));

		await vi.waitFor(() => expect(alertText()).toContain(message));
		expect(onchanged).not.toHaveBeenCalled();
	});

	it('removes a shared logo; without one there is nothing to remove', async () => {
		const onchanged = vi.fn();
		const view = render(PublisherLogoModal, {
			open: true,
			publisher: publisher({ logoUrl: `${logo}&v=2` }),
			onchanged
		});

		await fireEvent.click(button(/Remove shared logo/));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledOnce());
		expect(writes()).toEqual([`DELETE ${logo}`]);
		expect(toasts.items.map((t) => t.text)).toContain("Nintendo's shared logo was removed.");

		await view.rerender({ open: true, publisher: publisher(), onchanged });
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

	it('genre rows have no logo button', async () => {
		answers['GET /api/discovery/genres?pageSize=20'] = [
			{
				status: 200,
				body: {
					items: [{ key: hudson, name: 'Puzzle', gameCount: 1 }],
					page: 1,
					pageSize: 20,
					total: 1
				}
			}
		];
		render(DiscoveryGroups, {
			view: { kind: 'genres', key: null, groupPage: 1, gamePage: 1 },
			onpage: vi.fn()
		});

		await screen.findByRole('link', { name: /Puzzle/ });
		expect(screen.queryByRole('button', { name: /Change logo/ })).toBeNull();
	});
});
