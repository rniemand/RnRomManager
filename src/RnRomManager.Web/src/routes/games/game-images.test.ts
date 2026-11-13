import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail, ImageInfo } from '#lib/api/types.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

// The page reads `?tab=` from the URL (GP1).
vi.mock('$app/state', () => ({
	page: { params: { id: 'g1' }, url: new URL('http://localhost/games/g1?tab=images') }
}));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		constructor(private el: HTMLElement) {}
		show() {
			this.el.classList.add('show');
		}
		hide() {
			this.el.classList.remove('show');
		}
		dispose() {}
	}
}));

// The release note button has a tooltip (R4); Bootstrap's Tooltip isn't mocked here.
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));
const { default: GamePage } = await import('./[id]/+page.svelte');
const { default: GameImages } = await import('#lib/components/GameImages.svelte');

const image = (id: number, type: ImageInfo['type'], extra: Partial<ImageInfo> = {}): ImageInfo => ({
	id,
	type,
	fileName: `${type === 'Snap' ? 'snap' : 'boxart'}-0${id}.png`,
	source: 'upload',
	isDefault: false,
	isCover: false,
	sizeBytes: 2048,
	width: 160,
	height: 144,
	url: `/api/games/g1/images/${id}`,
	...extra
});

const game = (images: ImageInfo[]): GameDetail => ({
	id: 'g1',
	system: 'gb',
	systemName: 'Game Boy',
	browserCore: null,
	title: 'Tetris',
	displayTitle: 'Tetris',
	releaseYear: null,
	developers: [],
	publishers: [],
	genres: [],
	description: null,
	hasDefaultBoxArt: false,
	rating: null,
	favourite: false,
	metadata: { createdAt: '', updatedAt: '' },
	roms: [],
	images
});

const placeholderAndSnap = () => [
	image(1, 'BoxArt', { isDefault: true, source: 'default', fileName: 'boxart-01.png' }),
	image(2, 'Snap', { isCover: true, fileName: 'snap-01.png' })
];

type Call = { key: string; body: unknown };

