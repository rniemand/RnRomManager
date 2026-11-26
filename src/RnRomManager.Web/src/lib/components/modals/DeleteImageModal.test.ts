import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ImageInfo } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: DeleteImageModal } = await import('./DeleteImageModal.svelte');

const image = {
	id: 3,
	type: 'BoxArt',
	fileName: 'cover.png',
	url: '/img/3',
	width: 300,
	height: 400
} as ImageInfo;

/** `extra` plus a bindable `open` (a spread would copy the getter's value and drop the setter). */
function bound<T extends object>(extra: T) {
	let open = true;
	const props = Object.defineProperty({ ...extra }, 'open', {
		get: () => open,
		set: (v: boolean) => (open = v),
		enumerable: true,
		configurable: true
	}) as T & { open: boolean };
	return { props, isOpen: () => open };
}

describe('DeleteImageModal', () => {
	it('names the image; Delete image calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ image, gameTitle: 'Tetris', onconfirm });
		render(DeleteImageModal, props);
		expect(document.querySelector('.modal-body p')?.textContent?.trim()).toBe(
			"Delete cover.png from Tetris? This can't be undone."
		);
		const del = screen.getByRole('button', { name: 'Delete image', hidden: true });
		expect(del.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(del);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
