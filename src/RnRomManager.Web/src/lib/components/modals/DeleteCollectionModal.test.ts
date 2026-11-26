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

const { default: DeleteCollectionModal } = await import('./DeleteCollectionModal.svelte');

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

describe('DeleteCollectionModal', () => {
	it('names the collection', () => {
		render(DeleteCollectionModal, { open: true, name: 'Faves', onconfirm: () => {} });
		expect(screen.getByRole('heading', { name: 'Delete Faves?', hidden: true })).toBeTruthy();
		expect(body().querySelector('strong')?.textContent).toBe('Faves');
	});

	it('Delete calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ ...{ name: 'Faves' }, onconfirm });
		render(DeleteCollectionModal, props);
		const button = screen.getByRole('button', { name: 'Delete', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
