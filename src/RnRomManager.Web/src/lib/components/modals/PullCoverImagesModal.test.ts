import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: PullCoverImagesModal } = await import('./PullCoverImagesModal.svelte');

describe('PullCoverImagesModal', () => {
	it('names the count, singular and plural', () => {
		const { unmount } = render(PullCoverImagesModal, { open: true, count: 1, onconfirm: () => {} });
		expect(document.querySelector('.modal-body')?.textContent).toContain(
			'the cover of 1 game with its IGDB cover'
		);
		unmount();
		render(PullCoverImagesModal, { open: true, count: 1200, onconfirm: () => {} });
		expect(document.querySelector('.modal-body')?.textContent).toContain(
			'the cover of 1,200 games with their IGDB cover'
		);
	});

	it('confirm calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		let open = true;
		render(PullCoverImagesModal, {
			count: 2,
			onconfirm,
			get open() {
				return open;
			},
			set open(v) {
				open = v;
			}
		});
		await fireEvent.click(screen.getByRole('button', { name: 'Pull Cover Images', hidden: true }));
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(open).toBe(false);
	});
});
