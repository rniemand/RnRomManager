import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail } from '#lib/api/types.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

vi.mock('$app/navigation', () => ({ goto: (url: string) => navigateTo(url) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: 'g1' },
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: GamePage } = await import('./[id]/+page.svelte');

const rom = (id: number, hasNote: boolean) => ({
	id,
	fileName: `Tetris ${id}.gb`,
	originalFileName: `Tetris ${id}.gb`,
	baseTitle: 'Tetris',
	regions: [],
	languages: [],
	revision: null,
	flags: [],
	isPrimary: id === 1,
	identificationMethod: 'Dat' as const,
	datName: null,
	datVersion: null,
	datGameName: null,
	header: null,
	sizeBytes: 1,
	crc32: '0',
	md5: '0',
	sha1: '0',
	dateAdded: '',
	dateModified: '',
	downloadUrl: '',
	hasNote,
	externalFileName: null as string | null,
	version: null as string | null,
	files: null,
	totalSizeBytes: 1
});

const game = (): GameDetail => ({
	id: 'g1',
	system: 'gb',
	systemName: 'Game Boy',
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
	roms: [rom(1, true), rom(2, false)],
	images: []
});

type Answer = { status: number; body?: unknown; text?: string };

describe('release notes (B11)', () => {
	let calls: { key: string; body?: string }[];
	let answers: Record<string, Answer[]>;
	const note = '/api/games/g1/roms/1/note';

	beforeEach(() => {
		resetClient();
		navigateTo('/games/g1');
		toasts.items.length = 0;
		calls = [];
		answers = { 'GET /api/games/g1': [{ status: 200, body: game() }] };
		answers[`GET ${note}`] = [{ status: 200, text: '# Saved tips\r\n\r\nUse the **B** button.' }];
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			// B16: the game's collections (the badges); answered here so the queued answers stay as they were.
			if (url.endsWith('/collections')) return Promise.resolve(new Response('[]', { status: 200 }));
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body as string | undefined });
			const answer = answers[key]?.shift() ?? { status: 204 };
			const payload =
				answer.text ?? (answer.body === undefined ? null : JSON.stringify(answer.body));
			return Promise.resolve(new Response(payload, { status: answer.status }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	const noteModal = () => {
		const title = [...document.querySelectorAll('.modal-title')].find((t) =>
			t.textContent?.startsWith('Note —')
		)!;
		return within(title.closest<HTMLElement>('.modal')!);
	};
	const radio = (name: string) =>
		noteModal().getByRole('radio', { name, hidden: true }) as HTMLInputElement;
	const button = (name: string) =>
		noteModal().getByRole('button', { name, hidden: true }) as HTMLButtonElement;
	const textarea = () =>
		noteModal().getByLabelText('Note (Markdown)', { selector: 'textarea' }) as HTMLTextAreaElement;

	async function open(name: string): Promise<void> {
		await fireEvent.click(await screen.findByRole('button', { name }));
	}

	it('shows a filled icon for a release with a note and an outline one without', async () => {
		render(GamePage);
		const withNote = await screen.findByRole('button', { name: 'Note for Tetris 1.gb' });
		const without = screen.getByRole('button', { name: 'Add note for Tetris 2.gb' });
		expect(withNote.querySelector('i')!.className).toContain('bi-sticky-fill');
		expect(without.querySelector('i')!.className).toMatch(/bi-sticky(?!-fill)/);
		expect(withNote.classList.contains('btn-sm')).toBe(true);
	});

	it('opens in View with the rendered note when one exists', async () => {
		render(GamePage);
		await open('Note for Tetris 1.gb');

		const heading = await vi.waitFor(() =>
			noteModal().getByRole('heading', { name: 'Saved tips', hidden: true })
		);
		expect(heading.tagName).toBe('H1');
		expect(radio('View').checked).toBe(true);
		expect(document.querySelector('.modal-dialog.modal-lg')).toBeTruthy();
		expect(button('Delete note')).toBeTruthy();
		expect(button('Save').disabled).toBe(true);
	});

	it('opens in Edit without fetching when there is no note; View is disabled', async () => {
		render(GamePage);
		await open('Add note for Tetris 2.gb');

		expect(radio('Edit').checked).toBe(true);
		expect(radio('View').disabled).toBe(true);
		expect(textarea()).toBeTruthy();
		expect(calls.some((c) => c.key.includes('/roms/2/note'))).toBe(false);
		expect(noteModal().queryByRole('button', { name: 'Delete note', hidden: true })).toBeNull();
	});

	it('switches between View, Edit and Preview; Preview renders the edited text', async () => {
		render(GamePage);
		await open('Note for Tetris 1.gb');
		await vi.waitFor(() => expect(radio('View').checked).toBe(true));

		await fireEvent.click(radio('Edit'));
		expect(textarea().value).toBe('# Saved tips\n\nUse the **B** button.');
		await fireEvent.input(textarea(), { target: { value: '## New heading' } });

		await fireEvent.click(radio('Preview'));
		expect(noteModal().getByRole('heading', { name: 'New heading', hidden: true }).tagName).toBe(
			'H2'
		);

		await fireEvent.click(radio('View'));
		expect(noteModal().getByRole('heading', { name: 'Saved tips', hidden: true })).toBeTruthy();
		expect(button('Save').disabled).toBe(false);
	});

	it('after a save the game is refetched, so the next note uses the new ROM ids', async () => {
		const rewritten = game();
		rewritten.roms = [
			{ ...rom(11, true), fileName: 'Tetris 1.gb' },
			{ ...rom(12, true), fileName: 'Tetris 2.gb' }
		];
		answers['GET /api/games/g1'].push({ status: 200, body: rewritten });
		answers['GET /api/games/g1/roms/12/note'] = [{ status: 200, text: '# Fresh' }];
		render(GamePage);
		await open('Add note for Tetris 2.gb');

		await fireEvent.input(textarea(), { target: { value: '# Fresh' } });
		await fireEvent.click(button('Save'));
		await vi.waitFor(() =>
			expect(calls.filter((c) => c.key === 'GET /api/games/g1')).toHaveLength(2)
		);
		await new Promise((resolve) => setTimeout(resolve, 20)); // let the refetched game render

		await open('Note for Tetris 2.gb');
		await vi.waitFor(() =>
			expect(noteModal().getByRole('heading', { name: 'Fresh', hidden: true })).toBeTruthy()
		);
		expect(calls.map((c) => c.key)).toContain('GET /api/games/g1/roms/12/note');
		expect(calls.map((c) => c.key)).not.toContain('GET /api/games/g1/roms/2/note');
	});

	it('Save sends PUT with normalised text and flips hasNote', async () => {
		render(GamePage);
		await open('Add note for Tetris 2.gb');

		expect(button('Save').disabled).toBe(true);
		await fireEvent.input(textarea(), { target: { value: 'Line 1\r\nLine 2' } });
		await fireEvent.click(button('Save'));

		await vi.waitFor(() =>
			expect(calls.find((c) => c.key === 'PUT /api/games/g1/roms/2/note')?.body).toBe(
				JSON.stringify({ text: 'Line 1\nLine 2' })
			)
		);
		expect(await screen.findByRole('button', { name: 'Note for Tetris 2.gb' })).toBeTruthy();
		expect(toasts.items.map((t) => t.text)).toContain('Note saved.');
	});

	it('Delete shows an inline Yes/No; No cancels, Yes sends DELETE', async () => {
		render(GamePage);
		await open('Note for Tetris 1.gb');
		await vi.waitFor(() => button('Delete note'));

		await fireEvent.click(button('Delete note'));
		expect(noteModal().getByText('Delete this note?')).toBeTruthy();
		await fireEvent.click(button('No'));
		expect(noteModal().queryByText('Delete this note?')).toBeNull();
		expect(calls.some((c) => c.key === `DELETE ${note}`)).toBe(false);

		await fireEvent.click(button('Delete note'));
		await fireEvent.click(button('Yes'));
		await vi.waitFor(() => expect(calls.map((c) => c.key)).toContain(`DELETE ${note}`));
		expect(await screen.findByRole('button', { name: 'Add note for Tetris 1.gb' })).toBeTruthy();
		expect(toasts.items.map((t) => t.text)).toContain('Note deleted.');
	});

	it('the byte counter counts UTF-8 after CRLF normalisation and blocks Save over the cap', async () => {
		render(GamePage);
		await open('Add note for Tetris 2.gb');

		await fireEvent.input(textarea(), { target: { value: 'é\r\n' } });
		const counter = noteModal().getByText(/bytes$/);
		expect(counter.textContent!.trim()).toBe('3 / 65,536 bytes');
		expect(counter.classList.contains('text-danger')).toBe(false);

		await fireEvent.input(textarea(), { target: { value: 'a'.repeat(65536) } });
		expect(counter.textContent!.trim()).toBe('65,536 / 65,536 bytes');
		expect(button('Save').disabled).toBe(false);

		await fireEvent.input(textarea(), { target: { value: 'é'.repeat(32768) + 'a' } });
		expect(counter.textContent!.trim()).toBe('65,537 / 65,536 bytes');
		expect(counter.classList.contains('text-danger')).toBe(true);
		expect(button('Save').disabled).toBe(true);
	});

	it('409 JobRunning shows the job-running toast and keeps the text', async () => {
		answers['PUT /api/games/g1/roms/2/note'] = [{ status: 409, body: { code: 'JobRunning' } }];
		render(GamePage);
		await open('Add note for Tetris 2.gb');

		await fireEvent.input(textarea(), { target: { value: 'tip' } });
		await fireEvent.click(button('Save'));

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain(
				'Another job is running. Try again when it has finished.'
			)
		);
		expect(textarea().value).toBe('tip');
		expect(screen.getByRole('button', { name: 'Add note for Tetris 2.gb' })).toBeTruthy();
	});

	it('other errors show an alert inside the modal and keep the text', async () => {
		answers['PUT /api/games/g1/roms/2/note'] = [
			{ status: 400, body: { code: 'ValidationFailed', title: 'The note is too long.' } }
		];
		render(GamePage);
		await open('Add note for Tetris 2.gb');

		await fireEvent.input(textarea(), { target: { value: 'tip' } });
		await fireEvent.click(button('Save'));

		expect(await vi.waitFor(() => noteModal().getByText('The note is too long.'))).toBeTruthy();
		expect(textarea().value).toBe('tip');
	});

	it('rendered note links open in a new tab and scripts are stripped', async () => {
		answers[`GET ${note}`] = [
			{ status: 200, text: '[site](https://example.com)\n\n<img src=x onerror="alert(1)">' }
		];
		render(GamePage);
		await open('Note for Tetris 1.gb');

		const link = await vi.waitFor(() =>
			noteModal().getByRole('link', { name: 'site', hidden: true })
		);
		expect(link.getAttribute('target')).toBe('_blank');
		expect(link.getAttribute('rel')).toBe('noopener noreferrer');
		expect(
			document.querySelector('[data-testid="note-rendered"] img')!.hasAttribute('onerror')
		).toBe(false);
	});
});
