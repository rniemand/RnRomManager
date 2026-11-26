import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ReviewDetail } from '#lib/types/index.js';
import ReviewMatchedTab from './ReviewMatchedTab.svelte';

const item = (candidateGames: ReviewDetail['candidateGames']) =>
	({
		datIdentification: { romName: 'Tetris (World)', datName: 'No-Intro', datVersion: '2026' },
		candidateGames
	}) as ReviewDetail;

describe('ReviewMatchedTab', () => {
	it('links each candidate game and attaches through onattach', async () => {
		const onattach = vi.fn();
		const game = { id: 'g1', displayTitle: 'Tetris' };
		render(ReviewMatchedTab, { item: item([game]), disabled: false, onattach });

		expect(screen.getByText('Tetris (World)')).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Tetris' }).getAttribute('href')).toBe('/games/g1');
		await fireEvent.click(screen.getByRole('button', { name: 'Attach' }));
		expect(onattach).toHaveBeenCalledWith(game);
	});

	it('says when no candidate game is left', () => {
		render(ReviewMatchedTab, { item: item([]), disabled: false, onattach: vi.fn() });

		expect(
			screen.getByText('None of the candidate games exist any more. Run ingestion again.')
		).toBeTruthy();
	});
});
