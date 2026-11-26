import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ReviewListItem } from '#lib/types/index.js';

const items = (n: number, isArchive = false) =>
	Array.from({ length: n }, (_, i) => ({
		id: i,
		fileName: `f${i}.zip`,
		isArchive
	})) as unknown as ReviewListItem[];

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: DeleteReviewItemsModal } = await import('./DeleteReviewItemsModal.svelte');

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

describe('DeleteReviewItemsModal', () => {
	it('counts and lists the items, and warns about archives', () => {
		const { unmount } = render(DeleteReviewItemsModal, {
			open: true,
			items: items(1),
			onconfirm: () => {}
		});
		expect(
			screen.getByRole('heading', { name: 'Delete 1 review file?', hidden: true })
		).toBeTruthy();
		expect(body().textContent).not.toContain('Archive entries');
		unmount();
		render(DeleteReviewItemsModal, { open: true, items: items(11, true), onconfirm: () => {} });
		expect(
			screen.getByRole('heading', { name: 'Delete 11 review files?', hidden: true })
		).toBeTruthy();
		expect(body().querySelectorAll('li')).toHaveLength(10);
		expect(body().textContent).toContain('… and 1 more');
		expect(body().querySelector('.text-danger')?.textContent).toContain('Archive entries delete');
	});

	it('Delete calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ ...{ items: items(1) }, onconfirm });
		render(DeleteReviewItemsModal, props);
		const button = screen.getByRole('button', { name: 'Delete', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
