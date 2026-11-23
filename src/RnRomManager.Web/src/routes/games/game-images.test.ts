import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail, ImageInfo, ImageSuggestion } from '#lib/api/types.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

// The page reads `?tab=` from the URL (GP1).
const nav = vi.hoisted(() => ({
	url: new URL('http://localhost/games/g1?tab=images'),
	params: { id: 'g1' },
	goto: vi.fn<(...args: unknown[]) => Promise<void>>(() => Promise.resolve())
}));
vi.mock('$app/state', () => ({
	page: {
		get params() {
			return nav.params;
		},
		get url() {
			return nav.url;
		}
	}
}));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => nav.goto(...args) }));
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
	fileSets: false,
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
	saveCount: 0,
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
		auth.set({ id: 1, username: 'm', roles: ['admin'], mustChangePassword: false });
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
		nav.url = new URL('http://localhost/games/g1?tab=images');
		nav.params = { id: 'g1' };
		nav.goto.mockClear();
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
		// B10: the caption keeps the dimensions; the size is a badge over the thumbnail.
		const caption = cards[1]!.querySelector('.card-body .text-body-secondary')!;
		expect(caption.textContent!.trim()).toBe('160×144');
		const size = cards[1]!.querySelector('.position-relative > .badge')!;
		expect(size.textContent!.trim()).toBe('2.0 KiB');
		expect(size.className).toBe('badge text-bg-light border position-absolute top-0 end-0 m-1');
		expect(size.closest('button')).toBeNull();
		expect(screen.queryByLabelText('Upload an image')).toBeNull();
		expect(screen.queryByRole('button', { name: 'Upload Image' })).toBeNull();
		expect(screen.queryByRole('button', { name: /Use as cover/ })).toBeNull();
		expect(screen.queryByRole('button', { name: /^Delete / })).toBeNull();
	});

	it('every user gets a Download link per image, first in its actions row (B10)', () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		const detail = game(placeholderAndSnap());
		render(GameImages, { game: detail, onchanged: () => {} });

		const links = screen.getAllByRole('link', { name: /^Download / });
		expect(links.map((a) => a.getAttribute('aria-label'))).toEqual(
			detail.images.map((i) => `Download ${i.fileName}`)
		);
		expect(links[0].getAttribute('href')).toBe(
			`/api/games/g1/images/${detail.images[0].id}?download=true`
		);
		expect(links[0].hasAttribute('download')).toBe(true);
	});

	it('an Admin’s actions follow Download; tiles carry the hover class (B10)', () => {
		const detail = game(placeholderAndSnap());
		render(GameImages, { game: detail, onchanged: () => {} });

		const download = screen.getByRole('link', { name: `Download ${detail.images[1].fileName}` });
		const row = download.parentElement!;
		expect(row.firstElementChild).toBe(download);
		expect(
			within(row).getByRole('button', { name: `Delete ${detail.images[1].fileName}` })
		).toBeTruthy();
		expect(download.closest('.card')!.classList).toContain('image-tile');
	});

	it('Suggest Images, Google Images and Upload Image sit right-aligned on their own row before the grid; the heading is visually hidden (G3, I1, B8, S11)', () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		const upload = screen.getByRole('button', { name: 'Upload Image' });
		const google = screen.getByRole('link', { name: 'Google Images' });
		const suggest = screen.getByRole('button', { name: 'Suggest Images' });
		const heading = screen.getByRole('heading', { name: 'Images' });
		expect(heading.className).toBe('visually-hidden');
		expect([...upload.parentElement!.children]).toEqual([suggest, google, upload]);
		expect(suggest.classList.contains('ms-auto')).toBe(true);
		expect(suggest.querySelector('i')!.classList.contains('bi-magic')).toBe(true);
		expect(google.classList.contains('ms-auto')).toBe(false);
		expect(upload.classList.contains('ms-auto')).toBe(false);
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
	const choose = async (file: File) => {
		await fireEvent.click(screen.getByLabelText('File'));
		await fireEvent.change(screen.getByLabelText('Upload an image'), { target: { files: [file] } });
	};
	const typeRadio = (label: 'Box art' | 'Screenshot') =>
		screen.getByLabelText(label) as HTMLInputElement;
	const png = () => new File([new Uint8Array([1, 2, 3])], 'art.png', { type: 'image/png' });

	it('the upload form lives in a modal opened by the Upload Image button', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		expect(uploadModal().classList.contains('show')).toBe(false);
		expect(uploadModal().querySelector('.modal-title')!.textContent).toBe('Upload Image');
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		expect(uploadModal().classList.contains('show')).toBe(true);
	});

	it('Google Images opens Google Images for the header query in a new tab (B8)', () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		const google = screen.getByRole('link', { name: 'Google Images' });
		expect(google.getAttribute('href')).toBe(
			`https://www.google.com/search?udm=2&q=${encodeURIComponent('"Tetris" Game Boy')}`
		);
		expect(google.getAttribute('target')).toBe('_blank');
		expect(google.getAttribute('rel')).toBe('noopener noreferrer');
		expect(google.className).toBe('btn btn-outline-secondary btn-sm');
		expect(google.querySelector('i')!.className).toContain('bi-google');
	});

	it('a Site user gets no Google Images button (B8)', () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		expect(screen.queryByRole('link', { name: 'Google Images' })).toBeNull();
		expect(screen.queryByRole('button', { name: 'Suggest Images' })).toBeNull();
	});

	it('the Image URL input gets focus each time the dialog has opened (B6)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		uploadModal().dispatchEvent(new Event('shown.bs.modal'));
		expect(document.activeElement).toBe(screen.getByLabelText('Image URL'));
	});

	it('Enter in the Image URL input submits once, and not while busy (B7)', async () => {
		let posts = 0;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (url.endsWith('/from-url') && init?.method === 'POST') {
				posts++;
				return new Promise<Response>(() => {}); // stays busy
			}
			return Promise.resolve(new Response(null, { status: 204 }));
		});
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		const input = screen.getByLabelText('Image URL');
		await fireEvent.input(input, { target: { value: 'https://example.com/a.png' } });

		const first = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, bubbles: true });
		input.dispatchEvent(first);
		await vi.waitFor(() => expect(posts).toBe(1));
		await fireEvent.keyDown(input, { key: 'Enter' });
		await new Promise((r) => setTimeout(r, 10));

		expect(first.defaultPrevented).toBe(true);
		expect(posts).toBe(1);
	});

	it('Enter on the source radios does not submit (B7)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));

		await fireEvent.keyDown(screen.getByLabelText('URL'), { key: 'Enter' });
		await new Promise((r) => setTimeout(r, 10));

		expect(calls.filter((c) => c.key.startsWith('POST'))).toEqual([]);
		expect(screen.queryByText('Enter an http or https image URL.')).toBeNull();
	});

	it('upload posts FormData, closes the modal, resets the fields and hands back the fresh detail', async () => {
		const fresh = game([image(5, 'BoxArt', { isCover: true })]);
		answers['POST /api/games/g1/images'] = { status: 200, body: fresh };
		const onchanged = vi.fn();
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		await choose(png());
		await fireEvent.click(typeRadio('Screenshot'));
		await confirmUpload();

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledWith(fresh));
		const body = calls.find((c) => c.key === 'POST /api/games/g1/images')!.body as FormData;
		expect(body).toBeInstanceOf(FormData);
		expect((body.get('file') as File).name).toBe('art.png');
		expect(body.get('type')).toBe('Snap');
		await vi.waitFor(() => expect(uploadModal().classList.contains('show')).toBe(false));
		expect(typeRadio('Box art').checked).toBe(true);
		expect((screen.getByLabelText('Upload an image') as HTMLInputElement).value).toBe('');
	});

	it('an empty submit shows the file error, keeps the modal open, and reopening clears it', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		await fireEvent.click(screen.getByLabelText('File'));
		await confirmUpload();

		expect(screen.getByText('Choose an image file.')).toBeTruthy();
		expect(uploadModal().classList.contains('show')).toBe(true);
		expect(calls.some((c) => c.key.startsWith('POST'))).toBe(false);

		await fireEvent.click(
			within(uploadModal() as HTMLElement).getByRole('button', { name: 'Cancel', hidden: true })
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		expect(screen.queryByText('Choose an image file.')).toBeNull();
	});

	const urlRadio = () => screen.getByLabelText('URL') as HTMLInputElement;
	const fileRadio = () => screen.getByLabelText('File') as HTMLInputElement;
	const urlInput = () => screen.getByLabelText('Image URL') as HTMLInputElement;
	const fileGroup = () => screen.getByLabelText('Upload an image').closest('div')!;
	const fallback = 'Download the image and upload the file instead.';

	async function openUrlMode(url: string): Promise<void> {
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
		await fireEvent.click(urlRadio());
		await fireEvent.input(urlInput(), { target: { value: url } });
	}

	it('opens on URL, listed before File; File mode shows the file input and hides the URL field (U6, I2)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));

		const source = screen.getByRole('group', { name: 'Image source', hidden: true });
		expect([...source.querySelectorAll('label')].map((l) => l.textContent)).toEqual([
			'URL',
			'File'
		]);
		expect(urlRadio().checked).toBe(true);
		expect(urlRadio().className).toBe('btn-check');
		expect(screen.getByText('URL').className).toBe('btn btn-outline-secondary btn-sm');
		expect(urlInput().type).toBe('url');
		expect(
			screen.getByText(
				'The server downloads the image. PNG, JPEG or WebP (saved as PNG/JPEG), at most 8 MiB.'
			)
		).toBeTruthy();
		expect(fileGroup().hidden).toBe(true);

		await fireEvent.click(fileRadio());

		expect(screen.queryByLabelText('Image URL')).toBeNull();
		expect(fileGroup().hidden).toBe(false);
	});

	it('Type is a Box art / Screenshot radio group after the inputs, Box art by default (I2)', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));

		const group = screen.getByRole('group', { name: 'Image type', hidden: true });
		expect(group.className).toBe('btn-group');
		expect([...group.querySelectorAll('label')].map((l) => l.textContent)).toEqual([
			'Box art',
			'Screenshot'
		]);
		expect(typeRadio('Box art').checked).toBe(true);
		expect(typeRadio('Box art').className).toBe('btn-check');
		expect(
			screen.getByLabelText('Upload an image').compareDocumentPosition(group) &
				Node.DOCUMENT_POSITION_FOLLOWING
		).toBeTruthy();
	});

	it('a type error shows below the group (I2)', async () => {
		answers['POST /api/games/g1/images/from-url'] = {
			status: 400,
			body: {
				status: 400,
				code: 'ValidationFailed',
				title: 'One or more validation errors occurred.',
				errors: { type: ['Choose BoxArt or Snap.'] }
			}
		};
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await openUrlMode('https://example.com/a.png');
		await confirmUpload();

		const error = await screen.findByText('Choose BoxArt or Snap.');
		expect(error.className).toBe('invalid-feedback d-block');
		expect(error.previousElementSibling).toBe(
			screen.getByRole('group', { name: 'Image type', hidden: true })
		);
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
		await fireEvent.click(typeRadio('Screenshot'));
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
			await fireEvent.click(typeRadio('Screenshot'));
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
			expect(typeRadio('Screenshot').checked).toBe(true);
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
		await fireEvent.click(typeRadio('Screenshot'));
		await confirmUpload();
		await fireEvent.input(urlInput(), { target: { value: 'https://x.test/a.png' } });

		await fireEvent.click(
			within(uploadModal() as HTMLElement).getByRole('button', { name: 'Cancel', hidden: true })
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));

		await fireEvent.click(fileRadio());
		await fireEvent.click(
			within(uploadModal() as HTMLElement).getByRole('button', { name: 'Cancel', hidden: true })
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));

		expect(urlRadio().checked).toBe(true);
		expect(typeRadio('Box art').checked).toBe(true);
		expect(urlInput().value).toBe('');
		expect(screen.queryByText('Enter an http or https image URL.')).toBeNull();
	});

	describe('drag and drop (I3)', () => {
		/** jsdom has no DataTransfer and only takes a real FileList on `files`; both are stood in for. */
		class FakeDataTransfer {
			private list: File[] = [];
			items = { add: (f: File) => void this.list.push(f) };
			get files() {
				return this.list;
			}
		}
		function drag(type: string, files: File[] | null, types = files ? ['Files'] : ['text/plain']) {
			const event = new Event(type, { bubbles: true, cancelable: true }) as DragEvent;
			Object.defineProperty(event, 'dataTransfer', { value: { types, files: files ?? [] } });
			return event;
		}
		const dropZone = () => screen.getByRole('group', { name: 'Image to upload', hidden: true });
		let picked: unknown;

		async function openWithFileInput(): Promise<HTMLInputElement> {
			vi.stubGlobal('DataTransfer', FakeDataTransfer);
			render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
			await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
			const input = screen.getByLabelText('Upload an image') as HTMLInputElement;
			picked = undefined;
			Object.defineProperty(input, 'files', {
				configurable: true,
				get: () => picked,
				set: (v) => (picked = v)
			});
			return input;
		}

		it('dragging files over the body shows the overlay; leaving hides it', async () => {
			await openWithFileInput();

			const over = drag('dragover', [png()]);
			await fireEvent(dropZone(), over);

			expect(over.defaultPrevented).toBe(true);
			const overlay = screen.getByText('Drop image to upload');
			expect(overlay.className).toContain('border-primary');
			await fireEvent(dropZone(), drag('dragleave', [png()]));
			expect(screen.queryByText('Drop image to upload')).toBeNull();
		});

		it('dropping one file selects File and uses it as if picked', async () => {
			await openWithFileInput();
			expect(urlRadio().checked).toBe(true);

			const drop = drag('drop', [png()]);
			await fireEvent(dropZone(), drop);

			expect(drop.defaultPrevented).toBe(true);
			expect(fileRadio().checked).toBe(true);
			expect((picked as File[]).map((f) => f.name)).toEqual(['art.png']);
			expect(screen.queryByText(/Only one image/)).toBeNull();
		});

		it('dropping several uses the first and says so until the next drop, pick or open', async () => {
			const input = await openWithFileInput();
			const second = new File([new Uint8Array([4])], 'other.png', { type: 'image/png' });

			await fireEvent(dropZone(), drag('drop', [png(), second]));

			expect((picked as File[]).map((f) => f.name)).toEqual(['art.png']);
			const note = screen.getByText('Only one image can be uploaded at a time; using art.png.');
			expect(note.className).toBe('form-text');

			await fireEvent(dropZone(), drag('drop', [second]));
			expect(screen.queryByText(/Only one image/)).toBeNull();

			await fireEvent(dropZone(), drag('drop', [png(), second]));
			await fireEvent.change(input);
			expect(screen.queryByText(/Only one image/)).toBeNull();

			await fireEvent(dropZone(), drag('drop', [png(), second]));
			await fireEvent.click(
				within(uploadModal() as HTMLElement).getByRole('button', { name: 'Cancel', hidden: true })
			);
			await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
			expect(screen.queryByText(/Only one image/)).toBeNull();
		});

		it('dragging or dropping text is left alone (no preventDefault, no change)', async () => {
			await openWithFileInput();

			const over = drag('dragover', null);
			const drop = drag('drop', null);
			await fireEvent(dropZone(), over);
			await fireEvent(dropZone(), drop);

			expect(over.defaultPrevented).toBe(false);
			expect(drop.defaultPrevented).toBe(false);
			expect(screen.queryByText('Drop image to upload')).toBeNull();
			expect(urlRadio().checked).toBe(true);
			expect(picked).toBeUndefined();
		});
	});

	it('a file over 8 MiB is refused before sending', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		const big = new File(['x'], 'big.png', { type: 'image/png' });
		Object.defineProperty(big, 'size', { value: 8 * 1024 * 1024 + 1 });

		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
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

		await fireEvent.click(screen.getByRole('button', { name: 'Upload Image' }));
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

describe('Suggest Images (IGDB suggestions S11–S13)', () => {
	type Pending = { url: string; init?: RequestInit; resolve: (r: Response) => void };
	let pending: Pending[];

	const suggestion = (n: number, extra: Partial<ImageSuggestion> = {}): ImageSuggestion => ({
		previewUrl: `https://images.igdb.com/igdb/image/upload/t_cover_big/co${n}.jpg`,
		imageUrl: `https://images.igdb.com/igdb/image/upload/t_1080p/co${n}.jpg`,
		gameName: `Tetris ${n}`,
		width: 600,
		height: 800,
		...extra
	});
	const json = (body: unknown, status = 200) =>
		new Response(JSON.stringify(body), {
			status,
			headers: { 'content-type': 'application/json' }
		});
	const problem = (status: number, code: string, title: string) =>
		json({ status, code, title }, status);

	/** The first unanswered request whose URL contains `part`. */
	const take = async (part: string): Promise<Pending> => {
		let found: Pending | undefined;
		await vi.waitFor(() => {
			found = pending.find((p) => p.url.includes(part));
			expect(found).toBeTruthy();
		});
		pending.splice(pending.indexOf(found!), 1);
		return found!;
	};
	const dialog = () =>
		screen
			.getByRole('heading', { name: 'Suggest Images', hidden: true })
			.closest('.modal') as HTMLElement;
	const inDialog = () => within(dialog());
	const results = () => dialog().querySelector('[aria-live="polite"]') as HTMLElement;
	const tile = (name: string) => inDialog().getByRole('button', { name, hidden: true });
	const open = () => fireEvent.click(screen.getByRole('button', { name: 'Suggest Images' }));
	const close = () =>
		fireEvent.click(
			inDialog()
				.getAllByRole('button', { name: 'Close', hidden: true })
				.find((b) => b.textContent === 'Close')!
		);
	const settle = () => new Promise((r) => setTimeout(r, 10));

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		auth.set({ id: 1, username: 'm', roles: ['admin'], mustChangePassword: false });
		pending = [];
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (url === '/api/auth/antiforgery')
				return Promise.resolve(new Response(null, { status: 204 }));
			return new Promise<Response>((resolve) => pending.push({ url, init, resolve }));
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('opening loads Box art and shows each tile with its name and size, in a live region', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();

		const load = await take('/image-suggestions');
		expect(load.url).toBe('/api/games/g1/image-suggestions?type=BoxArt');
		expect(load.init?.signal).toBeInstanceOf(AbortSignal);
		expect(results().getAttribute('aria-live')).toBe('polite');
		expect(within(results()).getByRole('status', { hidden: true })).toBeTruthy();
		expect((inDialog().getByLabelText('Box art') as HTMLInputElement).checked).toBe(true);
		load.resolve(
			json({ suggestions: [suggestion(1), suggestion(2, { width: null, height: null })] })
		);

		const first = await vi.waitFor(() => tile('Add Tetris 1 box art 1'));
		expect(first.parentElement!.classList.contains('col')).toBe(true);
		expect(first.querySelector('img')!.getAttribute('src')).toBe(suggestion(1).previewUrl);
		expect(first.querySelector('img')!.getAttribute('alt')).toBe('');
		expect(within(first).getByText('Tetris 1').getAttribute('title')).toBe('Tetris 1');
		expect(within(first).getByText('600×800')).toBeTruthy();
		expect(tile('Add Tetris 2 box art 2').textContent).not.toContain('×');
		expect(
			inDialog().getByText('Suggestions from IGDB.com. Click one to add it to the game.')
		).toBeTruthy();
	});

	it('switching to Screenshot loads Snap and ignores the late Box art answer', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		const boxArt = await take('type=BoxArt');

		await fireEvent.click(inDialog().getByLabelText('Screenshot'));
		const snap = await take('type=Snap');
		expect(boxArt.init?.signal?.aborted).toBe(true);
		snap.resolve(json({ suggestions: [suggestion(5)] }));
		boxArt.resolve(json({ suggestions: [suggestion(9)] }));

		await vi.waitFor(() => tile('Add Tetris 5 screenshot 1'));
		await settle();
		expect(inDialog().queryByRole('button', { name: /Tetris 9/, hidden: true })).toBeNull();
	});

	it('closing aborts the load, and a late answer is ignored after reopening', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		const first = await take('type=BoxArt');

		await close();
		expect(first.init?.signal?.aborted).toBe(true);
		await open();
		const second = await take('type=BoxArt');
		first.resolve(json({ suggestions: [suggestion(9)] }));
		await settle();
		expect(inDialog().queryByRole('button', { name: /Tetris 9/, hidden: true })).toBeNull();

		second.resolve(json({ suggestions: [suggestion(1)] }));
		await vi.waitFor(() => tile('Add Tetris 1 box art 1'));
	});

	it('empty answers say so per type', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		(await take('type=BoxArt')).resolve(json({ suggestions: [] }));
		await vi.waitFor(() => inDialog().getByText('IGDB has no box art for this game.'));

		await fireEvent.click(inDialog().getByLabelText('Screenshot'));
		(await take('type=Snap')).resolve(json({ suggestions: [] }));
		await vi.waitFor(() => inDialog().getByText('IGDB has no screenshots for this game.'));
	});

	it('IgdbNotConfigured shows the setup alert', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		(await take('type=BoxArt')).resolve(
			problem(
				409,
				'IgdbNotConfigured',
				'Image suggestions need Sources:IgdbClientId and Sources:IgdbClientSecret.'
			)
		);

		const alert = await vi.waitFor(() => {
			const found = results().querySelector('.alert-info');
			expect(found).toBeTruthy();
			return found!;
		});
		expect(alert.textContent!.replace(/\s+/g, ' ').trim()).toBe(
			'Image suggestions need an IGDB Client ID and secret. Set Sources:IgdbClientId and Sources:IgdbClientSecret and restart.'
		);
	});

	it('other errors show an ErrorAlert whose Retry reloads the current type', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		(await take('type=BoxArt')).resolve(
			problem(502, 'IgdbFailed', 'IGDB is busy; try again in a moment.')
		);

		await vi.waitFor(() => inDialog().getByText('IGDB is busy; try again in a moment.'));
		await fireEvent.click(inDialog().getByRole('button', { name: /Retry/, hidden: true }));
		(await take('type=BoxArt')).resolve(json({ suggestions: [suggestion(1)] }));
		await vi.waitFor(() => tile('Add Tetris 1 box art 1'));
	});

	it('a tile adds itself with the dialog type, refreshes, toasts and closes the dialog (S1)', async () => {
		const onchanged = vi.fn();
		const after = game([image(7, 'BoxArt', { isCover: true })]);
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });
		await open();
		(await take('type=BoxArt')).resolve(json({ suggestions: [suggestion(1), suggestion(2)] }));
		await fireEvent.click(await vi.waitFor(() => tile('Add Tetris 1 box art 1')));

		const add = await take('/from-url');
		expect(add.url).toBe('/api/games/g1/images/from-url');
		expect(add.init?.method).toBe('POST');
		expect(JSON.parse(add.init!.body as string)).toEqual({
			url: suggestion(1).imageUrl,
			type: 'BoxArt'
		});
		// S13: tiles and radios are disabled while adding; Close is not.
		expect((tile('Add Tetris 2 box art 2') as HTMLButtonElement).disabled).toBe(true);
		expect((inDialog().getByLabelText('Screenshot') as HTMLInputElement).disabled).toBe(true);
		expect(tile('Add Tetris 1 box art 1').querySelector('.spinner-border')).toBeTruthy();
		for (const button of inDialog().getAllByRole('button', { name: 'Close', hidden: true }))
			expect((button as HTMLButtonElement).disabled).toBe(false);
		add.resolve(json(after));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledWith(after));
		expect(toasts.items.map((t) => t.text)).toContain('Image added from IGDB.');
		await vi.waitFor(() => expect(dialog().classList.contains('show')).toBe(false));
	});

	it('an add that finishes after close and reopen still refreshes and toasts, but never closes the reopened dialog', async () => {
		const onchanged = vi.fn();
		render(GameImages, { game: game(placeholderAndSnap()), onchanged });
		await open();
		(await take('type=BoxArt')).resolve(json({ suggestions: [suggestion(1)] }));
		await fireEvent.click(await vi.waitFor(() => tile('Add Tetris 1 box art 1')));
		const add = await take('/from-url');

		await close();
		await open();
		(await take('type=BoxArt')).resolve(json({ suggestions: [suggestion(1)] }));
		await vi.waitFor(() => tile('Add Tetris 1 box art 1'));
		add.resolve(json(game([])));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledTimes(1));
		expect(toasts.items.map((t) => t.text)).toContain('Image added from IGDB.');
		expect(dialog().classList.contains('show')).toBe(true);
	});

	it('a duplicate is toasted with the existing message and keeps the dialog open', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		(await take('type=BoxArt')).resolve(json({ suggestions: [suggestion(1)] }));
		await fireEvent.click(await vi.waitFor(() => tile('Add Tetris 1 box art 1')));
		(await take('/from-url')).resolve(problem(409, 'DuplicateImage', 'Duplicate'));

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain('The game already has this image.')
		);
		expect((tile('Add Tetris 1 box art 1') as HTMLButtonElement).disabled).toBe(false);
		expect(dialog().classList.contains('show')).toBe(true);
	});

	it('a failed download is toasted with its title', async () => {
		render(GameImages, { game: game(placeholderAndSnap()), onchanged: () => {} });
		await open();
		(await take('type=BoxArt')).resolve(json({ suggestions: [suggestion(1)] }));
		await fireEvent.click(await vi.waitFor(() => tile('Add Tetris 1 box art 1')));
		(await take('/from-url')).resolve(problem(502, 'ImageDownloadFailed', 'HTTP 404.'));

		await vi.waitFor(() => expect(toasts.items.map((t) => t.text)).toContain('HTTP 404.'));
	});
});
