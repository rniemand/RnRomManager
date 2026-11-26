import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { flushSync } from 'svelte';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock('#lib/api/client.js', () => ({ api }));

const { default: SuggestImagesModal } = await import('./SuggestImagesModal.svelte');

const suggestion = {
	previewUrl: '/p.png',
	imageUrl: 'https://igdb/x.png',
	gameName: 'Tetris',
	width: 264,
	height: 352
};

describe('SuggestImagesModal', () => {
	it('opening loads box art; a tile adds it, reports the detail and closes', async () => {
		api.get.mockResolvedValue({ suggestions: [suggestion] });
		api.post.mockResolvedValue({ id: 'g1' });
		const state = $state({ open: false });
		const onchanged = vi.fn();
		render(SuggestImagesModal, {
			gameId: 'g1',
			onchanged,
			onfailed: () => {},
			get open() {
				return state.open;
			},
			set open(v) {
				state.open = v;
			}
		});
		expect(api.get).not.toHaveBeenCalled();
		state.open = true;
		flushSync();
		expect(api.get.mock.calls[0][0]).toBe('/api/games/g1/image-suggestions?type=BoxArt');
		const tile = await screen.findByRole('button', { name: 'Add Tetris box art 1', hidden: true });
		expect(tile.className).toBe(
			'btn p-0 border card h-100 w-100 position-relative overflow-hidden'
		);
		await fireEvent.click(tile);
		await waitFor(() => expect(onchanged).toHaveBeenCalledWith({ id: 'g1' }));
		expect(api.post).toHaveBeenCalledWith('/api/games/g1/images/from-url', {
			url: 'https://igdb/x.png',
			type: 'BoxArt'
		});
		expect(state.open).toBe(false);
	});

	it('switching to Screenshot reloads', async () => {
		api.get.mockClear();
		api.get.mockResolvedValue({ suggestions: [] });
		const state = $state({ open: true });
		render(SuggestImagesModal, {
			gameId: 'g1',
			onchanged: () => {},
			onfailed: () => {},
			get open() {
				return state.open;
			},
			set open(v) {
				state.open = v;
			}
		});
		await screen.findByText('IGDB has no box art for this game.');
		await fireEvent.click(screen.getByRole('radio', { name: 'Screenshot', hidden: true }));
		expect(api.get.mock.calls.at(-1)![0]).toBe('/api/games/g1/image-suggestions?type=Snap');
		await screen.findByText('IGDB has no screenshots for this game.');
	});
});
