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
		// J2/J3: warning, then a Copy-only toolbar, then the <pre> in the pane.
		const pane = document.querySelector('pre')!.parentElement!;
		expect(pane.className).toBe('border rounded bg-body-tertiary p-2 overflow-auto');
		expect((pane as HTMLElement).style.maxHeight).toBe('60vh');
		const copy = screen.getByRole('button', { name: 'Copy info.json', hidden: true });
		const alert = screen.getByRole('alert', { hidden: true });
		expect(alert.compareDocumentPosition(copy) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
		expect(pane.contains(copy)).toBe(false);
	});

	it('one toolbar row holds Expand all, Collapse all and Copy; the tree scrolls in its own pane (J1–J3)', async () => {
		answers.push(new Response('{ "a": { "b": { "c": 1 } } }', { status: 200 }));
		render(InfoJsonModal, { open: true, gameId: 'g1' });
		await screen.findByText('1', {}, { timeout: 2000 });

		const expand = screen.getByRole('button', { name: 'Expand all', hidden: true });
		const copy = screen.getByRole('button', { name: 'Copy info.json', hidden: true });
		const row = expand.parentElement!;
		expect(row.contains(screen.getByRole('button', { name: 'Collapse all', hidden: true }))).toBe(
			true
		);
		expect(row.contains(copy)).toBe(true);
		expect(copy.className).toBe('btn btn-sm btn-outline-secondary');
		expect(copy.textContent?.trim()).toBe('Copy');

		const pane = document.querySelector('.border.rounded.overflow-auto') as HTMLElement;
		expect((pane as HTMLElement).style.maxHeight).toBe('60vh');
		expect(pane.contains(row)).toBe(false);
		expect(pane.querySelector('details')).not.toBeNull();

		const details = () => [...document.querySelectorAll('details')].map((d) => d.open);
		await fireEvent.click(expand);
		expect(details().every(Boolean)).toBe(true);
		await fireEvent.click(screen.getByRole('button', { name: 'Collapse all', hidden: true }));
		expect(details().some(Boolean)).toBe(false);
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
