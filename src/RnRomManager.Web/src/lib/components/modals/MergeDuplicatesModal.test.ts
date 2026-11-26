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

const { default: MergeDuplicatesModal } = await import('./MergeDuplicatesModal.svelte');

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

const body = () => document.querySelector('.modal-body')!;

describe('MergeDuplicatesModal', () => {
	it('names the count and the target', () => {
		const { unmount } = render(MergeDuplicatesModal, {
			open: true,
			count: 1,
			targetTitle: 'Tetris',
			onconfirm: () => {}
		});
		expect(body().textContent?.replace(/\s+/g, ' ').trim()).toBe(
			'Merge 1 game into Tetris? Their releases move into it and they are deleted.'
		);
		unmount();
		render(MergeDuplicatesModal, {
			open: true,
			count: 3,
			targetTitle: 'Tetris',
			onconfirm: () => {}
		});
		expect(body().textContent?.replace(/\s+/g, ' ')).toContain('Merge 3 games into');
	});

	it('shows no text while nothing is chosen', () => {
		render(MergeDuplicatesModal, {
			open: true,
			count: null,
			targetTitle: null,
			onconfirm: () => {}
		});
		expect(body().querySelector('p')).toBeNull();
	});

	it('Merge calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ ...{ count: 1, targetTitle: 'Tetris' }, onconfirm });
		render(MergeDuplicatesModal, props);
		const button = screen.getByRole('button', { name: 'Merge', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
