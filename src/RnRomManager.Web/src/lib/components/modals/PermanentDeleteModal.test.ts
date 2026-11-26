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

const { default: PermanentDeleteModal } = await import('./PermanentDeleteModal.svelte');

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

describe('PermanentDeleteModal', () => {
	it('counts the files and lists the first 10', () => {
		render(PermanentDeleteModal, { open: true, entries: entries(12), onconfirm: () => {} });
		expect(
			screen.getByRole('heading', { name: 'Permanently delete 12 files?', hidden: true })
		).toBeTruthy();
		const items = body().querySelectorAll('li');
		expect(items).toHaveLength(11);
		expect(items[0].textContent).toBe('gb/f0.zip');
		expect(items[10].textContent).toBe('and 2 more');
	});

	it('Delete calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ ...{ entries: entries(1) }, onconfirm });
		render(PermanentDeleteModal, props);
		const button = screen.getByRole('button', { name: 'Delete', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
