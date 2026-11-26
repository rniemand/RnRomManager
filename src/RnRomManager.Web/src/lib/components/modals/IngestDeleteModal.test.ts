import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { IngestDeleteEntry } from '#lib/types/index.js';
const entries = (n: number, folder = 'unmatched') =>
	Array.from({ length: n }, (_, i) => ({
		system: 'gb',
		name: `f${i}.zip`,
		folder
	})) as unknown as IngestDeleteEntry[];

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: IngestDeleteModal } = await import('./IngestDeleteModal.svelte');

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

describe('IngestDeleteModal', () => {
	it('warns about pending Review items only for pending files', () => {
		const { unmount } = render(IngestDeleteModal, {
			open: true,
			entries: entries(1),
			onconfirm: () => {}
		});
		expect(body().querySelector('p')?.textContent).toContain('This file is permanently deleted');
		expect(body().textContent).not.toContain('pending Review items');
		unmount();
		render(IngestDeleteModal, { open: true, entries: entries(2, 'pending'), onconfirm: () => {} });
		expect(body().querySelector('p')?.textContent).toContain('These files are');
		expect(body().textContent).toContain('Their pending Review items are deleted too.');
	});

	it('Delete calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ ...{ entries: entries(1) }, onconfirm });
		render(IngestDeleteModal, props);
		const button = screen.getByRole('button', { name: 'Delete', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
