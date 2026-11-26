import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ComponentProps } from 'svelte';
import type { DatSearchResult, ReviewDetail } from '#lib/types/index.js';
import ReviewSuggestionsTab from './ReviewSuggestionsTab.svelte';

const item = {
	system: 'gb',
	suggestions: [{ datGameName: 'Tetris', romName: 'Tetris (World).gb', score: 0.876 }]
} as ReviewDetail;

function setup(props: Partial<ComponentProps<typeof ReviewSuggestionsTab>> = {}) {
	const handlers = { onupdatechange: vi.fn(), onuse: vi.fn(), onsearch: vi.fn() };
	render(ReviewSuggestionsTab, {
		item,
		titleDb: false,
		asUpdate: false,
		disabled: false,
		next: 5,
		searchText: '',
		searchResults: null,
		searching: false,
		...handlers,
		...props
	});
	return handlers;
}

describe('ReviewSuggestionsTab', () => {
	it('lists the suggestions with their score and uses one, alone or with Next', async () => {
		const { onuse } = setup();

		expect(screen.getByText('88%')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Use' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Use and Next' }));
		expect(onuse.mock.calls).toEqual([['Tetris'], ['Tetris', true]]);
	});

	it('disables Use and Next when the queue has no next item', () => {
		setup({ next: 'none' });

		const button = screen.getByRole('button', { name: 'Use and Next' }) as HTMLButtonElement;
		expect(button.disabled).toBe(true);
		expect(button.title).toBe('No more items in the review queue.');
	});

	it('submits the DAT search and lists its results', async () => {
		const result = { romName: 'Tetris 2.gb', gameName: 'Tetris 2', sizeBytes: 10 };
		const { onsearch, onuse } = setup({ searchResults: [result as DatSearchResult] });

		await fireEvent.submit(screen.getByRole('search'));
		expect(onsearch).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getAllByRole('button', { name: 'Use' })[1]);
		expect(onuse).toHaveBeenCalledWith('Tetris 2');
	});

	it('offers the update checkbox for titledb items', async () => {
		const { onupdatechange } = setup({ titleDb: true });

		await fireEvent.click(screen.getByLabelText('This file is an update'));
		expect(onupdatechange).toHaveBeenCalledWith(true);
	});
});
