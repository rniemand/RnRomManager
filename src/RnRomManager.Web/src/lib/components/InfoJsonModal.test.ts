import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: InfoJsonModal } = await import('./InfoJsonModal.svelte');

describe('InfoJsonModal', () => {
	let answers: Response[];
	let urls: string[];
	let copied: string[];

	beforeEach(() => {
		resetClient();
		answers = [];
		urls = [];
		copied = [];
		vi.stubGlobal('fetch', (url: string) => {
			urls.push(url);
			return Promise.resolve(answers.shift() ?? new Response(null, { status: 500 }));
		});
		vi.stubGlobal('navigator', {
			clipboard: { writeText: (text: string) => Promise.resolve(void copied.push(text)) }
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	it('fetches only when opened, shows the tree, and copies the raw text', async () => {
		const raw = '{ "title": "Tetris" }';
		answers.push(new Response(raw, { status: 200 }));
		const { rerender } = render(InfoJsonModal, { open: false, gameId: 'g1' });
		expect(urls).toEqual([]);

		await rerender({ open: true, gameId: 'g1' });

		expect(await screen.findByText('"Tetris"', {}, { timeout: 2000 })).toBeTruthy();
		expect(urls).toEqual(['/api/games/g1/info-json']);
		await fireEvent.click(screen.getByRole('button', { name: 'Copy info.json', hidden: true }));
		expect(copied).toEqual([raw]);
	});

	it('shows invalid JSON as raw text with a warning', async () => {
		answers.push(new Response('{ "title": ', { status: 200 }));
		render(InfoJsonModal, { open: true, gameId: 'g1' });

		expect(await screen.findByText(/isn't valid JSON/)).toBeTruthy();
		expect(document.querySelector('pre')!.textContent).toBe('{ "title": ');
	});

	it('shows the error with Retry, which fetches again', async () => {
		answers.push(
			new Response(JSON.stringify({ code: 'IoError', title: "The game's ZIP can't be read" }), {
				status: 500
			}),
			new Response('{"a":1}', { status: 200 })
		);
		render(InfoJsonModal, { open: true, gameId: 'g1' });

		await fireEvent.click(await screen.findByRole('button', { name: /Retry/, hidden: true }));

		expect(await screen.findByText('1')).toBeTruthy();
		expect(urls).toHaveLength(2);
	});
});
