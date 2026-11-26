import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import type { ImageInfo } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: ViewImageModal } = await import('./ViewImageModal.svelte');

const image = {
	id: 3,
	type: 'BoxArt',
	fileName: 'cover.png',
	url: '/img/3',
	width: 300,
	height: 400
} as ImageInfo;

describe('ViewImageModal', () => {
	it('shows the image full size with its alt text', () => {
		render(ViewImageModal, { open: true, image, gameTitle: 'Tetris' });
		const img = document.querySelector('.modal-body img') as HTMLImageElement;
		expect(img.getAttribute('src')).toBe('/img/3');
		expect(img.alt).toBe('Tetris box art, full size');
		expect(screen.getByRole('heading', { name: 'cover.png', hidden: true })).toBeTruthy();
	});
});