describe('game images', () => {
	let calls: Call[];
	let answers: Record<string, { status: number; body?: unknown }>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		auth.set({ id: 1, username: 'm', roles: ['rom-manager'], mustChangePassword: false });
		calls = [];
		answers = {};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			// B16: the game's collections (the badges); answered here so the queued answers stay as they were.
			if (url.endsWith('/collections')) return Promise.resolve(new Response('[]', { status: 200 }));
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url !== '/api/auth/antiforgery') calls.push({ key, body: init?.body });
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

	it('the top image on the page is the cover, even when it is a screenshot', async () => {
		answers['GET /api/games/g1'] = { status: 200, body: game(placeholderAndSnap()) };
		render(GamePage);

		const top = await screen.findByAltText('Tetris cover');
		expect(top.getAttribute('src')).toBe('/api/games/g1/images/2');
	});

	it('everyone sees the gallery with type, Cover and Placeholder badges, but no edit controls', () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		const cards = screen
			.getAllByRole('button', { name: /full size/ })
			.map((b) => b.closest('.card')!);
		expect(cards).toHaveLength(2);
		expect(within(cards[0] as HTMLElement).getByText('Box art')).toBeTruthy();
		expect(within(cards[0] as HTMLElement).getByText('Placeholder')).toBeTruthy();
		expect(within(cards[1] as HTMLElement).getByText('Screenshot')).toBeTruthy();
		expect(within(cards[1] as HTMLElement).getByText('Cover').className).toContain(
			'text-bg-primary'
		);
		expect(cards[1]!.textContent).toMatch(/2\.0 KiB · 160×144/);
		expect(screen.queryByLabelText('Upload an image')).toBeNull();
		expect(screen.queryByRole('button', { name: 'Upload image' })).toBeNull();
		expect(screen.queryByRole('button', { name: /Use as cover/ })).toBeNull();
		expect(screen.queryByRole('button', { name: /^Delete / })).toBeNull();
	});

	it('Upload image sits in the heading row, before the grid (G3)', () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		const upload = screen.getByRole('button', { name: 'Upload image' });
		const heading = screen.getByRole('heading', { name: 'Images' });
		expect(upload.parentElement).toBe(heading.parentElement);
		const grid = screen.getAllByRole('button', { name: /full size/ })[0].closest('.row')!;
		expect(upload.compareDocumentPosition(grid) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
		expect(grid.nextElementSibling).toBeNull();
	});

	it('editors get Use as cover on non-covers and Delete on everything but the placeholder', () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		expect(screen.getAllByRole('button', { name: /Use as cover/ })).toHaveLength(1);
		expect(screen.getByRole('button', { name: 'Use as cover: boxart-01.png' })).toBeTruthy();
		expect(
			screen.getAllByRole('button', { name: /^Delete / }).map((b) => b.getAttribute('aria-label'))
		).toEqual(['Delete snap-01.png']);
	});

	it('clicking a thumbnail opens it full size in an info modal', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		await fireEvent.click(screen.getByRole('button', { name: 'View snap-01.png full size' }));

		const full = screen.getByAltText('Tetris screenshot, full size');
		expect(full.getAttribute('src')).toBe('/api/games/g1/images/2');
		const footer = full.closest('.modal-content')!.querySelector('.modal-footer')!;
		expect([...footer.querySelectorAll('button')].map((b) => b.textContent?.trim())).toEqual([
			'Close'
		]);
	});

	const uploadModal = () => screen.getByLabelText('Upload an image').closest('.modal')!;
	const confirmUpload = () =>
		fireEvent.click(screen.getByRole('button', { name: 'Upload', hidden: true }));
	const choose = (file: File) =>
		fireEvent.change(screen.getByLabelText('Upload an image'), { target: { files: [file] } });
	const png = () => new File([new Uint8Array([1, 2, 3])], 'art.png', { type: 'image/png' });

	it('the upload form lives in a modal opened by the Upload image button', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		expect(uploadModal().classList.contains('show')).toBe(false);
		expect(uploadModal().querySelector('.modal-title')!.textContent).toBe('Upload image');
		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		expect(uploadModal().classList.contains('show')).toBe(true);
	});

	it('upload posts FormData, closes the modal, resets the fields and hands back the fresh detail', async () => {
		const fresh = game([image(5, 'BoxArt', { isCover: true })]);
		answers['POST /api/games/g1/images'] = { status: 200, body: fresh };
		const onchanged = vi.fn();
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		await choose(png());
		await fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Snap' } });
		await confirmUpload();

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledWith(fresh));
		const body = calls.find((c) => c.key === 'POST /api/games/g1/images')!.body as FormData;
		expect(body).toBeInstanceOf(FormData);
		expect((body.get('file') as File).name).toBe('art.png');
		expect(body.get('type')).toBe('Snap');
		await vi.waitFor(() => expect(uploadModal().classList.contains('show')).toBe(false));
		expect((screen.getByLabelText('Type') as HTMLSelectElement).value).toBe('BoxArt');
		expect((screen.getByLabelText('Upload an image') as HTMLInputElement).value).toBe('');
	});

	it('an empty submit shows the file error, keeps the modal open, and reopening clears it', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		await confirmUpload();

		expect(screen.getByText('Choose an image file.')).toBeTruthy();
		expect(uploadModal().classList.contains('show')).toBe(true);
		expect(calls.some((c) => c.key.startsWith('POST'))).toBe(false);

		await fireEvent.click(
			within(uploadModal() as HTMLElement).getByRole('button', { name: 'Cancel', hidden: true })
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		expect(screen.queryByText('Choose an image file.')).toBeNull();
	});

	const urlRadio = () => screen.getByLabelText('URL') as HTMLInputElement;
	const fileRadio = () => screen.getByLabelText('File') as HTMLInputElement;
	const urlInput = () => screen.getByLabelText('Image URL') as HTMLInputElement;
	const fileGroup = () => screen.getByLabelText('Upload an image').closest('div')!;
	const fallback = 'Download the image and upload the file instead.';

	async function openUrlMode(url: string): Promise<void> {
		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		await fireEvent.click(urlRadio());
		await fireEvent.input(urlInput(), { target: { value: url } });
	}

	it('opens on File; URL mode shows the URL field and hides the file input (U6)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));

		expect(fileRadio().checked).toBe(true);
		expect(fileRadio().className).toBe('btn-check');
		expect(screen.getByText('File').className).toBe('btn btn-outline-secondary btn-sm');
		expect(screen.queryByLabelText('Image URL')).toBeNull();
		expect(fileGroup().hidden).toBe(false);

		await fireEvent.click(urlRadio());

		expect(urlInput().type).toBe('url');
		expect(
			screen.getByText('The server downloads the image. PNG or JPEG, at most 8 MiB.')
		).toBeTruthy();
		expect(fileGroup().hidden).toBe(true);
		expect(screen.getByLabelText('Type')).toBeTruthy();
	});

	it('an empty URL shows the error without a request (U6)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await openUrlMode('');
		await confirmUpload();

		expect(screen.getByText('Enter an http or https image URL.')).toBeTruthy();
		expect(screen.queryByText(fallback, { exact: false })).toBeNull();
		expect(calls.some((c) => c.key.startsWith('POST'))).toBe(false);
	});

	it('a URL posts {url, type} to from-url, toasts the host name and closes (U6)', async () => {
		const fresh = game([image(5, 'Snap', { isCover: true })]);
		answers['POST /api/games/g1/images/from-url'] = { status: 200, body: fresh };
		const onchanged = vi.fn();
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });

		await openUrlMode('https://example.com:8443/a.png');
		await fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Snap' } });
		await confirmUpload();

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledWith(fresh));
		const body = calls.find((c) => c.key === 'POST /api/games/g1/images/from-url')!.body as string;
		expect(JSON.parse(body)).toEqual({ url: 'https://example.com:8443/a.png', type: 'Snap' });
		expect(toasts.items.map((t) => t.text)).toContain('Image added from example.com.');
		await vi.waitFor(() => expect(uploadModal().classList.contains('show')).toBe(false));
	});

	it.each([
		[
			'ImageDownloadFailed',
			{ status: 502, code: 'ImageDownloadFailed', title: "Couldn't download the image: HTTP 404." },
			"Couldn't download the image: HTTP 404."
		],
		[
			'ImageUrlBlocked',
			{
				status: 400,
				code: 'ImageUrlBlocked',
				title: 'The URL points to a private or local network address.'
			},
			'The URL points to a private or local network address.'
		],
		[
			'ValidationFailed',
			{
				status: 400,
				code: 'ValidationFailed',
				title: 'One or more validation errors occurred.',
				errors: { url: ["The URL didn't return a PNG or JPEG image."] }
			},
			"The URL didn't return a PNG or JPEG image."
		]
	])(
		'%s shows its message and the file fallback under the URL field (U6)',
		async (_code, body, message) => {
			answers['POST /api/games/g1/images/from-url'] = { status: body.status, body };
			render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

			await openUrlMode('https://example.com/a.png');
			await fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Snap' } });
			await confirmUpload();

			await vi.waitFor(() => expect(urlInput().classList).toContain('is-invalid'));
			const feedback = urlInput().parentElement!.querySelector('.invalid-feedback')!;
			expect(feedback.textContent).toContain(message);
			expect(feedback.textContent).toContain(fallback);
			expect(uploadModal().classList.contains('show')).toBe(true);
			expect(toasts.items).toEqual([]);

			await fireEvent.click(
				screen.getByRole('button', { name: 'Upload a file instead', hidden: true })
			);
			expect(fileRadio().checked).toBe(true);
			expect(fileGroup().hidden).toBe(false);
			expect((screen.getByLabelText('Type') as HTMLSelectElement).value).toBe('Snap');
		}
	);

	it('a DuplicateImage from a URL toasts (U6)', async () => {
		answers['POST /api/games/g1/images/from-url'] = {
			status: 409,
			body: { title: 'DuplicateImage', status: 409, code: 'DuplicateImage' }
		};
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		await openUrlMode('https://example.com/a.png');
		await confirmUpload();

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain('The game already has this image.')
		);
	});

	it('reopening after a cancel resets mode, URL, Type and errors (U6)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await openUrlMode('');
		await fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Snap' } });
		await confirmUpload();
		await fireEvent.input(urlInput(), { target: { value: 'https://x.test/a.png' } });

		await fireEvent.click(
			within(uploadModal() as HTMLElement).getByRole('button', { name: 'Cancel', hidden: true })
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));

		expect(fileRadio().checked).toBe(true);
		expect((screen.getByLabelText('Type') as HTMLSelectElement).value).toBe('BoxArt');
		await fireEvent.click(urlRadio());
		expect(urlInput().value).toBe('');
		expect(screen.queryByText('Enter an http or https image URL.')).toBeNull();
	});

	it('a file over 8 MiB is refused before sending', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		const big = new File(['x'], 'big.png', { type: 'image/png' });
		Object.defineProperty(big, 'size', { value: 8 * 1024 * 1024 + 1 });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		await choose(big);
		await confirmUpload();

		expect(screen.getByText('The image must be at most 8 MiB.')).toBeTruthy();
		expect(calls.some((c) => c.key.startsWith('POST'))).toBe(false);
	});

	it('a DuplicateImage failure toasts and leaves the modal open', async () => {
		answers['POST /api/games/g1/images'] = {
			status: 409,
			body: { title: 'Duplicate', status: 409, code: 'DuplicateImage' }
		};
		const onchanged = vi.fn();
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload image' }));
		await choose(png());
		await confirmUpload();

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain('The game already has this image.')
		);
		expect(uploadModal().classList.contains('show')).toBe(true);
		expect(onchanged).not.toHaveBeenCalled();
	});

	it('Use as cover and Delete call the API and refresh from the returned detail', async () => {
		const afterCover = game([image(11, 'BoxArt', { isCover: true }), image(12, 'Snap')]);
		answers['POST /api/games/g1/images/1/cover'] = { status: 200, body: afterCover };
		answers['DELETE /api/games/g1/images/2'] = {
			status: 200,
			body: game([image(1, 'BoxArt', { isCover: true })])
		};
		const onchanged = vi.fn();
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });

		await fireEvent.click(screen.getByRole('button', { name: 'Use as cover: boxart-01.png' }));
		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledWith(afterCover));

		await fireEvent.click(screen.getByRole('button', { name: 'Delete snap-01.png' }));
		expect(screen.getByText('snap-01.png', { selector: 'code' })).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Delete image', hidden: true }));
		await vi.waitFor(() =>
			expect(calls.map((c) => c.key)).toContain('DELETE /api/games/g1/images/2')
		);
		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledTimes(2));
	});
});
